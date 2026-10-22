using System.Diagnostics;
using System.Globalization;
using SolarShade.Camera;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SolarShade.SkyPhotoMasking;
using SolarShade.Shading;
using SolarShade.MaskEditor;
using Cv2 = OpenCvSharp.Cv2;
using ImreadModes = OpenCvSharp.ImreadModes;
using SolarShade.Calibration.Validation;

namespace SolarShade.Desktop;

public sealed class IrradianceWorkspace : UserControl, IDisposable
{
    private readonly Window owner;
    private HelpWindow? helpWindow, noticesWindow;
    private readonly IrradianceWorkspaceController controller;
    public IrradianceWorkspaceController Controller => controller;
    public FrameworkElement Actions { get; private set; } = null!;
    private UserSettings settings { get => controller.Settings; set => controller.Settings = value; }
    private readonly Func<TimeZoneInfo> systemZone;
    private IIrradianceService services => controller.Service;
    private readonly SourceFileWatch sourceWatch;
    private int sourceCheckQueued, observations;
    private bool checkAfterUpdate, fullCheckQueued;
    private Task inputWork = Task.CompletedTask;
    private InputDependencies? uiInputs;
    private ArtifactGroup uiInvalid;
    public Task InputsSettled => inputWork;
    private CancellationTokenSource? pending { get => controller.Pending; set => controller.Pending = value; }
    private long revision { get => controller.Revision; set => controller.Revision = value; }
    private bool loading, closing, exporting;
    private bool calibrating { get => controller.Calibrating; set => controller.Calibrating = value; }
    private bool updating { get => controller.Updating; set => controller.Updating = value; }
    private UpdateState updateState { get => controller.State; set => controller.State = value; }
    private readonly TextBlock updateIndicator = Label("● Update required", 13);
    private readonly Button update = new() { Content = "Update results" };
    private readonly Button refreshData = new() { Content = "Download weather again & update" };
    private readonly TextBlock weatherSource = Label("", 11);
    private readonly TextBlock lastWeatherResult = Label("", 11);
    private readonly Button chooseWeatherFile = new() { Content = "Choose XLSX / CSV file" };
    private readonly Button stop = new() { Content = "Stop", IsEnabled = false };
    private Evaluation? completed { get => controller.Completed; set => controller.Completed = value; }
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly HashSet<string> invalidFields = new();
    private readonly TextBlock status = Label("Ready when you are", 13), detail = Label("Start with your data, or load the included example.", 12), maskInfo = Label("Choose a sky photo to begin", 12), calibrationInfo = Label("No camera profile selected", 11);
    private readonly Button reviewCalibration = new() { Content = "Review checkerboard photos", IsEnabled = false };
    private CalibrationReview? calibrationReview;
    private CalibrationReviewWindow? calibrationReviewWindow;
    private string? reviewedProfile;
    private string? reviewedProfileFingerprint;
    private readonly TextBlock beforeEnergy = Label("—", 26), afterEnergy = Label("—", 26), reduction = Label("—", 26), hover = Label("Scroll to zoom · drag to pan · click Full period to reset", 11);
    private readonly TextBlock provenance = Label("Horizontal data is retained; both graph curves refer to the panel.", 11);
    private readonly ImageOverlayPreview photo = new();
    private readonly CheckBox showCardinals = new() { Content = "Show cardinal directions", VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 8, 0) };
    private readonly TextBlock cardinalInfo = Label("Cardinal directions unavailable · add a calibrated sky photo", 11);
    private readonly TextBlock fieldOfViewInfo = Label("FOV unavailable", 11);
    private int orientedPhotoWidth, orientedPhotoHeight;
    private long orientationRevision;
    private byte[]? displayedPhoto, displayedCardinals;
    private CardinalDirectionOverlayResult? currentCardinals;
    private readonly MaskOverlayPreview maskPreview = new();
    private readonly TextBlock sunPathInfo = Label("Sun path unavailable · add a calibrated sky photo", 11);
    private readonly CheckBox showSunPath = new() { Content = "Show sun path", VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 16, 0) };
    private bool sunPathPublishedEarly;
    private bool sunPathIsCurrent;
    private byte[]? displayedMaskPng;
    private MaskAsset? displayedAsset;
    private readonly Button editMask = new() { Content = "Edit mask", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6) };
    private readonly Button savedStudies = new() { Content = "Saved studies" };
    private readonly Button choosePhoto = new() { Content = "Choose sky photo…", Margin = new(0, 4, 6, 4), Padding = new(10, 6, 10, 6) };
    private readonly Button importMask = new() { Content = "Import existing mask…", Margin = new(0, 4, 6, 4), Padding = new(10, 6, 10, 6) };
    private readonly ComboBox maskChoice = new() { MinWidth = 220 };
    private sealed record MaskChoice(string? Id, string Title, bool Imported = false) { public override string ToString() => Title; }
    private readonly IrradianceChart chart = new();
    private readonly Button export = new() { Content = "Export irradiance", IsEnabled = false };
    private ComboBox provider = null!, model = null!, resolution = null!, images = null!, zone = null!, window = null!;
    private CheckBox isotropic = null!, useSystemZone = null!;
    private DatePicker graphDay = null!;
    private readonly Brush ink = new SolidColorBrush(Color.FromRgb(25, 48, 58)), accent = new SolidColorBrush(Color.FromRgb(15, 125, 111));
    public IrradianceWorkspace(Window owner, IrradianceWorkspaceController controller, Func<TimeZoneInfo>? systemZoneProvider = null, bool loadExampleOnFirstRun = true)
    {
        this.owner = owner; this.controller = controller;
        controller.CommandsChanged += OnCommandsChanged;
        sourceWatch = new(() =>
        {
            if (Interlocked.Exchange(ref sourceCheckQueued, 1) != 0 || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref sourceCheckQueued, 0); if (!closing) RecheckSources(); });
        });
        systemZone = systemZoneProvider ?? TimeZoneSelection.CurrentSystemZone;
        settings = AppData.ReadSettings() ?? PortablePaths.Example();
        TimeZoneSelection.ApplySystemZone(settings, systemZone());
        AddStyles();
        var root = new DockPanel(); Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Actions = actions; actions.Resources = Resources;
        actions.Children.Add(Button("Load example", async () => await LoadExample()));
        actions.Children.Add(Button("Help", ShowHelp));
        savedStudies.Click += (_, _) => ShowSavedStudies(); actions.Children.Add(savedStudies);
        export.Click += async (_, _) =>
        {
            await RecheckSourcesAsync();
            var snapshot = completed; if (snapshot == null || !export.IsEnabled) return;
            var pick = new OpenFolderDialog { Title = "Choose a folder for a new scenario export" };
            if (pick.ShowDialog(owner) != true) return;
            await RecheckSourcesAsync(); if (!export.IsEnabled || completed != snapshot) return;
            exporting = true; RefreshButtons();
            try
            {
                string folder = await Task.Run(() => AppServices.Export(snapshot, pick.FolderName));
                if (!closing) { status.Text = "Exported debug data and Summary.pdf"; detail.Text = folder; }
            }
            catch (Exception ex) { if (!closing) { status.Text = "Export could not finish"; detail.Text = ex.Message; } }
            finally { exporting = false; if (!closing) { RecheckSources(); RefreshButtons(); } }
        };
        actions.Children.Add(export);
        var footer = new StackPanel { Margin = new(22, 8, 22, 10) }; footer.Children.Add(status); footer.Children.Add(detail); detail.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new Grid { Margin = new(16, 12, 16, 0), MinWidth = 950, MinHeight = 640 }; body.ColumnDefinitions.Add(new() { Width = new GridLength(348) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); body.ColumnDefinitions.Add(new());
        root.Children.Add(new WorkspaceViewport(body));
        var inputs = new StackPanel { Margin = new(14) };
        var inputHost = new DockPanel { Background = Brushes.White }; body.Children.Add(inputHost);
        var tuning = new StackPanel { Margin = new(14, 0, 14, 8) }; DockPanel.SetDock(tuning, Dock.Bottom); inputHost.Children.Add(tuning);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = inputs, Background = Brushes.White }; inputHost.Children.Add(scroll);
        Section(inputs, "01  IMAGES & CALIBRATION");
        PathField(inputs, "Calibration folder", "CalibrationFolder", () => PickCalibrationFolder());
        var board = new Grid(); for (int i = 0; i < 3; i++) board.ColumnDefinitions.Add(new());
        inputs.Children.Add(board);
        SmallField(board, 0, "Inner columns", "Columns"); SmallField(board, 1, "Inner rows", "Rows"); SmallField(board, 2, "Square, mm", "SquareMm");
        inputs.Children.Add(Label("Count inner corners, not squares: 7 × 10 squares → 6 × 9 corners.", 11));
        var calButtons = new WrapPanel(); calButtons.Children.Add(Button("Calibrate", async () => await Calibrate())); calButtons.Children.Add(Button("Load camera profile…", PickProfile));
        reviewCalibration.Click += (_, _) => { RefreshCalibrationReview(); ShowCalibrationReview(); }; calButtons.Children.Add(reviewCalibration);
        inputs.Children.Add(calButtons); inputs.Children.Add(calibrationInfo);
        inputs.Children.Add(Label("Load camera-profile.json for camera parameters and validated coverage. Calibrate saves this reusable profile under Data/Profiles and diagnostics under Debug Data/01-calibration.", 11));
        PathField(inputs, "Sky photo folder", "SkyFolder", PickSkyFolder);
        var imageButtons = new WrapPanel(); imageButtons.Children.Add(choosePhoto); imageButtons.Children.Add(importMask); inputs.Children.Add(imageButtons);
        choosePhoto.Click += (_, _) => PickSkyPhoto(); importMask.Click += (_, _) => PickMask();
        importMask.ToolTip = "Use an existing black-and-white PNG without AI segmentation. White is open sky and black is obstruction; inversion is an explicit import option.";
        inputs.Children.Add(Label("Choose the photograph first. Import a ready-made black-and-white mask to skip AI segmentation.", 11));
        images = Combo(inputs, "Active sky photo", []); images.SelectionChanged += (_, _) => { if (!loading && images.SelectedItem is string name) { settings.SkyImage = Path.Combine(settings.SkyFolder, name); RestorePhotoMaskSelection(); Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview(); } };
        model = Combo(inputs, "Sky segmentation", Enum.GetNames<SkyModel>()); model.SelectionChanged += (_, _) => { if (!loading && model.SelectedIndex >= 0) { settings.Model = (SkyModel)model.SelectedIndex; settings.SelectedMaskId = null; Changed(); RefreshMaskChoices(); } };
        resolution = Combo(inputs, "Mask resolution", ["1024 · original quality", "512 · faster"]); resolution.SelectionChanged += (_, _) => { if (!loading) { settings.Resolution = resolution.SelectedIndex == 1 ? 512 : 1024; settings.SelectedMaskId = null; Changed(); RefreshMaskChoices(); } };
        Section(inputs, "02  CAMERA POSE");
        Field(inputs, "Image bottom bearing, ° true north", "BottomAzimuth"); Field(inputs, "Camera tilt from vertical, °", "CameraTilt");
        inputs.Children.Add(Label("Default: image bottom south (180°), camera looking straight up. Positive tilt points toward image top before additional roll.", 11));
        inputs.Children.Add(Label("True north means geographic north. Bearings increase clockwise: N 0°, E 90°, S 180°, W 270°. Use your compass's true-north setting; magnetic correction is not automatic.", 11));
        inputs.Children.Add(Label("Looking up: with bottom south and roll 0°, north is at the top, east on the left and west on the right. Arrows, sun paths and shading use this same orientation.", 11));
        Section(inputs, "03  SITE LOCATION");
        Field(inputs, "Latitude, ° N (+) / S (−)", "Latitude"); Field(inputs, "Longitude, ° E (+) / W (−)", "Longitude"); Field(inputs, "Elevation above sea level, m", "Elevation");
        inputs.Children.Add(Label("Use decimal degrees, for example latitude 43.56 and longitude 1.48. Southern latitudes and western longitudes are negative.", 11));
        useSystemZone = new() { Content = "Use Windows time zone (automatic)", Margin = new(0, 8, 0, 4) };
        useSystemZone.Checked += (_, _) => ChangeTimeZoneMode(true);
        useSystemZone.Unchecked += (_, _) => ChangeTimeZoneMode(false);
        inputs.Children.Add(useSystemZone);
        zone = Combo(inputs, "Time zone for dates and graph", []);
        zone.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName); zone.SelectedValuePath = nameof(TimeZoneInfo.Id);
        zone.ItemsSource = TimeZoneSelection.Choices();
        zone.DropDownOpened += (_, _) =>
        {
            if (zone.Template.FindName("PART_Popup", zone) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement list })
            {
                list.MinWidth = Math.Min(600, SystemParameters.WorkArea.Width - 48);
                list.MaxWidth = Math.Min(760, SystemParameters.WorkArea.Width - 48);
            }
        };
        zone.SelectionChanged += (_, _) => { if (!loading && !settings.UseSystemTimeZone && zone.SelectedValue is string z) { settings.Zone = z; zone.ToolTip = (zone.SelectedItem as TimeZoneInfo)?.DisplayName; Changed(); } };
        inputs.Children.Add(Label("Turn off automatic to choose. Named regions follow daylight saving; fixed UTC offsets never change. Coordinates select the weather location.", 11));
        Section(inputs, "04  HISTORICAL DATA");
        Field(inputs, "Start date · DD.MM.YYYY", "Start"); Field(inputs, "End date · inclusive · DD.MM.YYYY", "End");
        provider = Combo(inputs, "Weather data source", ["NASA POWER", "Open-Meteo · ERA5", "Import XLSX / CSV"]);
        System.Windows.Automation.AutomationProperties.SetName(provider, "Weather data source");
        provider.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            if (provider.SelectedIndex == 2) { if (string.IsNullOrEmpty(settings.ImportPath)) PickImport(); }
            else if (provider.SelectedIndex >= 0) { settings.Provider = provider.SelectedIndex; settings.ImportPath = ""; Changed(); }
            RefreshWeatherSource();
        };
        chooseWeatherFile.Click += (_, _) => PickImport(); inputs.Children.Add(chooseWeatherFile); inputs.Children.Add(weatherSource); inputs.Children.Add(lastWeatherResult);
        inputs.Children.Add(Label("Import: timestamp, direct horizontal (BHI), diffuse horizontal (DHI), in W/m². Native metadata sets the intervals; otherwise choose their duration and label below.", 11));
        inputs.Children.Add(Label("Example CSV header: Timestamp,BHI,DHI\n2025-05-15T12:00:00+07:00,500,100\nA total-only (GHI) measurement cannot supply both components. Example/Irradiance includes a workbook.", 11));
        window = Combo(inputs, "Imported rows represent", ["Preceding interval · end labels", "Following interval · start labels", "Centered interval"]); window.SelectionChanged += (_, _) => { if (!loading) { settings.ImportWindow = window.SelectedIndex; Changed(); } };
        Field(inputs, "Import interval, minutes (without metadata)", "ImportIntervalMinutes");
        isotropic = new() { Content = "Use isotropic diffuse instead of Hay–Davies", Margin = new(0, 8, 0, 8) }; isotropic.Checked += (_, _) => { if (!loading) { settings.Isotropic = true; Changed(); } }; isotropic.Unchecked += (_, _) => { if (!loading) { settings.Isotropic = false; Changed(); } }; inputs.Children.Add(isotropic);
        inputs.Children.Add(Label("Neither diffuse model is always the lower estimate. The result depends on weather, panel direction and the mask.", 11));
        Section(tuning, "05  PANEL INSTALLATION");
        PanelSlider(tuning, "Tilt from horizontal, °", "PanelTilt", 90); PanelSlider(tuning, "Front azimuth, ° true north", "PanelAzimuth", 360);
        tuning.Children.Add(Label("N 0° · E 90° · S 180° · W 270°", 11));
        update.Background = accent; update.Foreground = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(update, "UpdateResults");
        System.Windows.Automation.AutomationProperties.SetAutomationId(refreshData, "RefreshData");
        update.Click += (_, _) => Calculate(); refreshData.Click += (_, _) => Begin(true); stop.Click += (_, _) => StopUpdate();
        tuning.Children.Add(updateIndicator);
        var runButtons = new WrapPanel(); runButtons.Children.Add(update); runButtons.Children.Add(stop); tuning.Children.Add(runButtons);
        tuning.Children.Add(refreshData);
        tuning.Children.Add(Label("Update results reuses matching weather data. Download weather again bypasses the weather cache; camera calibration and masks are reused when unchanged.", 11));

        var right = new Grid(); Grid.SetColumn(right, 2); body.Children.Add(right);
        right.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star), MinHeight = 240, MaxHeight = 350 }); right.RowDefinitions.Add(new() { Height = new GridLength(10) }); right.RowDefinitions.Add(new() { Height = new GridLength(3, GridUnitType.Star), MinHeight = 390 });
        var preview = new Grid { Background = Brushes.White }; right.Children.Add(preview); preview.RowDefinitions.Add(new() { Height = new GridLength(42) }); preview.RowDefinitions.Add(new()); preview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var previewHeader = new DockPanel(); preview.Children.Add(previewHeader);
        DockPanel.SetDock(showCardinals, Dock.Right); previewHeader.Children.Add(showCardinals);
        showCardinals.Checked += (_, _) => SetCardinalVisibility(); showCardinals.Unchecked += (_, _) => SetCardinalVisibility();
        showCardinals.ToolTip = "Show compass bearings at the calibrated image boundary. Uses the same heading, tilt and roll as sun paths and shading. Visibility does not recalculate.";
        DockPanel.SetDock(showSunPath, Dock.Right); previewHeader.Children.Add(showSunPath);
        showSunPath.Checked += (_, _) => SetSunPathVisibility(); showSunPath.Unchecked += (_, _) => SetSunPathVisibility();
        showSunPath.ToolTip = "Display the sun path for the selected period on the calibrated mask. This changes only the preview.";
        var previewTitle = Label("SKY PHOTO  /  MASK", 14); previewTitle.Margin = new(16, 12, 0, 0); previewHeader.Children.Add(previewTitle);
        var pair = new Grid { Margin = new(12, 0, 12, 0) }; pair.ColumnDefinitions.Add(new()); pair.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); pair.ColumnDefinitions.Add(new()); Grid.SetRow(pair, 1); preview.Children.Add(pair);
        pair.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(233, 239, 239)), Child = photo });
        var maskPane = new Grid(); maskPane.Children.Add(maskPreview); maskPane.Children.Add(editMask);
        var maskBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(233, 239, 239)), Child = maskPane }; Grid.SetColumn(maskBorder, 2); pair.Children.Add(maskBorder);
        editMask.Click += (_, _) => OpenMaskEditor();
        var previewInfo = new Grid { Margin = new(16, 3, 8, 0) }; previewInfo.ColumnDefinitions.Add(new()); previewInfo.ColumnDefinitions.Add(new());
        var previewLabels = new StackPanel(); previewLabels.Children.Add(fieldOfViewInfo); previewLabels.Children.Add(maskInfo); previewLabels.Children.Add(sunPathInfo); previewLabels.Children.Add(cardinalInfo); previewInfo.Children.Add(previewLabels);
        System.Windows.Automation.AutomationProperties.SetAutomationId(fieldOfViewInfo, "SkyPhotoFieldOfView");
        var maskPicker = new StackPanel(); maskPicker.Children.Add(Label("Mask for calculation", 11)); maskPicker.Children.Add(maskChoice);
        maskChoice.SelectionChanged += (_, _) => SelectMaskChoice(); Grid.SetColumn(maskPicker, 1); previewInfo.Children.Add(maskPicker);
        Grid.SetRow(previewInfo, 2); preview.Children.Add(previewInfo);
        var graph = new Grid { Background = Brushes.White, Margin = new(0, 0, 0, 0) }; Grid.SetRow(graph, 2); right.Children.Add(graph);
        graph.RowDefinitions.Add(new() { Height = new GridLength(83) }); graph.RowDefinitions.Add(new() { Height = new GridLength(50) }); graph.RowDefinitions.Add(new()); graph.RowDefinitions.Add(new() { Height = new GridLength(31) }); graph.RowDefinitions.Add(new() { Height = new GridLength(45) });
        var cards = new UniformGridCompat(3) { Margin = new(18, 10, 18, 0) }; graph.Children.Add(cards);
        cards.Children.Add(Metric("BEFORE SHADING · kWh/m²", beforeEnergy, new SolidColorBrush(Color.FromRgb(185, 121, 26)))); cards.Children.Add(Metric("AFTER SHADING · kWh/m²", afterEnergy, accent)); cards.Children.Add(Metric("ESTIMATED LOSS", reduction, ink));
        var toolbar = new WrapPanel { Margin = new(14, 4, 10, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(toolbar, 1); graph.Children.Add(toolbar);
        var component = new ComboBox { ItemsSource = new[] { "Total irradiance", "Direct component", "Sky diffuse component" }, SelectedIndex = 0, Width = 170 }; component.SelectionChanged += (_, _) => { chart.Component = component.SelectedIndex; chart.InvalidateVisual(); }; toolbar.Children.Add(component);
        toolbar.Children.Add(Button("Full period", chart.Reset)); graphDay = new DatePicker { Width = 125, Margin = new(4, 0, 0, 0) }; graphDay.SelectedDateChanged += (_, _) => { if (graphDay.SelectedDate is { } d) chart.Day(d); }; toolbar.Children.Add(graphDay);
        toolbar.Children.Add(Label("  ● Before", 12, new SolidColorBrush(Color.FromRgb(185, 121, 26)))); toolbar.Children.Add(Label("  ● After", 12, accent));
        chart.HoverText = text => hover.Text = text; Grid.SetRow(chart, 2); graph.Children.Add(chart); hover.Margin = new(18, 4, 10, 0); Grid.SetRow(hover, 3); graph.Children.Add(hover);
        provenance.Margin = new(18, 0, 14, 0); provenance.TextWrapping = TextWrapping.Wrap; Grid.SetRow(provenance, 4); graph.Children.Add(provenance);
        
        settings = services.PrepareInputs(settings);
        LoadFields();
        ShowSelectedMaskPreview();
        services.ObserveInputs(settings, refreshSources: true);
        uiInputs = InputDependencies.Capture(settings);
        WatchSources();
        RefreshInputState();
        loadFirstExample = loadExampleOnFirstRun && settings.FirstRun;
        owner.ContentRendered += OnContentRendered;
        owner.Activated += OnActivated;
        SystemEvents.TimeChanged += SystemTimeChanged;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;

    }
    private bool loadFirstExample;
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        if (!loadFirstExample) { QueueObservation(true, true); return; }
        loadFirstExample = false;
        try { await LoadExample(); } catch (Exception ex) { ShowError(ex); }
    }
    private void OnActivated(object? sender, EventArgs e) { RefreshSystemTimeZone(); RecheckSources(); }
    private void OnCommandsChanged(object? sender, EventArgs e) { if (!closing) RefreshButtons(); }
    public void Dispose()
    {
        if (closing) return;
        closing = true; revision++; pending?.Cancel();
        owner.ContentRendered -= OnContentRendered; owner.Activated -= OnActivated;
        SystemEvents.TimeChanged -= SystemTimeChanged; SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        sourceWatch.Dispose();
        calibrationReviewWindow?.Close();
        controller.CommandsChanged -= OnCommandsChanged;
        try { AppData.SaveSettings(settings); } catch { }
        if (inputWork.IsCompleted) controller.Dispose();
        else _ = DisposeAfterObservation();
        async Task DisposeAfterObservation() { await inputWork; controller.Dispose(); }
    }
    private void AddStyles()
    {
        var text = new Style(typeof(TextBox)); text.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5))); text.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(202, 215, 219)))); text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 6, 7))); Resources.Add(typeof(TextBox), text);
        var combo = new Style(typeof(ComboBox)); combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 5, 6, 5))); combo.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 6, 7))); Resources.Add(typeof(ComboBox), combo);
        var button = new Style(typeof(Button)); button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7))); button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 6, 5))); button.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White)); button.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(191, 209, 214)))); button.Setters.Add(new Setter(Control.ForegroundProperty, ink)); button.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand)); Resources.Add(typeof(Button), button);
    }
    public static TextBlock Label(string text, double size = 13, Brush? brush = null) => new() { Text = text, FontSize = size, Foreground = brush ?? new SolidColorBrush(Color.FromRgb(73, 94, 104)), TextWrapping = TextWrapping.Wrap };
    private static StackPanel Metric(string name, TextBlock value, Brush color) { value.Foreground = color; var p = new StackPanel(); p.Children.Add(Label(name, 10)); p.Children.Add(value); return p; }
    private void Section(Panel parent, string text)
    {
        var title = Label(text, 14, ink); title.FontWeight = FontWeights.Bold;
        parent.Children.Add(new Border
        {
            Child = title, Background = new SolidColorBrush(Color.FromRgb(235, 244, 242)),
            BorderBrush = accent, BorderThickness = new(4, 0, 0, 0),
            Padding = new(10, 8, 8, 8), Margin = new(0, 14, 0, 10)
        });
    }
    private Button Button(string text, Action click, bool primary = false) { var b = new Button { Content = text }; if (primary) { b.Background = accent; b.Foreground = Brushes.White; } b.Click += (_, _) => click(); return b; }
    private void Field(Panel parent, string label, string property) { parent.Children.Add(Label(label, 12)); var t = MakeField(property); parent.Children.Add(t); }
    private TextBox MakeField(string property)
    {
        var t = new TextBox { Tag = property }; fields[property] = t;
        void ReadText()
        {
            if (loading) return;
            try
            {
                var p = typeof(UserSettings).GetProperty(property)!; object value;
                if (p.PropertyType == typeof(string)) value = t.Text.Trim();
                else if (p.PropertyType == typeof(DateTime)) value = DateTime.ParseExact(t.Text.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture);
                else if (p.PropertyType == typeof(int)) value = int.Parse(t.Text.Trim(), CultureInfo.InvariantCulture);
                else value = double.Parse(t.Text.Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
                if (value is double number && !double.IsFinite(number)) throw new FormatException();
                bool changed = !Equals(p.GetValue(settings), value) || invalidFields.Contains(property);
                p.SetValue(settings, value); invalidFields.Remove(property); t.ClearValue(Control.BorderBrushProperty);
                if (changed) Changed(saveSettings: false);
            }
            catch
            {
                invalidFields.Add(property); t.BorderBrush = Brushes.Firebrick;
                Changed(saveSettings: false);
            }
        }
        t.TextChanged += (_, _) => ReadText();
        t.LostKeyboardFocus += (_, _) => SaveInputs();
        t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveInputs(); e.Handled = true; } }; return t;
    }
    private void SmallField(Grid grid, int col, string label, string property) { var p = new StackPanel(); Grid.SetColumn(p, col); grid.Children.Add(p); Field(p, label, property); }
    private void PathField(Panel p, string label, string property, Action browse)
    { p.Children.Add(Label(label, 12)); var row = new DockPanel(); var b = Button("…", browse); DockPanel.SetDock(b, Dock.Right); row.Children.Add(b); var t = MakeField(property); t.IsReadOnly = true; row.Children.Add(t); p.Children.Add(row); }
    private static ComboBox Combo(Panel p, string label, string[] items) { p.Children.Add(Label(label, 12)); var c = new ComboBox { ItemsSource = items }; p.Children.Add(c); return c; }
    private void PanelSlider(Panel p, string label, string property, double max)
    {
        var row = new DockPanel(); var number = MakeField(property); number.Width = 66; DockPanel.SetDock(number, Dock.Right); row.Children.Add(number); var caption = Label(label, 12); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption); p.Children.Add(row);
        var slider = new Slider { Minimum = 0, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new(0, 0, 8, 7), Tag = property }; p.Children.Add(slider);
        slider.Value = (double)typeof(UserSettings).GetProperty(property)!.GetValue(settings)!;
        slider.ValueChanged += (_, _) => { if (loading) return; typeof(UserSettings).GetProperty(property)!.SetValue(settings, slider.Value); invalidFields.Remove(property); fields[property].ClearValue(Control.BorderBrushProperty); fields[property].Text = slider.Value.ToString("0", CultureInfo.InvariantCulture); Changed(); };
        fields[property].TextChanged += (_, _) => { if (double.TryParse(fields[property].Text, out double value) && value >= 0 && value <= max) { bool prior = loading; loading = true; slider.Value = value; loading = prior; } };
    }
    private void LoadFields()
    {
        loading = true; invalidFields.Clear();
        foreach (var (name, box) in fields) { box.ClearValue(Control.BorderBrushProperty); var value = typeof(UserSettings).GetProperty(name)!.GetValue(settings); box.Text = value is DateTime d ? d.ToString("dd.MM.yyyy") : Convert.ToString(value, CultureInfo.InvariantCulture); }
        model.SelectedIndex = (int)settings.Model; resolution.SelectedIndex = settings.Resolution == 512 ? 1 : 0; provider.SelectedIndex = settings.Provider; UpdateTimeZoneControls(); window.SelectedIndex = settings.ImportWindow; isotropic.IsChecked = settings.Isotropic;
        showSunPath.IsChecked = settings.ShowSunPath; maskPreview.ShowOverlay = settings.ShowSunPath;
        showCardinals.IsChecked = settings.ShowCardinalDirections; photo.ShowOverlay = settings.ShowCardinalDirections;
        PopulateImages(); graphDay.SelectedDate = settings.Start;
        RefreshMaskChoices();
        RefreshCalibrationReview();
        loading = false;
        RefreshWeatherSource();
    }
    private void RefreshWeatherSource()
    {
        bool imported = !string.IsNullOrEmpty(settings.ImportPath), previous = loading; loading = true;
        provider.SelectedIndex = imported ? 2 : settings.Provider;
        chooseWeatherFile.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        window.IsEnabled = fields["ImportIntervalMinutes"].IsEnabled = imported;
        weatherSource.Text = imported ? "Selected file: " + Path.GetFileName(settings.ImportPath) + "\nUpdate results rereads changed files. No weather download is used."
            : "Matching weather data is reused locally. Download weather again requests a fresh copy from the selected provider.";
        weatherSource.ToolTip = imported ? PortablePaths.Resolve(settings.ImportPath) : null;
        loading = previous;
        RefreshButtons();
    }
    private void PopulateImages()
    {
        string folder = PortablePaths.Resolve(settings.SkyFolder);
        var files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Where(p => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p => p).Select(Path.GetFileName).ToArray() : [];
        images.ItemsSource = files; images.SelectedItem = Path.GetFileName(settings.SkyImage);
        if (files.Length == 1 && images.SelectedIndex < 0) { images.SelectedIndex = 0; settings.SkyImage = Path.Combine(settings.SkyFolder, files[0]!); }
    }
    private void RestorePhotoMaskSelection()
    {
        settings.SelectedMaskId = null;
        if (string.IsNullOrEmpty(settings.SkyImage) || !File.Exists(PortablePaths.Resolve(settings.SkyImage))) return;
        string hash = ManualMaskStore.PhotoHash(settings.SkyImage);
        if (settings.LastMaskByPhoto.TryGetValue(hash, out string? id))
        {
            settings.SelectedMaskId = id;
            try
            {
                var variant = ManualMaskStore.Load(settings.SkyImage, id).Variant;
                if (!ManualMaskStore.IsCompatible(variant, settings)) settings.SelectedMaskId = null;
            }
            catch (Exception)
            {
                // Keep the missing/corrupt selection visible so it cannot silently become the AI mask.
            }
        }
    }
    private void RefreshMaskChoices()
    {
        if (maskChoice == null) return;
        var choices = new List<MaskChoice> { new(null, "AI mask · current model") };
        try
        {
            if (!string.IsNullOrEmpty(settings.SkyImage) && File.Exists(PortablePaths.Resolve(settings.SkyImage)))
            {
                string hash = ManualMaskStore.PhotoHash(settings.SkyImage);
                choices.AddRange(ManualMaskStore.List(hash).Select(v => new MaskChoice(v.Id,
                    $"{v.Label} · {v.CreatedUtc.ToLocalTime():g} · {(v.IsImported ? v.Source + " · no AI" : v.BaseMask.Model)}" +
                    (ManualMaskStore.IsCompatible(v, settings) ? "" : " · use original AI settings"), v.IsImported)));
            }
        }
        catch (Exception ex) { detail.Text = "Could not list saved masks: " + ex.Message; }
        if (settings.SelectedMaskId != null && choices.All(c => c.Id != settings.SelectedMaskId))
            choices.Add(new(settings.SelectedMaskId, "Selected edited mask is missing or invalid"));
        bool previous = loading; loading = true;
        try { maskChoice.ItemsSource = choices; maskChoice.SelectedItem = choices.First(c => c.Id == settings.SelectedMaskId); }
        finally { loading = previous; }
        bool imported = (maskChoice.SelectedItem as MaskChoice)?.Imported == true;
        model.IsEnabled = resolution.IsEnabled = !imported;
        model.ToolTip = resolution.ToolTip = imported ? "The imported mask is used directly. Choose AI mask to use these settings." : null;
    }
    private void SelectMaskChoice()
    {
        if (loading || maskChoice.SelectedItem is not MaskChoice choice || choice.Id == settings.SelectedMaskId) return;
        try
        {
            if (choice.Id != null)
            {
                var selected = ManualMaskStore.Load(settings.SkyImage, choice.Id).Variant;
                if (!ManualMaskStore.IsCompatible(selected, settings))
                    throw new InvalidOperationException("Use this mask's original AI model, resolution and disk setting before selecting it.");
            }
            settings.SelectedMaskId = choice.Id;
            if (!string.IsNullOrEmpty(settings.SkyImage) && File.Exists(PortablePaths.Resolve(settings.SkyImage)))
            {
                string hash = ManualMaskStore.PhotoHash(settings.SkyImage);
                settings.LastMaskByPhoto = new(settings.LastMaskByPhoto);
                if (choice.Id == null) settings.LastMaskByPhoto.Remove(hash);
                else settings.LastMaskByPhoto[hash] = choice.Id;
            }
            Changed(); ShowSelectedMaskPreview();
        }
        catch (Exception ex) { RefreshMaskChoices(); ShowError(ex); }
    }
    private void ShowSelectedMaskPreview()
    {
        try
        {
            if (settings.SelectedMaskId == null)
            {
                if (AppServices.LoadCachedAiMask(settings) is { } ai) ShowMask(ai);
                else if (File.Exists(PortablePaths.Resolve(settings.SkyImage)))
                {
                    using var unmaskedPhoto = Cv2.ImRead(PortablePaths.Resolve(settings.SkyImage), ImreadModes.Color);
                    if (unmaskedPhoto.Empty()) throw new InvalidDataException("The selected photograph could not be decoded.");
                    displayedPhoto = AppServices.Preview(unmaskedPhoto);
                    photo.SetImage(Bitmap(displayedPhoto), unmaskedPhoto.Width, unmaskedPhoto.Height);
                    orientedPhotoWidth = unmaskedPhoto.Width; orientedPhotoHeight = unmaskedPhoto.Height;
                    RefreshFieldOfView();
                    maskInfo.Text = "Photo selected · Update results for AI, or import an existing mask";
                }
                else { orientedPhotoWidth = orientedPhotoHeight = 0; RefreshFieldOfView(); }
                return;
            }
            var (variant, png) = ManualMaskStore.Load(settings.SkyImage, settings.SelectedMaskId);
            if (!ManualMaskStore.IsCompatible(variant, settings))
                throw new InvalidDataException("The selected edit was made with different AI settings.");
            using var original = Cv2.ImRead(PortablePaths.Resolve(settings.SkyImage), ImreadModes.Color);
            using var binary = Cv2.ImDecode(png, ImreadModes.Grayscale);
            if (original.Empty() || original.Width != variant.BaseMask.Width || original.Height != variant.BaseMask.Height)
                throw new InvalidDataException("Saved mask and selected photo dimensions differ.");
            ShowMask(new MaskAsset(AppServices.Preview(original), AppServices.Preview(binary), variant.BaseMask with { Png = png },
                variant.Description, variant));
        }
        catch (Exception ex) { orientedPhotoWidth = orientedPhotoHeight = 0; RefreshFieldOfView(); ShowError(ex); }
    }
    private void OpenMaskEditor()
    {
        var source = displayedAsset;
        if (source == null || updating || calibrating) return;
        try
        {
            string path = PortablePaths.Resolve(settings.SkyImage), photoHash = ManualMaskStore.PhotoHash(path);
            string? openingVariantId = settings.SelectedMaskId;
            long openingRevision = revision;
            using var original = Cv2.ImRead(path, ImreadModes.Color);
            if (original.Empty() || original.Width != source.Result.Width || original.Height != source.Result.Height)
                throw new InvalidDataException("The selected photo no longer matches the displayed mask.");
            Cv2.ImEncode(".png", original, out byte[] orientedPhoto);
            var editor = new MaskEditorWindow(new(orientedPhoto, source.Png, source.Disk.CenterX, source.Disk.CenterY,
                source.Disk.Radius, "Edited " + DateTime.Now.ToString("dd MMM yyyy HH:mm"), (png, label) =>
                {
                    if (revision != openingRevision || settings.SelectedMaskId != openingVariantId || ManualMaskStore.PhotoHash(path) != photoHash)
                        throw new IOException("The source photo or selected mask changed while the editor was open. Reopen the editor.");
                    if (openingVariantId != null)
                    {
                        var (current, _) = ManualMaskStore.Load(path, openingVariantId);
                        if (current.PngSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source.Png)))
                            throw new IOException("The opening edited mask changed on disk. Reopen the editor.");
                    }
                    var variant = ManualMaskStore.Save(path, source.Result, source.AiAncestorSha256, openingVariantId, label, png,
                        source.Variant == null ? source.Png : null);
                    settings.SelectedMaskId = variant.Id;
                    settings.LastMaskByPhoto = new(settings.LastMaskByPhoto) { [photoHash] = variant.Id };
                    Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview();
                    return true;
                })) { Owner = owner };
            editor.ShowDialog();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Changed(bool saveSettings = true, bool refreshSources = false, bool verifyArtifacts = false)
    {
        if (loading || closing) return;
        try
        {
            var next = InputDependencies.Capture(settings, uiInputs, false);
            var forced = invalidFields.Aggregate(ArtifactGroup.None, (a, f) => a | InputDependencies.ForField(f, settings));
            var changed = (uiInputs?.Difference(next) ?? ArtifactGroup.All) | forced | uiInvalid;
            uiInputs = next; uiInvalid = forced;
            if (changed != ArtifactGroup.None)
            {
                revision++; pending?.Cancel(); export.IsEnabled = false;
                ApplyInvalidation(changed); RefreshInputState();
            }
            if (changed != ArtifactGroup.None || refreshSources || verifyArtifacts) QueueObservation(refreshSources, verifyArtifacts);
            WatchSources();
        }
        catch (Exception ex)
        { revision++; pending?.Cancel(); ApplyInvalidation(ArtifactGroup.All); ShowError(ex); }
        if (saveSettings) SaveInputs();
    }
    private void QueueObservation(bool refreshSources, bool verifyArtifacts)
    {
        var previous = inputWork; var snapshot = settings with { }; var invalid = invalidFields.ToArray(); long observedRevision = revision;
        observations++; if (verifyArtifacts) fullCheckQueued = true;
        controller.Verifying = true; RefreshButtons();
        inputWork = ObserveAsync();
        async Task ObserveAsync()
        {
            try
            {
                await previous;
                if (closing) return;
                var result = await Task.Run(() =>
                {
                    var prepared = services.PrepareInputs(snapshot);
                    var changed = services.ObserveInputs(prepared, invalid, refreshSources, verifyArtifacts);
                    Evaluation? restored = null;
                    if (invalid.Length == 0 && (changed != ArtifactGroup.None || updateState != UpdateState.UpToDate))
                        restored = services.TryRestoreAccepted(prepared);
                    return (prepared, changed, restored);
                });
                if (closing) return;
                if (result.prepared != snapshot)
                {
                    // A calculation may already be waiting, or the user may have edited
                    // other fields. Merge only preserved paths that are still selected.
                    foreach (string name in new[] { "CalibrationFolder", "SkyFolder", "SkyImage", "ProfilePath", "ImportPath" })
                    {
                        var property = typeof(UserSettings).GetProperty(name)!;
                        if (Equals(property.GetValue(settings), property.GetValue(snapshot)))
                            property.SetValue(settings, property.GetValue(result.prepared));
                    }
                    bool wasLoading = loading; loading = true;
                    try { foreach (string name in new[] { "CalibrationFolder", "SkyFolder" }) fields[name].Text = (string)typeof(UserSettings).GetProperty(name)!.GetValue(settings)!; }
                    finally { loading = wasLoading; }
                    WatchSources(); SaveInputs();
                }
                if (observedRevision != revision) return;
                if (refreshSources || reviewedProfile != settings.ProfilePath) RefreshCalibrationReview();
                uiInputs = InputDependencies.Capture(settings, uiInputs, false);
                if (refreshSources && result.changed != ArtifactGroup.None)
                {
                    revision++; pending?.Cancel(); ApplyInvalidation(result.changed); RefreshInputState();
                }
                if (result.restored != null && !updating && !calibrating)
                    PresentResult(result.restored, restored: true);
            }
            catch (Exception ex) { if (!closing && observedRevision == revision) { revision++; pending?.Cancel(); ApplyInvalidation(ArtifactGroup.All); ShowError(ex); } }
            finally
            {
                observations--; if (verifyArtifacts) fullCheckQueued = false;
                controller.Verifying = observations > 0;
                if (!closing) RefreshButtons();
            }
        }
    }
    private void WatchSources() => sourceWatch.SetFiles(InputDependencies.Capture(settings, uiInputs, false).SourcePaths);
    public void RecheckSources()
    {
        if (closing) return;
        if (updating || calibrating) { checkAfterUpdate = true; return; }
        if (!fullCheckQueued) QueueObservation(true, true);
    }
    public Task RecheckSourcesAsync() { RecheckSources(); return inputWork; }
    private void ApplyInvalidation(ArtifactGroup groups)
    {
        if ((groups & ArtifactGroup.Mask) != 0)
        {
            bool geometryChanged = (groups & (ArtifactGroup.Profile | ArtifactGroup.Orientation)) != 0;
            if (geometryChanged) { photo.ClearImage(); displayedPhoto = null; orientedPhotoWidth = orientedPhotoHeight = 0; }
            maskPreview.ClearImage(); displayedMaskPng = null;
            displayedAsset = null; editMask.IsEnabled = false;
            maskInfo.Text = "Sky inputs changed · click Update results";
        }
        if ((groups & (ArtifactGroup.Profile | ArtifactGroup.Mask)) != 0) RefreshFieldOfView();
        if ((groups & ArtifactGroup.Orientation) != 0) InvalidateOrientation("Cardinal directions waiting for Update results");
        if ((groups & ArtifactGroup.SunPath) != 0) ClearSunPath("Sun path waiting for Update results");
        MarkPreviousResults();
    }
    private void SaveInputs()
    { if (!loading) try { AppData.SaveSettings(settings); } catch (Exception ex) { detail.Text = "Could not save settings: " + ex.Message; } }
    private void MarkPreviousResults()
    {
        if (completed == null) return;
        chart.Opacity = .5; beforeEnergy.Opacity = afterEnergy.Opacity = reduction.Opacity = .5;
        provenance.Text = "Previous results — update required";
    }
    private string? InputProblem() => invalidFields.Any(f => InputDependencies.ForField(f, settings) != ArtifactGroup.None)
        ? "Correct the highlighted calculation fields: " + string.Join(", ", invalidFields.Where(f => InputDependencies.ForField(f, settings) != ArtifactGroup.None).Order()) : UpdateInputs.Problem(settings);
    private void RefreshInputState()
    {
        string? problem = InputProblem();
        SetUpdateState(problem == null ? UpdateState.UpdateRequired : UpdateState.NeedsAttention,
            problem ?? "Inputs changed or not yet calculated. Click Update results when ready.");
    }
    private void SetUpdateState(UpdateState state, string message)
    {
        updateState = state;
        string label = state switch
        {
            UpdateState.NeedsAttention => "Needs attention", UpdateState.UpdateRequired => "Update required",
            UpdateState.Updating => "Updating…", UpdateState.UpToDate => "Results up to date",
            UpdateState.Stopped => "Update stopped", _ => "Update failed"
        };
        updateIndicator.Text = "● " + label;
        updateIndicator.Foreground = state == UpdateState.UpToDate ? accent
            : state is UpdateState.NeedsAttention or UpdateState.Failed ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(145, 100, 0));
        System.Windows.Automation.AutomationProperties.SetName(updateIndicator, label);
        status.Text = label; detail.Text = message;
        RefreshButtons();
    }
    private void RefreshButtons()
    {
        update.IsEnabled = refreshData.IsEnabled = !controller.DependentBusy && !updating && !calibrating && InputProblem() == null;
        refreshData.IsEnabled &= string.IsNullOrEmpty(settings.ImportPath);
        update.ToolTip = controller.DependentBusy ? "Wait for PV Autonomy or stop its operation first." : "Recalculate changed inputs; reuse matching weather, calibration and masks.";
        refreshData.ToolTip = controller.DependentBusy ? "Wait for PV Autonomy or stop its operation first." : string.IsNullOrEmpty(settings.ImportPath)
            ? "Download fresh weather from the selected provider, then update. Unchanged calibration and masks are reused."
            : "Imported file selected: use Update results to reread it. There is no weather download to refresh.";
        stop.IsEnabled = updating && pending?.IsCancellationRequested == false;
        controller.RefreshSource();
        export.IsEnabled = !controller.Verifying && !exporting && !updating && !calibrating && updateState == UpdateState.UpToDate && completed != null &&
            services.IsCurrent(completed) && completed.ManagedDebugRun?.HasCompleteDataset == true;
        editMask.IsEnabled = displayedAsset != null && !updating && !calibrating && !exporting;
        choosePhoto.IsEnabled = !calibrating && !exporting;
        importMask.IsEnabled = !updating && !calibrating && !exporting && File.Exists(PortablePaths.Resolve(settings.SkyImage));
        savedStudies.IsEnabled = !updating && !calibrating && !exporting && !controller.DependentBusy && !controller.Verifying;
    }
    public void StopUpdate()
    {
        if (!updating) return;
        revision++; pending?.Cancel(); MarkPreviousResults();
        InvalidateOrientation("Update stopped · click Update results to retry");
        ClearSunPath("Update stopped · click Update results to retry");
        SetUpdateState(InputProblem() == null ? UpdateState.Stopped : UpdateState.NeedsAttention,
            InputProblem() ?? "Stopped. Active native work may finish in the background; results will not be accepted.");
    }
    private void UpdateTimeZoneControls()
    {
        bool previous = loading; loading = true;
        useSystemZone.IsChecked = settings.UseSystemTimeZone; zone.IsEnabled = !settings.UseSystemTimeZone;
        zone.SelectedValue = settings.Zone;
        zone.ToolTip = (zone.SelectedItem as TimeZoneInfo)?.DisplayName;
        loading = previous;
    }
    private void ChangeTimeZoneMode(bool automatic)
    {
        if (loading) return;
        settings.UseSystemTimeZone = automatic;
        TimeZoneSelection.ApplySystemZone(settings, systemZone());
        UpdateTimeZoneControls(); Changed();
    }
    public void RefreshSystemTimeZone()
    {
        if (closing || !TimeZoneSelection.ApplySystemZone(settings, systemZone())) return;
        UpdateTimeZoneControls(); Changed();
    }
    private void SystemTimeChanged(object? sender, EventArgs e)
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshSystemTimeZone); }
    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    { if (e.Category == UserPreferenceCategory.Locale) SystemTimeChanged(sender, e); }
    public async void Begin(bool refresh)
    {
        if (closing || updating || calibrating || controller.DependentBusy) return;
        if (TimeZoneSelection.ApplySystemZone(settings, systemZone())) { UpdateTimeZoneControls(); Changed(); }
        if (InputProblem() is { } problem) { SetUpdateState(UpdateState.NeedsAttention, problem); return; }
        controller.Revalidating = !refresh && updateState == UpdateState.UpToDate && completed != null;
        settings.FirstRun = false; SaveInputs();
        pending?.Dispose(); pending = new(); var ct = pending.Token; long current = ++revision; updating = true;
        SetUpdateState(UpdateState.Updating, "Preparing the current inputs…");
        if (refresh || !sunPathIsCurrent) ClearSunPath("Preparing sun path…"); sunPathPublishedEarly = false;
        var snapshot = settings with { };
        long imageRevision = orientationRevision;
        try
        {
            await inputWork;
            if (closing || current != revision) return;
            ct.ThrowIfCancellationRequested();
            snapshot = settings with { };
            var result = await services.Evaluate(snapshot, refresh,
                text => Dispatcher.InvokeAsync(() => { if (current == revision) status.Text = text; }),
                asset => Dispatcher.InvokeAsync(() => { if (current == revision && imageRevision == orientationRevision && !closing) ShowMask(asset); }), ct,
                path => Dispatcher.InvokeAsync(() => { if (current == revision && !closing) { ShowSunPath(path); sunPathPublishedEarly = path != null; } }),
                overlay => Dispatcher.InvokeAsync(() => { if (current == revision && imageRevision == orientationRevision && !closing) ShowCardinals(overlay); }));
            if (current != revision || closing) return;
            PresentResult(result, restored: false);
        }
        catch (OperationCanceledException) { if (current == revision && !closing) SetUpdateState(UpdateState.Stopped, "Update stopped. Click Update results to retry."); }
        catch (Exception ex) { if (current == revision) ShowError(ex); }
        finally
        {
            if (!closing && checkAfterUpdate)
            {
                checkAfterUpdate = false; QueueObservation(true, true); await inputWork;
            }
            updating = false; controller.Revalidating = false;
            if (!closing) RefreshButtons();
        }
    }
    private void PresentResult(Evaluation result, bool restored)
    {
        completed = result; chart.Opacity = 1; beforeEnergy.Opacity = afterEnergy.Opacity = reduction.Opacity = 1;
        ShowWeatherOrigin(result);
        if (result.Mask != null) ShowMask(result.Mask);
        ShowCardinals(result.Cardinals); ShowSunPath(result.SunPath);
        chart.SetData(result.Run.Rows, TimeZoneSelection.Resolve(result.Settings.Zone));
        beforeEnergy.Text = result.Run.BeforeEnergy.ToString("F2"); afterEnergy.Text = result.Run.AfterEnergy?.ToString("F2") ?? "—";
        reduction.Text = result.Run.LossPercent is { } loss ? loss.ToString("F1") + "%" : "—";
        provenance.Text = result.Run.SkyCoverage is { } coverage ? $"{result.Run.Model} · observed sky coverage {coverage:P1} for this panel. Unobserved sky assumed blocked. No ground reflection." : "Baseline ready. Add a compatible camera profile and sky photo for the shaded curve.";
        bool complete = IrradianceReadiness.Inspect(services, result).IsReady;
        SetUpdateState(complete ? UpdateState.UpToDate : UpdateState.NeedsAttention,
            complete ? restored ? $"{result.Run.Rows.Count:N0} intervals · saved result restored · {result.Raw.Source} · {result.Settings.Zone}"
                : $"{result.Run.Rows.Count:N0} intervals · {result.TotalMilliseconds / 1000:F2} s · Debug Data saved · {result.Raw.Source} · {result.Settings.Zone}"
                : "The shaded dataset is incomplete. Check the camera profile and sky photograph.");
    }
    public void ShowMask(MaskAsset asset)
    {
        if (displayedPhoto != null) asset = asset with { OriginalPreview = displayedPhoto };
        if (!ReferenceEquals(displayedPhoto, asset.OriginalPreview))
        {
            photo.SetImage(Bitmap(asset.OriginalPreview), asset.Result.Width, asset.Result.Height);
            displayedPhoto = asset.OriginalPreview; displayedCardinals = null; currentCardinals = null;
        }
        // The mask and overlay share one native-resolution viewport; preview rounding cannot misalign them.
        if (!ReferenceEquals(displayedMaskPng, asset.Png))
        {
            maskPreview.SetMask(Bitmap(asset.Png)); displayedMaskPng = asset.Png;
            if (sunPathIsCurrent && completed?.SunPath != null) ShowSunPath(completed.SunPath);
        }
        maskInfo.Text = asset.Description;
        displayedAsset = asset; orientedPhotoWidth = asset.Result.Width; orientedPhotoHeight = asset.Result.Height;
        RefreshFieldOfView(); RefreshButtons();
    }
    private void RefreshFieldOfView()
    {
        var disk = displayedAsset?.Disk;
        var view = SkyPhotoFieldOfView.Load(settings, orientedPhotoWidth, orientedPhotoHeight,
            disk == null ? null : new ImageDisk(disk.CenterX, disk.CenterY, disk.Radius));
        fieldOfViewInfo.Text = view.Text; fieldOfViewInfo.ToolTip = view.Explanation;
    }
    public static string OrientationInputKey(UserSettings s) => AppData.Key(new
    { s.SkyImage, s.ProfilePath, s.Model, s.Resolution, s.CenteredDisk, s.CoverageAngle, s.BottomAzimuth, s.CameraTilt, s.CameraRoll });
    private void InvalidateOrientation(string message)
    {
        orientationRevision++;
        photo.ClearOverlay(); displayedCardinals = null; currentCardinals = null; cardinalInfo.Text = message;
    }
    private void ShowCardinals(CardinalDirectionOverlayResult? result)
    {
        currentCardinals = result;
        if (result == null)
        {
            photo.ClearOverlay(); displayedCardinals = null;
            cardinalInfo.Text = "Cardinal directions unavailable · add a calibrated sky photo"; return;
        }
        if (!ReferenceEquals(displayedCardinals, result.Png)) { photo.SetOverlay(Bitmap(result.Png)); displayedCardinals = result.Png; }
        photo.ShowOverlay = settings.ShowCardinalDirections;
        cardinalInfo.Text = result.HasMarkers ? "Cardinal bearings · calibrated image boundary" : "No cardinal bearings inside the calibrated view";
    }
    private void SetCardinalVisibility()
    {
        if (loading) return;
        settings.ShowCardinalDirections = showCardinals.IsChecked == true;
        photo.ShowOverlay = settings.ShowCardinalDirections;
        try { AppData.SaveSettings(settings); } catch (Exception ex) { detail.Text = "Could not save settings: " + ex.Message; }
    }
    private void ClearSunPath(string message)
    { maskPreview.ClearOverlay(); sunPathIsCurrent = false; sunPathInfo.Text = message; }
    private void ShowSunPath(SunPathOverlayResult? result)
    {
        if (result == null) { ClearSunPath("Sun path unavailable · add a calibrated sky photo"); return; }
        maskPreview.SetOverlay(Bitmap(result.Png)); maskPreview.ShowOverlay = settings.ShowSunPath;
        sunPathIsCurrent = true;
        sunPathInfo.Text = result.HasPaths ? "Sun path · selected period" : "No sun path inside the calibrated view for this period";
    }
    private void SetSunPathVisibility()
    {
        if (loading) return;
        settings.ShowSunPath = showSunPath.IsChecked == true;
        maskPreview.ShowOverlay = settings.ShowSunPath;
        try { AppData.SaveSettings(settings); } catch (Exception ex) { detail.Text = "Could not save settings: " + ex.Message; }
    }
    public static BitmapImage Bitmap(byte[] bytes) { var b = new BitmapImage(); using var stream = new MemoryStream(bytes); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; }
    private void ShowError(Exception ex)
    {
        var error = ex is AggregateException a ? a.Flatten().InnerExceptions[0] : ex;
        MarkPreviousResults(); SetUpdateState(UpdateState.Failed, error.Message);
        try { File.AppendAllText(AppData.PathFor("errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); } catch { }
    }
    private void PickCalibrationFolder() { var p = new OpenFolderDialog { Title = "Choose checkerboard photographs" }; if (p.ShowDialog(owner) == true) { settings.CalibrationFolder = PortablePaths.Store(p.FolderName); LoadFields(); Changed(); } }
    private void PickSkyPhoto()
    {
        var pick = new OpenFileDialog { Title = "Choose a sky photograph", Filter = "Sky photographs|*.jpg;*.jpeg;*.png" };
        if (pick.ShowDialog(owner) == true) SelectSkyPhoto(pick.FileName);
    }
    private void SelectSkyPhoto(string path)
    {
        settings.SkyImage = PortablePaths.Store(path); settings.SkyFolder = PortablePaths.Store(Path.GetDirectoryName(Path.GetFullPath(path))!);
        RestorePhotoMaskSelection(); LoadFields(); Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview();
    }
    private void PickMask()
    {
        var pick = new OpenFileDialog { Title = "Import an existing mask without AI", Filter = "Black-and-white PNG|*.png" };
        if (pick.ShowDialog(owner) != true) return;
        var dialog = new Window { Owner = owner, Title = "Import existing mask · no AI", Width = 510, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(20) }; dialog.Content = panel;
        panel.Children.Add(Label("Photo: " + Path.GetFileName(settings.SkyImage), 13));
        panel.Children.Add(Label("Mask: " + Path.GetFileName(pick.FileName), 13));
        panel.Children.Add(Label("Use the same pixel dimensions as the oriented photo. Gray, colored and transparent pixels are rejected. Pixels outside the photo's lens disk become black.", 12));
        panel.Children.Add(Label("Mask name", 12)); var name = new TextBox { Text = Path.GetFileNameWithoutExtension(pick.FileName), MaxLength = 100 }; panel.Children.Add(name);
        var polarity = Combo(panel, "Meaning of the source pixels", ["White = open sky; black = obstruction", "Black = open sky; white = obstruction (invert)"]); polarity.SelectedIndex = 0;
        var disk = Combo(panel, "Lens disk in the photograph", ["Detect from photo", "Centered full disk"]); disk.SelectedIndex = settings.CenteredDisk ? 1 : 0;
        var error = Label("", 12); error.Foreground = Brushes.Firebrick; panel.Children.Add(error);
        var commands = new WrapPanel(); panel.Children.Add(commands);
        bool importing = false;
        dialog.Closing += (_, e) => e.Cancel = importing;
        commands.Children.Add(Button("Import and select mask", async () =>
        {
            string photoPath = settings.SkyImage, label = name.Text; bool inverted = polarity.SelectedIndex == 1, centeredDisk = disk.SelectedIndex == 1;
            importing = true; panel.IsEnabled = false; error.Text = "Checking and importing mask…";
            try
            {
                var variant = await Task.Run(() => ManualMaskStore.Import(photoPath, pick.FileName, label, inverted, centeredDisk));
                if (closing || settings.SkyImage != photoPath) throw new IOException("The selected photo changed. Choose the mask again.");
                SelectImportedMask(variant); importing = false; dialog.Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { importing = false; panel.IsEnabled = true; }
        }));
        commands.Children.Add(Button("Cancel", dialog.Close)); dialog.ShowDialog();
    }
    private MaskVariant ImportExistingMask(string path, string label, bool inverted, bool centeredDisk)
    {
        var variant = ManualMaskStore.Import(settings.SkyImage, path, label, inverted, centeredDisk);
        SelectImportedMask(variant); return variant;
    }
    private void SelectImportedMask(MaskVariant variant)
    {
        settings.SelectedMaskId = variant.Id;
        settings.LastMaskByPhoto = new(settings.LastMaskByPhoto) { [variant.PhotoSha256] = variant.Id };
        Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview();
    }
    private void PickSkyFolder() { var p = new OpenFolderDialog { Title = "Choose the folder containing your sky photograph" }; if (p.ShowDialog(owner) == true) { settings.SkyFolder = PortablePaths.Store(p.FolderName); settings.SkyImage = ""; settings.SelectedMaskId = null; LoadFields(); RestorePhotoMaskSelection(); Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview(); } }
    public static OpenFileDialog CameraProfilePicker() => new()
    {
        Title = "Load camera parameters · camera-profile.json",
        Filter = "Camera profiles (*.json)|*.json",
        InitialDirectory = Directory.Exists(Path.Combine(AppData.Root, "Profiles")) ? Path.Combine(AppData.Root, "Profiles") : AppData.Root
    };
    private void PickProfile()
    {
        var picker = CameraProfilePicker(); if (picker.ShowDialog(owner) != true) return;
        if (!Path.GetExtension(picker.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            status.Text = "Choose camera-profile.json";
            detail.Text = "This file includes the validated lens coverage. For a native YAML calibration, choose its matching camera-profile.json or use Calibrate to create one.";
            return;
        }
        settings.ProfilePath = PortablePaths.Store(picker.FileName); settings.CoverageAngle = 0; LoadFields(); Changed();
    }
    private void RefreshCalibrationReview()
    {
        string? fingerprint = ProfileFingerprint();
        CalibrationReview? savedReview = null;
        if (calibrationReview?.Report.Failure != null && reviewedProfile == settings.ProfilePath && fingerprint == reviewedProfileFingerprint)
        {
            var failed = CalibrationReview.TryLoad(calibrationReview.Directory);
            if (failed?.EvidenceFingerprint == calibrationReview.EvidenceFingerprint) savedReview = failed;
        }
        savedReview ??= string.IsNullOrWhiteSpace(settings.ProfilePath) ? null : CalibrationReview.ForProfile(settings.ProfilePath);
        if (savedReview == null && calibrationReview != null && fingerprint == reviewedProfileFingerprint)
        {
            var previous = CalibrationReview.TryLoad(calibrationReview.Directory);
            // Identical relocated profiles may retain their original, still verified sibling evidence.
            if (fingerprint != null && previous?.EvidenceFingerprint == calibrationReview.EvidenceFingerprint &&
                previous.MatchesProfile(settings.ProfilePath)) savedReview = previous;
        }
        calibrationReview = savedReview;
        reviewedProfile = settings.ProfilePath;
        reviewedProfileFingerprint = fingerprint;
        reviewCalibration.IsEnabled = calibrationReview != null;
        calibrationInfo.Text = string.IsNullOrWhiteSpace(settings.ProfilePath) ? "No camera profile selected" :
            "Camera profile: " + settings.ProfilePath;
        if (calibrationReview != null) calibrationInfo.Text += "\n" + calibrationReview.Summary;
        if (calibrationReview?.FitSummary != null) calibrationInfo.Text += "\n" + calibrationReview.FitSummary;
        if (calibrationReview?.Report.Failure != null) calibrationInfo.Text += "\nLast calibration failed; open the review for details.";
        calibrationInfo.ToolTip = string.IsNullOrWhiteSpace(settings.ProfilePath) ? null : PortablePaths.Resolve(settings.ProfilePath);
    }
    private string? ProfileFingerprint()
    {
        if (string.IsNullOrWhiteSpace(settings.ProfilePath)) return null;
        try { return AppData.FileKey(PortablePaths.Resolve(settings.ProfilePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private void ShowCalibrationReview()
    {
        if (calibrationReview == null) return;
        calibrationReviewWindow?.Close();
        var dialog = new CalibrationReviewWindow(calibrationReview, owner);
        calibrationReviewWindow = dialog;
        dialog.Closed += (_, _) => { if (calibrationReviewWindow == dialog) calibrationReviewWindow = null; };
        dialog.Show();
    }
    private void PickImport() { var p = new OpenFileDialog { Title = "Choose horizontal irradiance: timestamp, BHI and DHI in W/m²", Filter = "Horizontal irradiance|*.xlsx;*.csv" }; if (p.ShowDialog(owner) == true) SelectWeatherFile(p.FileName); else RefreshWeatherSource(); }
    public void SelectWeatherFile(string path)
    { settings.ImportPath = PortablePaths.Store(path); Changed(); RefreshWeatherSource(); status.Text = "Imported source selected · check dates, site, time zone and source interval metadata"; detail.Text = path; }
    private void ShowWeatherOrigin(Evaluation result)
    {
        RefreshWeatherSource();
        string origin = "Source origin unavailable";
        try
        {
            var manifest = result.DebugDirectory is { } directory ? AppData.Read<DebugDataRun.Manifest>(Path.Combine(directory, "run.json")) : null;
            if (manifest?.Stages.TryGetValue("03-irradiance", out var stage) == true) origin = stage.Origin;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        lastWeatherResult.Text = $"Last result: {origin}. Source: {result.Raw.Source}. Dataset timestamp: {result.Raw.FetchedUtc.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.";
    }
    private async Task Calibrate()
    {
        if (calibrating || updating || controller.DependentBusy) return;
        if (invalidFields.Count > 0) { status.Text = "Correct the highlighted fields first"; return; }
        calibrating = true;
        pending?.Cancel(); revision++; export.IsEnabled = false;
        MarkPreviousResults(); SetUpdateState(UpdateState.Updating, "Calibrating checkerboard photographs…");
        ClearSunPath("Sun path waiting for the new calibration");
        InvalidateOrientation("Cardinal directions waiting for the new calibration");
        var snapshot = settings with { }; status.Text = "Calibrating checkerboard photographs…";
        try
        {
            var result = await services.Calibrate(snapshot); if (closing) return;
            calibrationReview = CalibrationReview.TryLoad(Path.GetDirectoryName(result.Path)!);
            reviewCalibration.IsEnabled = calibrationReview != null;
            if (settings.CalibrationFolder != snapshot.CalibrationFolder || settings.Columns != snapshot.Columns || settings.Rows != snapshot.Rows || settings.SquareMm != snapshot.SquareMm)
            { status.Text = "Calibration saved for the previous board inputs"; detail.Text = result.Path; ShowCalibrationReview(); return; }
            settings.ProfilePath = PortablePaths.Store(result.Path); settings.CoverageAngle = 0; LoadFields(); status.Text = "Calibration saved"; detail.Text = result.Path; Changed(); ShowCalibrationReview();
        }
        catch (CalibrationReviewException ex)
        {
            if (closing) return;
            calibrationReview = CalibrationReview.TryLoad(ex.DiagnosticsDirectory); reviewCalibration.IsEnabled = calibrationReview != null;
            reviewedProfile = settings.ProfilePath;
            reviewedProfileFingerprint = ProfileFingerprint();
            RefreshCalibrationReview();
            ShowError(ex); ShowCalibrationReview();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { calibrating = false; if (!closing) { if (updateState == UpdateState.Updating) RefreshInputState(); else RefreshButtons(); } }
    }
    public Task LoadExample()
    {
        pending?.Cancel();
        TimeZoneSelection.ApplySystemZone(settings, systemZone());
        settings = PortablePaths.Example() with { FirstRun = false, Zone = settings.Zone, UseSystemTimeZone = settings.UseSystemTimeZone, ShowSunPath = settings.ShowSunPath, ShowCardinalDirections = settings.ShowCardinalDirections };
        LoadFields(); Changed();
        return Task.CompletedTask;
    }
    private void ShowHelp()
    {
        var text = "1. Select checkerboard photographs. Enter INNER columns/rows and the size of ONE square in mm, then Calibrate. Calibrate saves native calibration.yml, camera-profile.json and diagnostics in Debug Data/01-calibration, with durable copies under Data/Profiles.\n\n2. Select your sky photo folder, then its active photo and segmentation model. The physical lens and oriented image dimensions must match calibration. The mask is saved as a black-and-white PNG. Show sun path adds a separate transparent preview for the selected period as soon as solar geometry is ready. The toggle only changes its visibility; the binary mask remains unchanged. Show cardinal directions adds a separate orientation overlay to the original colored photograph. It is generated during Update results before irradiance retrieval, and reused when its inputs are unchanged. The letters follow the same camera convention as sun paths and shading.\n\n3. Set camera pose, panel angles, site, dates and time zone. Dates include the complete end day. Positive camera tilt points toward image top before additional roll; bottom south = bearing 180°.\n\n4. Edit your inputs, then click Update results. Edits never start calculations. Yellow means an update is needed or running, red means attention is needed, and green means complete results match the current inputs. Export is available only when green. Irradiance data determines the output intervals: hourly inputs stay hourly and 15-minute inputs stay 15-minute. The current NASA POWER and Open-Meteo endpoints supply hourly means. Solar integration samples improve geometry within each interval; they do not create finer weather data.\n\nImport XLSX/CSV uses a header followed by timestamp, BHI and DHI (W/m²). Native exported workbooks carry their own interval IDs, bounds and conventions. For a three-column file, set the interval duration in minutes and choose start, end or center labels. Local Excel dates and DD.MM.YYYY HH:mm use the selected time zone; ambiguous daylight-saving times need an explicit offset. Provide complete coverage of both selected date boundaries. Missing periods are reported.\n\n5. Tune the panel. Both graph curves are irradiance ON THE PANEL, using Hay–Davies or the isotropic option in Historical Data. Circumsolar diffuse follows the 0.25° solar disk. Unseen sky is conservatively blocked; fitted calibration coverage is provisional.\n\nEach explicit update maintains one current Debug Data set beside APPLICATION.exe: 01-calibration (YAML/profile), 02-sky-mask (PNG), 02-orientation (cardinal PNG/XLSX/JSON), 03-irradiance (XLSX), 04-solar-positions (XLSX), 05-transposition (XLSX), and 06-shading (visibility, transmission and shaded results). run.json records stage origins, saved files, fingerprints and the current status. Only a complete calculation is marked Complete; calibration or orientation alone is Partial. Unavailable stages are marked skipped. Edits remove affected files from the latest run, hide stale overlays and disable export. Camera rotation preserves the mask and numerical solar positions; panel edits preserve upstream files. Source-file changes are checked without recalculating. Close any locked diagnostic workbook and retry Update. Valid files are kept unchanged. Replacements are staged before publication. Verified old run folders are migrated after protecting selected inputs under Data/Inputs. Close the other SolarShade instance if this dataset is already in use.\n\nExport results copies the verified current debug files and adds a one-page Summary.pdf with the saved parameters and results. It creates a new scenario folder only after every file is ready. Export requires a complete shaded dataset; a baseline alone is insufficient. If inputs change during export, retry after Update results. Example/Irradiance holds the bundled input workbook; Example/Debug Data/reference-run holds a fixed library-generated reference run. Your current dataset uses the top-level Debug Data folder; the bundled reference stays unchanged.\n\nKeep APPLICATION.exe, Example, Data and Debug Data together when moving the package. Data stores settings, caches, extracted model weights and durable Profiles/Inputs. Keep these durable inputs when moving or backing up the application. Your own inputs may be anywhere. Data location:\n" + AppData.Root + "\n\nSolar Irradiance estimates historical direct + sky-diffuse irradiance. Once its complete shaded dataset is current, open PV Autonomy to enter a daily consumption profile, panel area and efficiencies, battery capacity and initial charge. Evaluate system computes hourly battery charge and unmet load for this study. First model use is slower; later panel edits reuse expensive work.";
        text += "\n\nCoordinates and orientation: Enter decimal degrees, for example latitude 43.56, longitude 1.48. South and west coordinates are negative. True north is geographic north; magnetic-compass correction is not automatic. Bearings increase clockwise: N 0°, E 90°, S 180°, W 270°. Bottom bearing sets the image direction before additional roll. With roll 0 degrees, it is the direction at the image bottom. With an upward camera, bottom south and roll 0°, east appears left and west right because you are looking up.";
        text += "\n\nChoosing photos and existing masks: Choose sky photo opens a photograph directly; the folder and active-photo list follow your choice. Import existing mask reads an already segmented black-and-white PNG without AI. Choose whether white or black means open sky; white is the default. The mask must match the oriented photo's dimensions, use exact black/white grayscale pixels and be opaque. Lens-disk detection uses the photo, not the mask; choose Centered full disk only for a centered full-disk photo. Outside-lens pixels become black. Give the mask a name. Import selects a durable copy immediately, ready for Update results; the original import file is no longer required. AI settings are unavailable while an imported mask is selected. Choose AI mask in Mask for calculation to use segmentation again.";
        text += "\n\nMask editing: Click Edit mask on an imported mask or a generated AI mask. The separate window blocks main-study changes until you save or cancel. Paint black for obstruction or white for open sky; change brush size, mask opacity and zoom while editing. Saving creates a new named variant and selects it in Mask for calculation. Original masks stay available. A new variant needs Update results; returning to a previously accepted exact photo, mask and study restores verified results and export without recalculation. Exports include mask-provenance.json, identifying imported pixels, explicit inversion and subsequent edits. Keep Data/Masks and Data/AcceptedResults when moving the application.";
        HelpWindow.Open(ref helpWindow, owner, "Using SolarShade · preview 0.2.0", text, panel =>
            panel.Children.Add(Button("Third-party notices", () =>
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices")!;
                using var reader = new StreamReader(stream);
                HelpWindow.Open(ref noticesWindow, helpWindow!, "Third-party notices", reader.ReadToEnd());
            })));
    }
    private void ShowSavedStudies()
    {
        try
        {
            var (count, bytes) = AcceptedResultVault.StorageUsage();
            string message = $"{count} saved studies use {bytes / 1048576.0:F1} MB. They let the app restore earlier photo, mask and study results without recalculating.\n\nClear saved studies now? The current result and saved masks remain available.";
            if (MessageBox.Show(owner, message, "Saved studies", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int removed = AcceptedResultVault.ClearKnown();
            MessageBox.Show(owner, $"Removed {removed} saved studies. Your current result and edited masks are unchanged.", "Saved studies");
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Saved studies could not be cleared", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    public Evaluation? Completed => completed;
    public string FieldOfViewTextForTest => fieldOfViewInfo.Text;
    public CalibrationReview? CalibrationReviewForTest => calibrationReview;
    public Task<(CalibrationProfile Profile, string Path, string Details)> CalibrateForTest(UserSettings inputs) => services.Calibrate(inputs);
    public void SetProfileForTest(string path) { settings.ProfilePath = PortablePaths.Store(path); settings.CoverageAngle = 0; LoadFields(); Changed(); }
    public void SelectSkyPhotoForTest(string path) => SelectSkyPhoto(path);
    public MaskVariant ImportMaskForTest(string path, string label, bool inverted = false, bool centeredDisk = true) => ImportExistingMask(path, label, inverted, centeredDisk);
    public void SelectMaskForTest(string? id) => maskChoice.SelectedItem = maskChoice.Items.Cast<MaskChoice>().Single(c => c.Id == id);
    public void Calculate() => Begin(false);
    public void SetTimeZoneForTest(bool automatic, string zoneId)
    { useSystemZone.IsChecked = automatic; if (!automatic) zone.SelectedValue = zoneId; }
    public void SetPanelForTest(double tilt, double azimuth) { settings.PanelTilt = tilt; settings.PanelAzimuth = azimuth; LoadFields(); Changed(); }
    public void SetDiffuseModelForTest(bool useIsotropic) => isotropic.IsChecked = useIsotropic;
    public void CommitFieldForTest(string property, string value)
    {
        var field = fields[property]; field.Text = value;
        field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, field, null)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
    }
    public void SetCameraPoseForTest(double bottomAzimuth, double tilt, double roll)
    { settings.BottomAzimuth = bottomAzimuth; settings.CameraTilt = tilt; settings.CameraRoll = roll; LoadFields(); Changed(); }
    public void SetSunPathVisibleForTest(bool visible) => showSunPath.IsChecked = visible;
    public bool SunPathVisibleForTest => maskPreview.OverlayVisible;
    public bool SunPathPublishedEarlyForTest => sunPathPublishedEarly;
    public long RevisionForTest => revision;
    public UpdateState UpdateStateForTest => updateState;
    public bool ExportEnabledForTest => export.IsEnabled;
    public bool UpdateEnabledForTest => update.IsEnabled;
    public bool UpdatingForTest => updating;
    public void EditFieldForTest(string property, string value) => fields[property].Text = value;
    public void SetCardinalsVisibleForTest(bool visible) => showCardinals.IsChecked = visible;
    public bool CardinalsVisibleForTest => photo.OverlayVisible;
    public CardinalDirectionOverlayResult? CardinalsForTest => currentCardinals;
    public long OrientationRevisionForTest => orientationRevision;
    public int CardinalGenerationCountForTest => services.CardinalGenerationCount;
}
