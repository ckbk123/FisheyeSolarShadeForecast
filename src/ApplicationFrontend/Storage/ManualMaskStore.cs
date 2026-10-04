using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;
using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

public sealed record MaskVariant(string Id, string Label, DateTimeOffset CreatedUtc, string PhotoSha256,
    string PngSha256, string AiAncestorSha256, string? ParentId, SkyMaskResult BaseMask);

/// <summary>Immutable edited masks. IDs are safe path components; metadata and pixels are checked on every load.</summary>
public static class ManualMaskStore
{
    private static readonly Regex ValidId = new("^mask-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}$", RegexOptions.Compiled);
    private static string PhotoFolder(string photoHash)
    { CheckHash(photoHash); return Path.Combine(AppData.PathFor("Masks"), photoHash); }
    private static string AncestorPath(string photoHash, string ancestorHash)
    { CheckHash(ancestorHash); return Path.Combine(PhotoFolder(photoHash), "AI", ancestorHash, "sky-mask.png"); }
    public static string PhotoHash(string photoPath) => AppData.FileKey(PortablePaths.Resolve(photoPath));
    public static bool IsCompatible(MaskVariant variant, UserSettings settings) =>
        variant.BaseMask.Model == settings.Model && variant.BaseMask.InputSize == settings.Resolution &&
        string.Equals(variant.BaseMask.DiskMethod, settings.CenteredDisk ? "Centered" : "Auto", StringComparison.OrdinalIgnoreCase);
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
        string ancestor = AncestorPath(hash, variant.AiAncestorSha256);
        if (!File.Exists(ancestor) || AppData.FileKey(ancestor) != variant.AiAncestorSha256)
            throw new InvalidDataException("The immutable AI source mask is missing or changed.");
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
        string hash = PhotoHash(photoPath);
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100) throw new ArgumentException("Enter a mask name of 1–100 characters.");
        if (!Regex.IsMatch(aiAncestorHash, "^[A-F0-9]{64}$")) throw new ArgumentException("AI ancestor hash is invalid.");
        if (parentId != null) CheckId(parentId);
        Validate(png, baseMask);
        if (aiAncestorPng == null) aiAncestorPng = LoadAncestor(photoPath, aiAncestorHash);
        if (Convert.ToHexString(SHA256.HashData(aiAncestorPng)) != aiAncestorHash)
            throw new InvalidDataException("AI source mask bytes do not match the recorded hash.");
        Validate(aiAncestorPng, baseMask, enforceOutside: false);
        string ancestorPath = AncestorPath(hash, aiAncestorHash);
        if (File.Exists(ancestorPath))
        { if (AppData.FileKey(ancestorPath) != aiAncestorHash) throw new InvalidDataException("The immutable AI source mask has changed."); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ancestorPath)!);
            string ancestorTemp = ancestorPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            try { File.WriteAllBytes(ancestorTemp, aiAncestorPng); File.Move(ancestorTemp, ancestorPath); }
            finally { if (File.Exists(ancestorTemp)) File.Delete(ancestorTemp); }
        }
        string id = "mask-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..12];
        var variant = new MaskVariant(id, label.Trim(), DateTimeOffset.UtcNow, hash,
            Convert.ToHexString(SHA256.HashData(png)), aiAncestorHash, parentId, baseMask with { Png = [] });
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
            throw new InvalidDataException("Edited mask dimensions differ from the AI source.");
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
