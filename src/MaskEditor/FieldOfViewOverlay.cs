using System.Windows;
using System.Windows.Media;

namespace SolarShade.MaskEditor;

/// <summary>Display-only FOV contours in the oriented photograph's native pixel coordinates.</summary>
public sealed class FieldOfViewOverlay : FrameworkElement
{
    private static readonly Brush Green = FrozenBrush(Color.FromRgb(0x55, 0xe8, 0x95));
    private static readonly Brush Halo = FrozenBrush(Color.FromArgb(215, 14, 40, 28));
    private Geometry? edge;
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

    public void SetEdge(Geometry? geometry)
    {
        if (ReferenceEquals(edge, geometry)) return;
        edge = geometry;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (edge == null || edge.IsEmpty()) return;
        foreach (var (ink, thickness) in new[] { (Halo, 3.5), (Green, 1.8) })
        {
            var pen = new Pen(ink, thickness / displayScale);
            pen.Freeze();
            drawing.DrawGeometry(null, pen, edge);
        }
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
