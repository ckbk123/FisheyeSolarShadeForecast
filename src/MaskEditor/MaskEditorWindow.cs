using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("APPLICATION")]

namespace SolarShade.MaskEditor;

public sealed record MaskEditorRequest(byte[] OrientedPhotoPng, byte[] OpeningMaskPng,
    double DiskX, double DiskY, double DiskRadius, string SuggestedName,
    Func<byte[], string, bool> Save);

/// <summary>A modal paint window. Its host owns source checks, persistence and result state.</summary>
public sealed class MaskEditorWindow : Window
{
    private readonly MaskCanvas document;
    private readonly Image maskImage;
    private readonly WriteableBitmap maskBitmap;
    private readonly Grid canvas;
    private readonly Ellipse brushCursor;
    private readonly ScrollViewer scroll;
    private readonly Slider zoom, opacity, brush;
    private readonly TextBox name;
    private readonly RadioButton black, white;
    private readonly Func<byte[], string, bool> save;
    private Point last, panPoint, cursorPoint;
    private Point? zoomAnchor;
    private bool painting, panning, saved;

    public MaskEditorWindow(MaskEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        save = request.Save ?? throw new ArgumentNullException(nameof(request.Save));
        var photoBitmap = Decode(request.OrientedPhotoPng);
        var openingBitmap = Decode(request.OpeningMaskPng);
        if (photoBitmap.PixelWidth != openingBitmap.PixelWidth || photoBitmap.PixelHeight != openingBitmap.PixelHeight)
            throw new InvalidDataException("The original photo and opening mask have different dimensions.");
        int width = openingBitmap.PixelWidth, height = openingBitmap.PixelHeight;
        var opening = new byte[checked(width * height)];
        var gray = new FormatConvertedBitmap(openingBitmap, PixelFormats.Gray8, null, 0);
        gray.CopyPixels(opening, width, 0);
        document = new(width, height, opening, request.DiskX, request.DiskY, request.DiskRadius);
        maskBitmap = new(width, height, 96, 96, PixelFormats.Gray8, null);
        maskImage = new() { Source = maskBitmap, Width = width, Height = height, Stretch = Stretch.None, Opacity = .55,
            IsHitTestVisible = false };
        Title = "Edit sky mask"; Width = 1200; Height = 850; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(26, 39, 46));
        Foreground = Brushes.White;

