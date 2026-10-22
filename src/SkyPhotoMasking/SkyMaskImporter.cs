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
        using var source = Cv2.ImRead(maskPath, ImreadModes.Unchanged);
        if (source.Empty()) throw new InvalidDataException("The mask could not be decoded. Choose a black-and-white PNG.");
        if (source.Width != photo.Width || source.Height != photo.Height)
            throw new InvalidDataException($"Mask dimensions {source.Width} × {source.Height} must match the oriented photo ({photo.Width} × {photo.Height}). Do not resize the mask.");
        if (source.Depth() != MatType.CV_8U || source.Channels() is not (1 or 3 or 4))
            throw new InvalidDataException("The mask must contain 8-bit black (0) and white (255) pixels.");
        var disk = SkyPhotoMasker.FindDisk(photo, diskDetection);
        SkyPhotoMasker.ValidateDisk(disk, photo.Width, photo.Height);
        int channels = source.Channels(), width = source.Width, height = source.Height;
        using var binary = new Mat(height, width, MatType.CV_8UC1);
        var sourceRow = new byte[checked(width * channels)];
        var binaryRow = new byte[width];
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(source.Ptr(y), sourceRow, 0, sourceRow.Length);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                byte value = sourceRow[offset];
                if (value is not (0 or 255) || (channels > 1 && (sourceRow[offset + 1] != value || sourceRow[offset + 2] != value)))
                    throw new InvalidDataException("The mask must be grayscale and exactly black or white. Gray edges and colored pixels are not accepted.");
                if (channels == 4 && sourceRow[offset + 3] != 255)
                    throw new InvalidDataException("The mask must be opaque. Remove transparent pixels before importing.");
                // Outside-lens pixels have no sky meaning. Apply the photo disk to either polarity.
                double dx = x - disk.CenterX, dy = y - disk.CenterY;
                binaryRow[x] = dx * dx + dy * dy > disk.Radius * disk.Radius ? (byte)0
                    : inverted ? (byte)(255 - value) : value;
            }
            System.Runtime.InteropServices.Marshal.Copy(binaryRow, 0, binary.Ptr(y), binaryRow.Length);
        }
        Cv2.ImEncode(".png", binary, out byte[] png);
        return new(png, default, photo.Width, photo.Height, 0, disk, SkyPhotoMasker.CropForDisk(disk),
            diskDetection.ToString(), "Imported binary mask · no AI", null, 0, 0, 0, null, null);
    }
}
