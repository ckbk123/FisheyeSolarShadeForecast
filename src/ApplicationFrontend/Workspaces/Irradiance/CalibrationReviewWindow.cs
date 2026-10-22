using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SolarShade.Calibration.Solver;

namespace SolarShade.Desktop;

/// <summary>Review detector feedback without blocking changes in the main study.</summary>
public sealed class CalibrationReviewWindow : Window
{
    private readonly CalibrationReview review;
    private readonly ComboBox photos = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock feedback = IrradianceWorkspace.Label("", 13);
    public CalibrationImageDetection? SelectedPhoto => photos.SelectedItem is PhotoChoice choice ? choice.Image : null;
    public bool HasPreview => preview.Source != null;
    public void SelectPhoto(int index) => photos.SelectedIndex = index;
    private sealed record PhotoChoice(CalibrationImageDetection Image)
    {
        public override string ToString() => (Image.Detected ? "Detected · " : "Failed · ") + Image.Name;
    }

    public CalibrationReviewWindow(CalibrationReview review, Window? owner = null)
    {
        this.review = review;
        Title = "Checkerboard calibration review";
        if (string.Equals(Environment.GetEnvironmentVariable("SOLARSHADE_VALIDATION_MONITOR"), "secondary", StringComparison.OrdinalIgnoreCase))
            ShowActivated = false;
        Width = Math.Min(900, SystemParameters.WorkArea.Width); Height = Math.Min(760, SystemParameters.WorkArea.Height);
        if (owner != null)
        {
            Owner = owner;
            double OwnerExtent(double actual, double requested, double fallback) => double.IsFinite(actual) && actual > 0 ? actual
                : double.IsFinite(requested) && requested > 0 ? requested : fallback;
            Width = Math.Min(Width, OwnerExtent(owner.ActualWidth, owner.Width, Width));
            Height = Math.Min(Height, OwnerExtent(owner.ActualHeight, owner.Height, Height));
        }
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(16) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(IrradianceWorkspace.Label(review.Summary, 18));
        header.Children.Add(IrradianceWorkspace.Label(review.Report.Failure is { } error
            ? "Calibration could not complete: " + error : "Colored corner markers show the detected inner corners.", 13));
        if (review.Note != null) header.Children.Add(IrradianceWorkspace.Label(review.Note, 12));
        if (review.FitSummary != null) header.Children.Add(IrradianceWorkspace.Label(review.FitSummary, 12));
        header.Children.Add(IrradianceWorkspace.Label("Checkerboard photo", 12));
        AutomationProperties.SetName(photos, "Checkerboard photo detection results");
        photos.ItemsSource = review.Report.Images.Select(i => new PhotoChoice(i)).ToArray();
        photos.Margin = new(0, 6, 0, 8); header.Children.Add(photos);
        header.Children.Add(feedback);
        var footer = new StackPanel { Margin = new(0, 8, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(IrradianceWorkspace.Label("Saved review and diagnostics", 12));
        footer.Children.Add(new TextBox { Text = review.Directory, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new(0, 6, 0, 0) }; footer.Children.Add(buttons);
        var open = new Button { Content = "Open diagnostics folder", Padding = new(10, 5, 10, 5) };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(review.Directory) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { feedback.Text = "Could not open the folder: " + ex.Message; }
        };
        buttons.Children.Add(open);
        var close = new Button { Content = "Close review", Padding = new(10, 5, 10, 5), Margin = new(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close(); buttons.Children.Add(close);
        root.Children.Add(new Border { Margin = new(0, 8, 0, 0), Background = Brushes.Black, Child = preview });
        photos.SelectionChanged += (_, _) => ShowPhoto();
        photos.SelectedIndex = review.Report.Images.Count > 0 ? 0 : -1;
        if (photos.SelectedIndex < 0) feedback.Text = "No supported photos reached checkerboard detection. Check the folder and board settings.";
    }

    private void ShowPhoto()
    {
        preview.Source = null;
        if (SelectedPhoto is not { } image) return;
        feedback.Text = image.Message + "\nSource: " + image.Path;
        AutomationProperties.SetName(preview, image.Name + " · checkerboard detection preview");
        try
        {
            byte[]? bytes = review.Preview(image);
            if (bytes != null) preview.Source = IrradianceWorkspace.Bitmap(bytes);
            else feedback.Text += "\nNo preview is available for this photo.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        { feedback.Text += "\nPreview could not be opened: " + ex.Message; }
    }
}
