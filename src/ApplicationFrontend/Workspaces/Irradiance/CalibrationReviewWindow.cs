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
    private readonly CalibrationStamp stamp = new() { Name = "CheckerboardStatusStamp" };
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
        Width = 900; Height = 760;
        if (owner != null) WindowWorkArea.FitToOwner(this, owner);
        else { StudioTheme.Apply(this); WindowWorkArea.FitOnStartup(this); }
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new(16) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var header = new StackPanel(); root.Children.Add(header);
        var summary = IrradianceWorkspace.Label(review.Summary, 18, StudioTheme.Ink);
        summary.FontWeight = FontWeights.SemiBold; header.Children.Add(summary);
        if (review.Report.Failure is { } error) header.Children.Add(IrradianceWorkspace.Label("Calibration could not complete. " + error, 13));
        if (review.Note != null) header.Children.Add(IrradianceWorkspace.Label(review.Note, 12));
        if (review.FitSummary != null) header.Children.Add(IrradianceWorkspace.Label(review.FitSummary, 12));
        header.Children.Add(IrradianceWorkspace.Label("Checkerboard photo", 12));
        AutomationProperties.SetName(photos, "Checkerboard photo detection results");
        StudioTheme.Help(photos, "Choose a checkerboard photo to review its detection. Colored markers show the detected inner corners.");
        photos.ItemsSource = review.Report.Images.Select(i => new PhotoChoice(i)).ToArray();
        photos.Margin = new(0, 6, 0, 8); header.Children.Add(photos);
        header.Children.Add(feedback);
        var photoPane = new Grid { Background = StudioTheme.Navy, Height = 360, Margin = new(0, 8, 0, 0), ClipToBounds = true };
        photoPane.Children.Add(preview);
        AutomationProperties.SetAutomationId(stamp, "CheckerboardStatusStamp");
        photoPane.Children.Add(stamp);
        root.Children.Add(photoPane);
        var footer = new StackPanel { Margin = new(0, 8, 0, 0) }; root.Children.Add(footer);
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
        photos.SelectionChanged += (_, _) => ShowPhoto();
        photos.SelectedIndex = review.Report.Images.Count > 0 ? 0 : -1;
        if (photos.SelectedIndex < 0) ShowPhoto();
    }

    private void ShowPhoto()
    {
        preview.Source = null;
        var image = SelectedPhoto;
        if (review.Report.Failure is { } reason) stamp.SetMessage(CalibrationStamp.FailureLabel, reason);
        else if (image is { Detected: false }) stamp.SetMessage("PHOTO REJECTED", image.Message);
        else stamp.SetMessage(null);
        if (image == null)
        {
            feedback.Text = "No supported photos reached checkerboard detection. Check the folder and board settings.";
            return;
        }
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
