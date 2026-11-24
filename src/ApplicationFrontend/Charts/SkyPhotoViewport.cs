using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.MaskEditor;
using Cv2 = OpenCvSharp.Cv2;
using ImreadModes = OpenCvSharp.ImreadModes;

namespace SolarShade.Desktop;

/// <summary>Photo, projected cardinals and FOV guides share a single native-pixel coordinate plane.</summary>
internal sealed class SkyPhotoViewport : ImageViewport
{
    private readonly FieldOfViewOverlay guide = new();
    private readonly CalibrationStamp stamp = new();
    internal ImageOverlayPreview Photo => Layers;
    internal bool HasNativePhoto { get; private set; }
    internal Geometry? EdgeOutline { get; private set; }
    private (double X, double Y, double? Radius)? edgeCoordinates;
    private string? sourcePath;
    private long photoRevision;
    private bool requestedDetail;

    internal SkyPhotoViewport() : base(new ImageOverlayPreview(), "SkyPhotoViewport", "Sky photo with compass and field of view")
    {
        Plane.Children.Add(guide);
        AutomationProperties.SetAutomationId(stamp, "SkyCalibrationStamp");
        Children.Add(stamp);
    }

    internal void SetPhoto(BitmapSource preview, int nativeWidth, int nativeHeight, string path)
    {
        sourcePath = path;
        photoRevision++;
        requestedDetail = false;
        HasNativePhoto = preview.PixelWidth == nativeWidth && preview.PixelHeight == nativeHeight;
        SetImage(preview, nativeWidth, nativeHeight);
        SetFieldOfView(null);
    }

    internal void ClearPhoto()
    {
        sourcePath = null;
        photoRevision++;
        requestedDetail = HasNativePhoto = false;
        ClearImage();
        SetFieldOfView(null);
    }

    internal void SetFieldOfView(SkyPhotoFieldOfView? view)
    {
        edgeCoordinates = null;
        EdgeOutline = view?.CalibratedOutline;
        guide.SetEdge(EdgeOutline);
        var warning = HasImage ? view?.StampLabel : null;
        stamp.SetMessage(warning, view?.Explanation);
        if (warning != null)
        {
            Photo.ClearOverlay();
            PositionStamp();
        }
        SetPreviewHelp(Gestures);
    }

    internal void SetEdge(double cx, double cy, double? radius)
    {
        if (edgeCoordinates == (cx, cy, radius)) return;
        edgeCoordinates = (cx, cy, radius);
        EllipseGeometry? circle = null;
        if (radius is double r)
        {
            circle = new EllipseGeometry(new Point(cx + .5, cy + .5), r, r);
            circle.Freeze();
        }
        EdgeOutline = circle;
        guide.SetEdge(circle);
    }

    private void SetPreviewHelp(string detail)
    {
        if (stamp.Visibility == Visibility.Visible)
            detail = AutomationProperties.GetName(stamp) + ". " + AutomationProperties.GetHelpText(stamp) + " " + detail;
        StudioTheme.Help(this, detail);
    }

    internal override void RequestDetail() => _ = RequestNativePhotoAsync();

    protected override void OnDisplayScaleChanged(double scale)
    {
        guide.DisplayScale = scale;
        if (stamp.Visibility == Visibility.Visible) PositionStamp();
    }

    // Anchor the warning to the visible part of the photo so a pan cannot carry it out of sight.
    private void PositionStamp()
    {
        Rect visible = Rect.Intersect(ImageBounds, new Rect(RenderSize));
        if (visible.IsEmpty) return;
        stamp.Width = visible.Width;
        stamp.Height = visible.Height;
        SetLeft(stamp, visible.X);
        SetTop(stamp, visible.Y);
    }

    // Defer the full-resolution decode until inspection; mouse moves and wheel ticks reuse the frozen image.
    internal async Task RequestNativePhotoAsync()
    {
        if (HasNativePhoto || requestedDetail || string.IsNullOrWhiteSpace(sourcePath)) return;
        requestedDetail = true;
        long revision = photoRevision;
        string path = sourcePath;
        int width = (int)Plane.Width, height = (int)Plane.Height;
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
                SetPreviewHelp("Showing the available sky photo preview. Full-resolution detail could not be loaded. " + ex.Message);
        }
    }
}
