using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;
using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

public sealed record MaskVariant(string Id, string Label, DateTimeOffset CreatedUtc, string PhotoSha256,
    string PngSha256, string AiAncestorSha256, string? ParentId, SkyMaskResult BaseMask)
{
    public string Source { get; init; } = "Edited";
    public string? ImportedFileName { get; init; }
    public string? ImportedFileSha256 { get; init; }
    public string? ImportedAncestorSha256 { get; init; }
    public bool ImportInverted { get; init; }
    public bool IsImported => ImportedAncestorSha256 != null;
    public string AncestorSha256 => ImportedAncestorSha256 ?? AiAncestorSha256;
    public string Description => IsImported ? $"{Source} · {Label} · no AI" : $"Edited: {Label} · based on {BaseMask.Model}";
}

/// <summary>Immutable edited masks. IDs are safe path components; metadata and pixels are checked on every load.</summary>
public static class ManualMaskStore
{
    private static readonly Regex ValidId = new("^mask-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}$", RegexOptions.Compiled);
    private static string PhotoFolder(string photoHash)
    { CheckHash(photoHash); return Path.Combine(AppData.PathFor("Masks"), photoHash); }
    private static string AncestorPath(string photoHash, string ancestorHash, bool imported = false)
    { CheckHash(ancestorHash); return Path.Combine(PhotoFolder(photoHash), imported ? "Imported" : "AI", ancestorHash, "sky-mask.png"); }
    public static string PhotoHash(string photoPath) => AppData.FileKey(PortablePaths.Resolve(photoPath));
    public static string SourcePngPath(string photoHash, MaskVariant variant) => AncestorPath(photoHash, variant.AncestorSha256, variant.IsImported);
    public static bool IsCompatible(MaskVariant variant, UserSettings settings) =>
        variant.IsImported || (variant.BaseMask.Model == settings.Model && variant.BaseMask.InputSize == settings.Resolution &&
        string.Equals(variant.BaseMask.DiskMethod, settings.CenteredDisk ? "Centered" : "Auto", StringComparison.OrdinalIgnoreCase));
    public static string VariantPngPath(string photoHash, string id)
    {
        CheckId(id); return Path.Combine(PhotoFolder(photoHash), id, "sky-mask.png");
    }
    private static void CheckId(string id)
    { if (!ValidId.IsMatch(id)) throw new InvalidDataException("Invalid edited-mask ID."); }
    private static void CheckHash(string hash)
    { if (!Regex.IsMatch(hash, "^[A-F0-9]{64}$")) throw new InvalidDataException("Invalid mask source hash."); }
    public static IReadOnlyList<MaskVariant> List(string photoHash)
    {
        string folder = PhotoFolder(photoHash);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Where(p => ValidId.IsMatch(Path.GetFileName(p)))
            .Select(p => AppData.Read<MaskVariant>(Path.Combine(p, "variant.json")))
            .Where(v => v != null && v.PhotoSha256 == photoHash).Cast<MaskVariant>()
            .OrderByDescending(v => v.CreatedUtc).ToArray();
    }
    public static (MaskVariant Variant, byte[] Png) Load(string photoPath, string id)
    {
        string hash = PhotoHash(photoPath); CheckId(id);
        string folder = Path.Combine(PhotoFolder(hash), id);
        var variant = AppData.Read<MaskVariant>(Path.Combine(folder, "variant.json"))
            ?? throw new FileNotFoundException("Edited mask metadata is missing.", folder);
        if (variant.Id != id || variant.PhotoSha256 != hash || variant.BaseMask.Width != variant.BaseMask.Disk.ImageWidth ||
            variant.BaseMask.Height != variant.BaseMask.Disk.ImageHeight)
            throw new InvalidDataException("Edited mask metadata does not match this photo.");
        byte[] png = File.ReadAllBytes(Path.Combine(folder, "sky-mask.png"));
        if (Convert.ToHexString(SHA256.HashData(png)) != variant.PngSha256) throw new InvalidDataException("Edited mask pixels have changed.");
        string ancestor = AncestorPath(hash, variant.AncestorSha256, variant.IsImported);
        if (!File.Exists(ancestor) || AppData.FileKey(ancestor) != variant.AncestorSha256)
            throw new InvalidDataException("The immutable source mask is missing or changed.");
        Validate(png, variant.BaseMask);
        return (variant, png);
    }
    public static byte[] LoadAncestor(string photoPath, string hash)
    {
        byte[] png = File.ReadAllBytes(AncestorPath(PhotoHash(photoPath), hash));
        if (Convert.ToHexString(SHA256.HashData(png)) != hash)
            throw new InvalidDataException("The immutable AI source mask has changed.");
        return png;
    }
    public static MaskVariant Save(string photoPath, SkyMaskResult baseMask, string aiAncestorHash,
        string? parentId, string label, byte[] png, byte[]? aiAncestorPng = null)
    {
        // An edit of an imported mask retains its imported ancestor; no AI source is required.
        MaskVariant? importedParent = parentId == null ? null : Load(photoPath, parentId).Variant;
        if (importedParent?.IsImported == true)
            return Publish(photoPath, baseMask, label, png, importedParent.AncestorSha256, parentId,
                File.ReadAllBytes(AncestorPath(PhotoHash(photoPath), importedParent.AncestorSha256, true)),
                importedParent with { Source = "Edited" });
        aiAncestorPng ??= LoadAncestor(photoPath, aiAncestorHash);
        return Publish(photoPath, baseMask, label, png, aiAncestorHash, parentId, aiAncestorPng);
    }
    public static MaskVariant Import(string photoPath, string maskPath, string label, bool inverted = false,
        bool centeredDisk = false)
    {
        photoPath = PortablePaths.Resolve(photoPath); maskPath = PortablePaths.Resolve(maskPath);
        string photoHash = PhotoHash(photoPath), sourceHash = AppData.FileKey(maskPath);
        var mask = SkyMaskImporter.Import(photoPath, maskPath, inverted, centeredDisk ? DiskDetection.Centered : DiskDetection.Auto);
        if (PhotoHash(photoPath) != photoHash || AppData.FileKey(maskPath) != sourceHash)
            throw new IOException("The photo or mask changed during import. Choose the file again.");
        string ancestorHash = Convert.ToHexString(SHA256.HashData(mask.Png));
        var imported = new MaskVariant("", label, DateTimeOffset.UtcNow, photoHash, ancestorHash, "", null, mask)
        { Source = "Imported", ImportedFileName = Path.GetFileName(maskPath), ImportedFileSha256 = sourceHash,
            ImportedAncestorSha256 = ancestorHash, ImportInverted = inverted };
        return Publish(photoPath, mask, label, mask.Png, ancestorHash, null, mask.Png, imported);
    }
    private static MaskVariant Publish(string photoPath, SkyMaskResult baseMask, string label, byte[] png,
        string ancestorHash, string? parentId, byte[] ancestorPng, MaskVariant? imported = null)
    {
        string hash = PhotoHash(photoPath);
        if (imported != null && imported.PhotoSha256 != hash)
            throw new IOException("The photograph changed while saving the mask. Choose the photo again.");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100) throw new ArgumentException("Enter a mask name of 1–100 characters.");
        CheckHash(ancestorHash);
        if (parentId != null) CheckId(parentId);
        Validate(png, baseMask);
        if (Convert.ToHexString(SHA256.HashData(ancestorPng)) != ancestorHash)
            throw new InvalidDataException("Source mask bytes do not match the recorded hash.");
        Validate(ancestorPng, baseMask, enforceOutside: false);
        string ancestorPath = AncestorPath(hash, ancestorHash, imported != null);
        if (File.Exists(ancestorPath))
        { if (AppData.FileKey(ancestorPath) != ancestorHash) throw new InvalidDataException("The immutable source mask has changed."); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ancestorPath)!);
            string ancestorTemp = ancestorPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            try { File.WriteAllBytes(ancestorTemp, ancestorPng); File.Move(ancestorTemp, ancestorPath); }
            finally { if (File.Exists(ancestorTemp)) File.Delete(ancestorTemp); }
        }
        string id = "mask-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..12];
        var variant = (imported ?? new MaskVariant("", "", default, "", "", ancestorHash, null, baseMask)) with
        { Id = id, Label = label.Trim(), CreatedUtc = DateTimeOffset.UtcNow, PhotoSha256 = hash,
            PngSha256 = Convert.ToHexString(SHA256.HashData(png)), ParentId = parentId, BaseMask = baseMask with { Png = [] } };
        string parent = PhotoFolder(hash); Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, "." + id + ".partial");
        string destination = Path.Combine(parent, id);
        try
        {
            Directory.CreateDirectory(temporary);
            File.WriteAllBytes(Path.Combine(temporary, "sky-mask.png"), png);
            AppData.Write(Path.Combine(temporary, "variant.json"), variant);
            // Validate the complete pair before its directory becomes visible to List.
            if (AppData.FileKey(Path.Combine(temporary, "sky-mask.png")) != variant.PngSha256)
                throw new IOException("Edited-mask write verification failed.");
            Directory.Move(temporary, destination);
            return variant;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private static void Validate(byte[] png, SkyMaskResult metadata, bool enforceOutside = true)
    {
        using var decoded = Cv2.ImDecode(png, ImreadModes.Grayscale);
        if (decoded.Empty() || decoded.Width != metadata.Width || decoded.Height != metadata.Height)
            throw new InvalidDataException("Mask dimensions differ from the source photo.");
        var disk = metadata.Disk;
        int width = decoded.Width, height = decoded.Height;
        var rowBytes = new byte[width];
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(decoded.Ptr(y), rowBytes, 0, width);
            var row = rowBytes.AsSpan();
            if (row.IndexOfAnyExcept((byte)0, (byte)255) >= 0)
                throw new InvalidDataException("Edited mask pixels must be black or white.");
            for (int x = 0; x < width; x++)
            {
                double dx = x - disk.CenterX, dy = y - disk.CenterY;
                if (enforceOutside && dx * dx + dy * dy > disk.Radius * disk.Radius && row[x] != 0)
                    throw new InvalidDataException("Edited mask cannot be white outside the lens disk.");
            }
        }
    }
}
