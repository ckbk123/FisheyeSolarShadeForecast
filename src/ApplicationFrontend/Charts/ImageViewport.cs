using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SolarShade.Desktop;

/// <summary>Fits, zooms and pans one native-pixel plane for an image and its registered overlays.</summary>
internal abstract class ImageViewport : Canvas
{
    protected Grid Plane { get; } = new() { ClipToBounds = true, UseLayoutRounding = false };
    private readonly MatrixTransform transform = new();
    internal ImageOverlayPreview Layers { get; }
    internal double Zoom { get; private set; } = 1;
    internal Rect ImageBounds { get; private set; } = Rect.Empty;
    internal bool HasImage => Plane.Width > 0;
    internal event Action? ViewChanged;
    private double fitScale;
    private Vector offset;
    private Point? lastDrag;
    protected const string Gestures = "Scroll to zoom around the pointer. Drag to pan. With the preview focused, use + or − to zoom and the arrow keys to pan. Home fits the whole image.";

    internal ImageViewport(ImageOverlayPreview layers, string id, string name)
    {
        Layers = layers;
        ClipToBounds = true;
        Background = StudioTheme.Canvas;
        Focusable = true;
        UseLayoutRounding = false;
        AutomationProperties.SetAutomationId(this, id);
        AutomationProperties.SetName(this, name);
        StudioTheme.Help(this, Gestures);
        Plane.RenderTransform = transform;
        Plane.Children.Add(layers);
        Children.Add(Plane);
        SizeChanged += (_, _) => UpdateTransform();
        LostMouseCapture += (_, _) => lastDrag = null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    internal void SetImage(BitmapSource bitmap, int nativeWidth, int nativeHeight)
    {
        Layers.SetImage(bitmap, nativeWidth, nativeHeight);
        Plane.Width = nativeWidth;
        Plane.Height = nativeHeight;
        Fit();
    }

    internal void ClearImage()
    {
        Layers.ClearImage();
        Plane.Width = Plane.Height = 0;
        Fit();
    }

    internal virtual void RequestDetail() { }
    protected abstract void OnDisplayScaleChanged(double scale);

    internal void Fit()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
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
        if (Zoom > 1) RequestDetail();
    }

    internal void PanBy(Vector distance)
    {
        if (Zoom <= 1 || ImageBounds.IsEmpty) return;
        offset += distance;
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        if (!HasImage || ActualWidth <= 0 || ActualHeight <= 0)
        {
            ImageBounds = Rect.Empty;
            fitScale = 0;
        }
        else
        {
            double nextFit = Math.Min(ActualWidth / Plane.Width, ActualHeight / Plane.Height);
            if (fitScale > 0 && nextFit != fitScale) offset *= nextFit / fitScale;
            fitScale = nextFit;
            double scale = Zoom * fitScale;
            double width = Plane.Width * scale, height = Plane.Height * scale;
            double maxX = Math.Max(0, (width - ActualWidth) / 2);
            double maxY = Math.Max(0, (height - ActualHeight) / 2);
            offset = new(Math.Clamp(offset.X, -maxX, maxX), Math.Clamp(offset.Y, -maxY, maxY));
            ImageBounds = new((ActualWidth - width) / 2 + offset.X, (ActualHeight - height) / 2 + offset.Y, width, height);
            transform.Matrix = new(scale, 0, 0, scale, ImageBounds.X, ImageBounds.Y);
            OnDisplayScaleChanged(scale);
        }
        Cursor = Zoom > 1 ? Cursors.Hand : Cursors.Arrow;
        ViewChanged?.Invoke();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasImage || e.Delta == 0 || (Zoom == 1 && e.Delta < 0)) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.25, e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasImage) return;
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
        if (!HasImage) return;
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

internal sealed class MaskViewport() : ImageViewport(new MaskOverlayPreview(), "SkyMaskViewport", "Sky mask with sun path")
{
    // Inspect individual binary pixels when magnified, keeping the sun-path layer antialiased.
    protected override void OnDisplayScaleChanged(double scale) =>
        Layers.SetImageScalingMode(scale > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
}
