using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    public FrameworkElement ImagesAndCalibration { get; }
    private readonly Border studyPreview = new(), setupPreview = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly RowDefinition previewHeight = new() { Height = new(220) };
    private bool imageSetupVisible, previewExpanded;

    // Both tabs host the same preview so that edits, zoom and mask pixels stay in sync.
    internal void ShowImageSetup(bool show)
    {
        if (imageSetupVisible == show) return;
        imageSetupVisible = show;
        var previous = show ? studyPreview : setupPreview;
        var next = show ? setupPreview : studyPreview;
        var content = previous.Child;
        previous.Child = null;
        next.Child = content;
        FitPreviewHeight();
    }

    private void FitPreviewHeight() => previewHeight.Height = new(previewExpanded
        ? Math.Clamp(owner.ActualHeight * .55, 320, 640) : imageSetupVisible ? 360 : 220);

    private FrameworkElement BuildImageSetup()
    {
        var layout = new Grid { Margin = new(16, 12, 16, 8) };
        var inputs = new StackPanel { Margin = new(14) };
        var sidebar = Card(new ScrollViewer { Content = inputs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        sidebar.VerticalAlignment = VerticalAlignment.Top;
        layout.Children.Add(sidebar); layout.Children.Add(setupPreview);
        Section(inputs, "Lens calibration");
        PathField(inputs, "Calibration folder", "CalibrationFolder", () => PickCalibrationFolder());
        var board = new Grid(); for (int i = 0; i < 3; i++) board.ColumnDefinitions.Add(new());
        inputs.Children.Add(board);
        SmallField(board, 0, "Inner columns", "Columns"); SmallField(board, 1, "Inner rows", "Rows"); SmallField(board, 2, "Square, mm", "SquareMm");
        const string boardHelp = "Count the checkerboard’s inner corners. A board with 7 × 10 squares has 6 × 9 inner corners.";
        StudioTheme.Help(fields["Columns"], boardHelp);
        StudioTheme.Help(fields["Rows"], boardHelp);
        StudioTheme.Help(fields["SquareMm"], "Measure the side of one checkerboard square in millimetres.");
        var calButtons = new WrapPanel();
        var calibrate = Button("Calibrate", async () => await Calibrate());
        loadProfile.Click += (_, _) => PickProfile();
        StudioTheme.Help(calibrate, "Fit the checkerboard photos and save the reusable camera profile under Data/Profiles. Diagnostic files are saved under Debug Data/01-calibration.");
        calButtons.Children.Add(calibrate); calButtons.Children.Add(loadProfile);
        reviewCalibration.Click += (_, _) => { RefreshCalibrationReview(); ShowCalibrationReview(); }; calButtons.Children.Add(reviewCalibration);
        calibrationInfo.Name = "CalibrationOutcome";
        calibrationInfo.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(calibrationInfo, AutomationLiveSetting.Polite);
        inputs.Children.Add(calButtons); inputs.Children.Add(calibrationInfo);
        Section(inputs, "Sky mask");
        PathField(inputs, "Sky photo folder", "SkyFolder", PickSkyFolder);
        var imageButtons = new WrapPanel(); imageButtons.Children.Add(choosePhoto); inputs.Children.Add(imageButtons);
        choosePhoto.Click += (_, _) => PickSkyPhoto(); importMask.Click += (_, _) => PickMask();
        generateMask.Click += async (_, _) => await GenerateMask();
        AutomationProperties.SetAutomationId(generateMask, "GenerateSkyMask");
        StudioTheme.Help(generateMask, "Prepare the selected photo’s mask for inspection or editing. Weather and irradiance are calculated separately.");
        inputs.Children.Add(generateMask);
        StudioTheme.Help(choosePhoto, "Choose the original sky photograph taken with the calibrated lens.");
        StudioTheme.Help(importMask, "Import a mask image with the same dimensions as the sky photo. Bright pixels mean open sky by default.");
        images = Combo(inputs, "Active sky photo", []); images.SelectionChanged += (_, _) => { if (!loading && images.SelectedItem is string name) SelectSkyPhoto(Path.Combine(PortablePaths.Resolve(settings.SkyFolder), name)); };
        StudioTheme.Help(choosePhoto, PhotoSwitchHelp);
        model = Combo(inputs, "Sky segmentation", Enum.GetNames<SkyModel>()); model.SelectionChanged += (_, _) => { if (!loading && model.SelectedIndex >= 0) { settings.Model = (SkyModel)model.SelectedIndex; settings.SelectedMaskId = null; Changed(); RefreshMaskChoices(); } };
        resolution = Combo(inputs, "Mask quality", ["Original Quality (Slower)", "Reduced Quality (Faster)"]); resolution.SelectionChanged += (_, _) => { if (!loading) { settings.Resolution = resolution.SelectedIndex == 1 ? 512 : 1024; settings.SelectedMaskId = null; Changed(); RefreshMaskChoices(); } };

        var feedback = Label("", 12);
        feedback.SetBinding(TextBlock.TextProperty, new Binding(nameof(detail.Text)) { Source = detail });
        inputs.Children.Add(feedback);
        var page = new WorkspaceViewport(layout, sidebar, setupPreview, sidebarMinimumHeight: 530, contentMinimumHeight: 450);
        page.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = this });
        return page;
    }
}