        var root = new DockPanel(); Content = root;
        var top = new WrapPanel { Margin = new(12, 10, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        black = new RadioButton { Content = "Black · obstruction", IsChecked = true, Margin = new(4, 7, 16, 7), GroupName = "paint", Foreground = Brushes.White };
        white = new RadioButton { Content = "White · open sky", Margin = new(4, 7, 18, 7), GroupName = "paint", Foreground = Brushes.White };
        top.Children.Add(black); top.Children.Add(white);
        top.Children.Add(Label("Brush (px)")); brush = Slider(1, 300, 24, 145); top.Children.Add(brush);
        var brushText = Label("24 px"); top.Children.Add(brushText);
        brush.ValueChanged += (_, _) => brushText.Text = $"{brush.Value:0} px";
        top.Children.Add(Label("Mask opacity")); opacity = Slider(0, 100, 55, 120); top.Children.Add(opacity);
        var opacityText = Label("55%"); top.Children.Add(opacityText);
        opacity.ValueChanged += (_, _) => { maskImage.Opacity = opacity.Value / 100; opacityText.Text = $"{opacity.Value:0}%"; };
        top.Children.Add(Label("Zoom")); zoom = Slider(5, 800, 100, 140); top.Children.Add(zoom);
        var zoomText = Label("100%"); top.Children.Add(zoomText);
        zoom.ValueChanged += (_, e) =>
        {
            if (canvas != null && scroll != null)
            {
                double oldScale = e.OldValue > 0 ? e.OldValue / 100 : 1, newScale = zoom.Value / 100;
                Point anchor = zoomAnchor ?? new(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
                double nativeX = (scroll.HorizontalOffset + anchor.X) / oldScale;
                double nativeY = (scroll.VerticalOffset + anchor.Y) / oldScale;
                canvas.LayoutTransform = new ScaleTransform(newScale, newScale);
                scroll.UpdateLayout();
                scroll.ScrollToHorizontalOffset(nativeX * newScale - anchor.X);
                scroll.ScrollToVerticalOffset(nativeY * newScale - anchor.Y);
            }
            zoomText.Text = $"{zoom.Value:0}%";
        };
        top.Children.Add(Button("Fit image", FitImage));
        top.Children.Add(Button("Undo", () => { document.Undo(); Redraw(); }));
        top.Children.Add(Button("Redo", () => { document.Redo(); Redraw(); }));
        top.Children.Add(Button("Reset", () => { document.Reset(); Redraw(); }));
        var guide = new TextBlock { Text = "Green circle: detected lens edge. Camera calibration may cover less of the image; paint outside its coverage may not affect the calculation.",
            Margin = new(16, 0, 16, 7), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
        DockPanel.SetDock(guide, Dock.Top); root.Children.Add(guide);

        var bottom = new DockPanel { Margin = new(12, 6, 12, 10) };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Right); bottom.Children.Add(actions);
        actions.Children.Add(Button("Cancel", Close)); actions.Children.Add(Button("Save as new mask", SaveMask));
        bottom.Children.Add(Label("Name"));
        name = new TextBox { Text = request.SuggestedName, MinWidth = 250, MaxWidth = 460, Margin = new(8, 0, 14, 0),
            VerticalContentAlignment = VerticalAlignment.Center }; bottom.Children.Add(name);

        scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Black,
            CanContentScroll = false, PanningMode = PanningMode.None };
        root.Children.Add(scroll);
        canvas = new Grid { Width = width, Height = height, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        canvas.Children.Add(new Image { Source = photoBitmap, Width = width, Height = height, Stretch = Stretch.None, IsHitTestVisible = false });
        canvas.Children.Add(maskImage);
        var outline = new Ellipse { Width = request.DiskRadius * 2, Height = request.DiskRadius * 2,
            Stroke = Brushes.LimeGreen, StrokeThickness = 2, StrokeDashArray = [7, 5], Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new(request.DiskX - request.DiskRadius, request.DiskY - request.DiskRadius, 0, 0), IsHitTestVisible = false };
        canvas.Children.Add(outline); scroll.Content = canvas;
        brushCursor = new Ellipse { Stroke = Brushes.Cyan, StrokeThickness = 1.5, Fill = Brushes.Transparent,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        canvas.Children.Add(brushCursor);
        void MoveCursor(Point point)
        {
            cursorPoint = point;
            brushCursor.Width = brushCursor.Height = brush.Value;
            brushCursor.Margin = new(point.X - brush.Value / 2, point.Y - brush.Value / 2, 0, 0);
            brushCursor.Visibility = Visibility.Visible;
        }
        canvas.MouseMove += (_, e) => MoveCursor(e.GetPosition(canvas));
        canvas.MouseLeave += (_, _) => brushCursor.Visibility = Visibility.Collapsed;
        brush.ValueChanged += (_, _) => { if (brushCursor.Visibility == Visibility.Visible) MoveCursor(cursorPoint); };
        canvas.MouseLeftButtonDown += (_, e) => { painting = true; last = e.GetPosition(canvas); canvas.CaptureMouse();
            document.BeginStroke(); document.PaintLine(last.X, last.Y, last.X, last.Y, brush.Value, white.IsChecked == true ? (byte)255 : (byte)0); Redraw(); e.Handled = true; };
        canvas.MouseMove += (_, e) => { if (!painting) return; Point next = e.GetPosition(canvas);
            document.PaintLine(last.X, last.Y, next.X, next.Y, brush.Value, white.IsChecked == true ? (byte)255 : (byte)0);
            last = next; Redraw(); };
        canvas.MouseLeftButtonUp += (_, _) => FinishStroke();
        canvas.LostMouseCapture += (_, _) => FinishStroke();
        scroll.PreviewMouseWheel += (_, e) =>
        { zoomAnchor = e.GetPosition(scroll);
            try { zoom.Value = Math.Clamp(zoom.Value * (e.Delta > 0 ? 1.15 : 1 / 1.15), zoom.Minimum, zoom.Maximum); }
            finally { zoomAnchor = null; }
            e.Handled = true; };
        canvas.MouseRightButtonDown += (_, e) => { panning = true; panPoint = e.GetPosition(scroll); canvas.CaptureMouse(); };
        canvas.MouseRightButtonUp += (_, _) => { panning = false; canvas.ReleaseMouseCapture(); };
        canvas.MouseMove += (_, e) => { if (!panning) return; Point next = e.GetPosition(scroll);
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + panPoint.X - next.X);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + panPoint.Y - next.Y); panPoint = next; };
        KeyDown += (_, e) => { if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Z) { document.Undo(); Redraw(); e.Handled = true; }
            else if (e.Key == Key.Y) { document.Redo(); Redraw(); e.Handled = true; } };
        Closing += (_, e) => { if (saved || !document.IsDirty) return;
            if (MessageBox.Show(this, "Discard your unsaved mask edits?", "Unsaved mask", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true; };
        Loaded += (_, _) => { Redraw(); FitImage(); };
    }

    private static BitmapImage Decode(byte[] png)
    {
        var bitmap = new BitmapImage(); using var stream = new MemoryStream(png);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    private static TextBlock Label(string value) => new() { Text = value, Margin = new(7, 8, 3, 5), VerticalAlignment = VerticalAlignment.Center };
    private static Slider Slider(double min, double max, double value, double width) => new()
    { Minimum = min, Maximum = max, Value = value, Width = width, Margin = new(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string title, Action click)
    { var button = new Button { Content = title, Margin = new(5, 1, 4, 1), Padding = new(10, 6, 10, 6) };
        button.Click += (_, _) => click(); return button; }
    private void Redraw()
    {
        if (document.TakeDirtyRegion() is { } changed)
            maskBitmap.WritePixels(changed.Rectangle, changed.Pixels, changed.Rectangle.Width, 0);
    }
    private void FinishStroke()
    { if (!painting) return; painting = false; document.EndStroke(); canvas.ReleaseMouseCapture(); }
    private void FitImage()
    { if (scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0) return;
        zoom.Value = Math.Clamp(Math.Min(scroll.ViewportWidth / document.Width, scroll.ViewportHeight / document.Height) * 100, zoom.Minimum, zoom.Maximum);
        scroll.ScrollToHorizontalOffset(0); scroll.ScrollToVerticalOffset(0); }
    private void SaveMask()
    {
        FinishStroke();
        if (!document.IsDirty) { MessageBox.Show(this, "Paint at least one change before saving a new mask."); return; }
        string label = name.Text.Trim();
        if (label.Length is < 1 or > 100) { MessageBox.Show(this, "Enter a mask name of 1–100 characters."); return; }
        try { if (save(document.EncodePng(), label)) { saved = true; DialogResult = true; } }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Mask could not be saved", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    internal void PaintStrokeForSmoke(double x, double y, double diameter, byte value)
    {
        document.BeginStroke(); document.PaintLine(x, y, x, y, diameter, value); document.EndStroke(); Redraw();
    }
    internal void CloseForSmoke() { saved = true; DialogResult = false; }
}
