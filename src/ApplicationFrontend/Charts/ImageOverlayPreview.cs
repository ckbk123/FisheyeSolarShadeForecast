using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace SolarShade.Desktop;

/// <summary>Presentation-only layering: both native rasters use exactly the same transform and letterboxing.</summary>
public class ImageOverlayPreview : Viewbox
{
    private readonly Grid surface = new() { ClipToBounds = true, UseLayoutRounding = false };
    private readonly Image mask = new() { Stretch = Stretch.Fill, UseLayoutRounding = false };
    private readonly Image overlay = new() { Stretch = Stretch.Fill, UseLayoutRounding = false, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private bool showOverlay = true;
    public ImageOverlayPreview()
    {
        Stretch = Stretch.Uniform; StretchDirection = StretchDirection.Both;
        surface.Children.Add(mask); surface.Children.Add(overlay); Child = surface;
    }
    public bool ShowOverlay
    { get => showOverlay; set { showOverlay = value; overlay.Visibility = value && overlay.Source != null ? Visibility.Visible : Visibility.Collapsed; } }
    public bool OverlayVisible => overlay.Source != null && overlay.Visibility == Visibility.Visible;
    public void SetMask(BitmapSource bitmap) => SetImage(bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
    public void SetImage(BitmapSource bitmap, int nativeWidth, int nativeHeight)
    {
        if (nativeWidth <= 0 || nativeHeight <= 0) throw new ArgumentOutOfRangeException(nameof(nativeWidth));
        if (ReferenceEquals(mask.Source, bitmap) && surface.Width == nativeWidth && surface.Height == nativeHeight) return;
        surface.Width = nativeWidth; surface.Height = nativeHeight; mask.Source = bitmap; ClearOverlay();
    }
    public void SetOverlay(BitmapSource bitmap)
    {
        if (mask.Source == null || bitmap.PixelWidth != surface.Width || bitmap.PixelHeight != surface.Height)
            throw new ArgumentException("Overlay dimensions must match the native image coordinate space.");
        overlay.Source = bitmap; ShowOverlay = showOverlay;
    }
    public void ClearOverlay() { overlay.Source = null; overlay.Visibility = Visibility.Collapsed; }
    public void ClearImage() { mask.Source = null; ClearOverlay(); }
}

public sealed class MaskOverlayPreview : ImageOverlayPreview { }

public sealed class UniformGridCompat : System.Windows.Controls.Primitives.UniformGrid
{ public UniformGridCompat(int columns) { Columns = columns; Rows = 1; } }
