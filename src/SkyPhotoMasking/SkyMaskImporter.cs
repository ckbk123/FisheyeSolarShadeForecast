using OpenCvSharp;

namespace SolarShade.SkyPhotoMasking;

/// <summary>Imports existing binary pixels without loading a segmentation model.
/// Lens geometry is measured from the photograph, never from the mask's open-sky region.</summary>
public static class SkyMaskImporter
{
    public static SkyMaskResult Import(string photoPath, string maskPath, bool inverted = false,
        DiskDetection diskDetection = DiskDetection.Auto)
    {
        using var photo = Cv2.ImRead(photoPath, ImreadModes.Color);
        if (photo.Empty()) throw new InvalidDataException("The sky photograph could not be decoded.");
        // Decode both images in display orientation. OpenCV converts color and bit depth to
        // 8-bit luminance here. Alpha is deliberately ignored, as documented in the importer.
        using var source = Cv2.ImRead(maskPath, ImreadModes.Grayscale);
        if (source.Empty()) throw new InvalidDataException("The mask image could not be decoded.");
        if (source.Size() != photo.Size())
            throw new InvalidDataException($"Mask dimensions {source.Width} × {source.Height} must match the oriented photo ({photo.Width} × {photo.Height}).");
        using var binary = new Mat();
        Cv2.Threshold(source, binary, 127, 255, inverted ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

        // Disk geometry is a hint for the viewer. It must never crop a user-supplied mask.
        LensDisk disk;
        if (Math.Min(photo.Height, photo.Width) < 33) diskDetection = DiskDetection.Centered;
        try { disk = SkyPhotoMasker.FindDisk(photo, diskDetection); }
        catch (InvalidDataException) when (diskDetection == DiskDetection.Auto)
        {
            diskDetection = DiskDetection.Centered;
            disk = SkyPhotoMasker.FindDisk(photo, diskDetection);
        }
        Cv2.ImEncode(".png", binary, out byte[] png);
        return new(png, default, photo.Width, photo.Height, 0, disk, new(0, 0, photo.Width, photo.Height),
            diskDetection.ToString(), "Imported binary mask · no AI", null, 0, 0, 0, null, null);
    }

    internal static void CopyBinaryPixels(Mat source, Mat binary,
        CancellationToken ct = default)
    {
        int channels = source.Channels(), width = source.Width, height = source.Height;
        var sourceRow = new byte[checked(width * channels)];
        var binaryRow = new byte[width];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            System.Runtime.InteropServices.Marshal.Copy(source.Ptr(y), sourceRow, 0, sourceRow.Length);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                byte value = sourceRow[offset];
                if (value is not (0 or 255) || (channels > 1 && (sourceRow[offset + 1] != value || sourceRow[offset + 2] != value)))
                    throw new InvalidDataException("The mask must be grayscale and exactly black or white. Gray edges and colored pixels are not accepted.");
                if (channels == 4 && sourceRow[offset + 3] != 255)
                    throw new InvalidDataException("The mask must be opaque. Remove transparent pixels before importing.");
                binaryRow[x] = value;
            }
            System.Runtime.InteropServices.Marshal.Copy(binaryRow, 0, binary.Ptr(y), binaryRow.Length);
        }
    }
}
