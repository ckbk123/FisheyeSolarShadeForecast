using System.Windows;
using System.Windows.Media;

namespace SolarShade.MaskEditor;

/// <summary>Display-only FOV contours in the oriented photograph's native pixel coordinates.</summary>
public sealed class FieldOfViewOverlay : FrameworkElement
{
    private static readonly Brush Calibrated = FrozenBrush(Color.FromRgb(0x55, 0xe8, 0x95));
    private static readonly Brush Usable = FrozenBrush(Color.FromRgb(0xb4, 0xff, 0xc0));
    private static readonly Brush Halo = FrozenBrush(Color.FromArgb(215, 14, 40, 28));
    private static readonly Brush MaskEdge = FrozenBrush(Color.FromRgb(0xff, 0xbb, 0x32));
    private static readonly Brush ScanCap = FrozenBrush(Color.FromRgb(0x78, 0xc6, 0xff));
    private Geometry? calibrated, usable, maskTransition, scanCap;
    private double displayScale = 1;

    public FieldOfViewOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public double DisplayScale
    {
        get => displayScale;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || displayScale == value) return;
            displayScale = value;
            InvalidateVisual();
        }
    }

    public void SetOutlines(Geometry? calibrated, Geometry? usable)
    {
        this.calibrated = calibrated;
        this.usable = usable;
        InvalidateVisual();
    }

    public void SetMaskScan(Geometry? transition, Geometry? cap)
    {
        maskTransition = transition; scanCap = cap;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        Draw(calibrated, Calibrated, false);
        Draw(usable, Usable, true);
        Draw(scanCap, ScanCap, true);
        Draw(maskTransition, MaskEdge, true);

        void Draw(Geometry? geometry, Brush brush, bool dashed)
        {
            if (geometry == null || geometry.IsEmpty()) return;
            foreach (var (ink, thickness) in new[] { (Halo, 3.5), (brush, 1.8) })
            {
                var pen = new Pen(ink, thickness / displayScale);
                // Keep both strokes' dash endpoints aligned regardless of their widths or the zoom.
                if (dashed) pen.DashStyle = new DashStyle([7 / thickness, 5 / thickness], 0);
                pen.Freeze();
                drawing.DrawGeometry(null, pen, geometry);
            }
        }
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
