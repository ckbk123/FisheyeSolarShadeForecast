using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.MaskEditor;
using Cv2 = OpenCvSharp.Cv2;
using ImreadModes = OpenCvSharp.ImreadModes;

namespace SolarShade.Desktop;

/// <summary>Photo, projected cardinals and FOV guides share a single native-pixel coordinate plane.</summary>
internal sealed class SkyPhotoViewport : Canvas
{
    private readonly Grid plane = new() { ClipToBounds = true, UseLayoutRounding = false };
    private readonly MatrixTransform transform = new();
    private readonly FieldOfViewOverlay guide = new();
    internal ImageOverlayPreview Photo { get; } = new();
    internal double Zoom { get; private set; } = 1;
    internal Rect ImageBounds { get; private set; } = Rect.Empty;
    internal bool HasNativePhoto { get; private set; }
    internal bool HasCalibratedOutline { get; private set; }
    internal bool HasPhoto => plane.Width > 0;
    internal event Action? ViewChanged;
    private double fitScale;
    private Vector offset;
    private Point? lastDrag;
    private string? sourcePath;
    private long photoRevision;
    private bool requestedDetail;
    private const string Gestures = "Scroll to zoom around the pointer. Drag to pan. Use + or − and the arrow keys with the photo focused. Home fits the whole photo.";

    internal SkyPhotoViewport()
    {
        ClipToBounds = true;
        Background = StudioTheme.Canvas;
        Focusable = true;
        UseLayoutRounding = false;
        AutomationProperties.SetAutomationId(this, "SkyPhotoViewport");
        AutomationProperties.SetName(this, "Sky photo with compass and field of view");
        StudioTheme.Help(this, Gestures);
        plane.RenderTransform = transform;
        plane.Children.Add(Photo);
        plane.Children.Add(guide);
        Children.Add(plane);
        SizeChanged += (_, _) => UpdateTransform();
        LostMouseCapture += (_, _) => lastDrag = null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    internal void SetPhoto(BitmapSource preview, int nativeWidth, int nativeHeight, string path)
    {
        Photo.SetImage(preview, nativeWidth, nativeHeight);
        plane.Width = nativeWidth;
        plane.Height = nativeHeight;
        sourcePath = path;
        photoRevision++;
        requestedDetail = false;
        StudioTheme.Help(this, Gestures);
        HasNativePhoto = preview.PixelWidth == nativeWidth && preview.PixelHeight == nativeHeight;
        SetFieldOfView(null);
        Fit();
    }

    internal void ClearPhoto()
    {
        sourcePath = null;
        photoRevision++;
        requestedDetail = HasNativePhoto = false;
        Photo.ClearImage();
        plane.Width = plane.Height = 0;
        SetFieldOfView(null);
        Fit();
    }

    internal void SetFieldOfView(SkyPhotoFieldOfView? view)
    {
        HasCalibratedOutline = view?.CalibratedOutline != null;
        guide.SetOutlines(view?.CalibratedOutline, view?.UsableOutline);
    }

    internal void Fit()
    {
        Zoom = 1;
        offset = default;
        UpdateTransform();
    }

    internal void ZoomAt(Point anchor, double factor)
    {
        if (ImageBounds.IsEmpty || !(factor > 0) || !double.IsFinite(factor)) return;
        double next = Math.Clamp(Zoom * factor, 1, Math.Max(8, 2 / fitScale));
        if (next == Zoom) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        Vector relative = anchor - center;
        offset = relative + (offset - relative) * (next / Zoom);
        Zoom = next;
        UpdateTransform();
        if (Zoom > 1) _ = RequestNativePhotoAsync();
    }

    internal void PanBy(Vector distance)
    {
        if (Zoom <= 1 || ImageBounds.IsEmpty) return;
        offset += distance;
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        if (!HasPhoto || ActualWidth <= 0 || ActualHeight <= 0)
        {
            ImageBounds = Rect.Empty;
            fitScale = 0;
        }
        else
        {
            double nextFit = Math.Min(ActualWidth / plane.Width, ActualHeight / plane.Height);
            if (fitScale > 0 && nextFit != fitScale) offset *= nextFit / fitScale;
            fitScale = nextFit;
            double scale = Zoom * fitScale;
            double width = plane.Width * scale, height = plane.Height * scale;
            double maxX = Math.Max(0, (width - ActualWidth) / 2);
            double maxY = Math.Max(0, (height - ActualHeight) / 2);
            offset = new(Math.Clamp(offset.X, -maxX, maxX), Math.Clamp(offset.Y, -maxY, maxY));
            ImageBounds = new((ActualWidth - width) / 2 + offset.X, (ActualHeight - height) / 2 + offset.Y, width, height);
            transform.Matrix = new(scale, 0, 0, scale, ImageBounds.X, ImageBounds.Y);
            guide.DisplayScale = scale;
        }
        Cursor = Zoom > 1 ? Cursors.Hand : Cursors.Arrow;
        ViewChanged?.Invoke();
    }

    // Defer the full-resolution decode until inspection; mouse moves and wheel ticks reuse the frozen image.
    internal async Task RequestNativePhotoAsync()
    {
        if (HasNativePhoto || requestedDetail || string.IsNullOrWhiteSpace(sourcePath)) return;
        requestedDetail = true;
        long revision = photoRevision;
        string path = sourcePath;
        int width = (int)plane.Width, height = (int)plane.Height;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                // Use the same EXIF orientation and BGR decode as masking and projection.
                using var pixels = Cv2.ImRead(path, ImreadModes.Color);
                if (pixels.Empty() || pixels.Width != width || pixels.Height != height)
                    throw new InvalidDataException("The selected file no longer matches the displayed sky photo.");
                int stride = checked((int)pixels.Step());
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null,
                    pixels.Data, checked(stride * height), stride);
                image.Freeze();
                return image;
            });
            if (revision != photoRevision) return;
            Photo.SetNativePhoto(bitmap);
            HasNativePhoto = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (revision == photoRevision)
                StudioTheme.Help(this, "Showing the available sky photo preview. Full-resolution detail could not be loaded. " + ex.Message);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasPhoto || e.Delta == 0 || (Zoom == 1 && e.Delta < 0)) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.25, e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasPhoto) return;
        Focus();
        if (e.ClickCount == 2) { if (Zoom > 1) Fit(); else ZoomAt(e.GetPosition(this), 2); }
        else if (Zoom > 1 && CaptureMouse()) lastDrag = e.GetPosition(this);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (lastDrag is not { } previous || e.LeftButton != MouseButtonState.Pressed) return;
        Point position = e.GetPosition(this);
        PanBy(position - previous);
        lastDrag = position;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!HasPhoto) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: ZoomAt(center, 1.5); break;
            case Key.Subtract: case Key.OemMinus: ZoomAt(center, 1 / 1.5); break;
            case Key.Home: Fit(); break;
            case Key.Left: PanBy(new(32, 0)); break;
            case Key.Right: PanBy(new(-32, 0)); break;
            case Key.Up: PanBy(new(0, 32)); break;
            case Key.Down: PanBy(new(0, -32)); break;
            default: return;
        }
        e.Handled = true;
    }
}
