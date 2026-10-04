using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SolarShade.MaskEditor;

/// <summary>A binary, native-pixel paint surface independent of the host application's results.</summary>
public sealed class MaskCanvas
{
    private readonly byte[] opening;
    private readonly byte[] pixels;
    private readonly Stack<Dictionary<int, byte>> undo = new();
    private readonly Stack<Dictionary<int, byte>> redo = new();
    private Dictionary<int, byte>? stroke;
    private readonly double centerX, centerY, radiusSquared;
    private int dirtyLeft, dirtyTop, dirtyRight, dirtyBottom;
    public int Width { get; }
    public int Height { get; }
    public bool IsDirty => !pixels.AsSpan().SequenceEqual(opening);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public MaskCanvas(int width, int height, byte[] binaryPixels, double diskX, double diskY, double diskRadius)
    {
        if (width <= 0 || height <= 0 || binaryPixels.Length != checked(width * height) ||
            binaryPixels.AsSpan().IndexOfAnyExcept((byte)0, (byte)255) >= 0)
            throw new ArgumentException("The opening mask must contain one black or white byte per image pixel.");
        if (!double.IsFinite(diskX) || !double.IsFinite(diskY) || !double.IsFinite(diskRadius) || diskRadius <= 0)
            throw new ArgumentException("The lens disk is invalid.");
        Width = width; Height = height; opening = (byte[])binaryPixels.Clone(); pixels = (byte[])binaryPixels.Clone();
        centerX = diskX; centerY = diskY; radiusSquared = diskRadius * diskRadius;
        dirtyLeft = dirtyTop = 0; dirtyRight = width - 1; dirtyBottom = height - 1;
        // Some older model exports retain white edge pixels outside the reported disk.
        // Keep their immutable source bytes in the host, but normalize this editable copy.
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (!Inside(x, y)) opening[y * width + x] = pixels[y * width + x] = 0;
    }

    private bool Inside(int x, int y)
    {
        double dx = x - centerX, dy = y - centerY;
        return dx * dx + dy * dy <= radiusSquared;
    }
    public byte Pixel(int x, int y) => pixels[checked(y * Width + x)];
    public byte[] CopyPixels() => (byte[])pixels.Clone();
    public (Int32Rect Rectangle, byte[] Pixels)? TakeDirtyRegion()
    {
        if (dirtyRight < dirtyLeft || dirtyBottom < dirtyTop) return null;
        int width = dirtyRight - dirtyLeft + 1, height = dirtyBottom - dirtyTop + 1;
        var region = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, (dirtyTop + y) * Width + dirtyLeft, region, y * width, width);
        var rectangle = new Int32Rect(dirtyLeft, dirtyTop, width, height);
        dirtyLeft = Width; dirtyTop = Height; dirtyRight = dirtyBottom = -1;
        return (rectangle, region);
    }
    private void MarkDirty(int x, int y)
    { dirtyLeft = Math.Min(dirtyLeft, x); dirtyTop = Math.Min(dirtyTop, y);
        dirtyRight = Math.Max(dirtyRight, x); dirtyBottom = Math.Max(dirtyBottom, y); }
    public void BeginStroke() { if (stroke != null) throw new InvalidOperationException("A stroke is already open."); stroke = []; }
    public void PaintLine(double x1, double y1, double x2, double y2, double diameter, byte value)
    {
        if (stroke == null) throw new InvalidOperationException("Begin a stroke before painting.");
        if (value is not (0 or 255) || !double.IsFinite(diameter) || diameter < 1 || diameter > 2048)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        double distance = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        int steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(0.5, diameter / 4)));
        for (int step = 0; step <= steps; step++)
        {
            double t = (double)step / steps;
            PaintDisk(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, diameter / 2, value);
        }
    }
    private void PaintDisk(double x, double y, double radius, byte value)
    {
        int left = Math.Max(0, (int)Math.Floor(x - radius)), right = Math.Min(Width - 1, (int)Math.Ceiling(x + radius));
        int top = Math.Max(0, (int)Math.Floor(y - radius)), bottom = Math.Min(Height - 1, (int)Math.Ceiling(y + radius));
        double radiusSquared = radius * radius;
        for (int yy = top; yy <= bottom; yy++)
            for (int xx = left; xx <= right; xx++)
            {
                double dx = xx - x, dy = yy - y;
                if (dx * dx + dy * dy > radiusSquared || !Inside(xx, yy)) continue;
                int index = yy * Width + xx;
                if (pixels[index] == value) continue;
                stroke!.TryAdd(index, pixels[index]); pixels[index] = value; MarkDirty(xx, yy);
            }
    }
    public void EndStroke()
    {
        if (stroke == null) return;
        if (stroke.Count > 0) { undo.Push(stroke); redo.Clear(); if (undo.Count > 100) TrimUndo(); }
        stroke = null;
    }
    private void TrimUndo()
    {
        var newest = undo.Take(100).ToArray(); undo.Clear();
        for (int i = newest.Length - 1; i >= 0; i--) undo.Push(newest[i]);
    }
    public void Undo() => Transfer(undo, redo);
    public void Redo() => Transfer(redo, undo);
    private void Transfer(Stack<Dictionary<int, byte>> from, Stack<Dictionary<int, byte>> to)
    {
        if (stroke != null) throw new InvalidOperationException("Finish the current stroke first.");
        if (from.Count == 0) return;
        var reverse = new Dictionary<int, byte>();
        foreach (var (index, previous) in from.Pop())
        { reverse[index] = pixels[index]; pixels[index] = previous; MarkDirty(index % Width, index / Width); }
        to.Push(reverse);
    }
    public void Reset()
    {
        if (stroke != null) throw new InvalidOperationException("Finish the current stroke first.");
        if (!IsDirty) return;
        var changes = new Dictionary<int, byte>();
        for (int i = 0; i < pixels.Length; i++)
            if (pixels[i] != opening[i]) { changes[i] = pixels[i]; pixels[i] = opening[i]; MarkDirty(i % Width, i / Width); }
        undo.Push(changes); redo.Clear();
    }
    public byte[] EncodePng()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8, null, pixels, Width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
}
