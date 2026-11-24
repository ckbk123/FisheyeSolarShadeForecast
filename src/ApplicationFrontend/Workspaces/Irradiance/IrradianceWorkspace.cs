using System.Diagnostics;
using System.Globalization;
using SolarShade.Camera;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
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

public sealed partial class IrradianceWorkspace : UserControl, IDisposable
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
    private readonly TextBlock runDetails = Label("", 12), modelDetails = Label("", 12);
    private readonly Expander studyDetails = new() { Header = "Study details", Visibility = Visibility.Collapsed };
    private readonly Button chooseWeatherFile = new() { Content = "Upload weather file…" };
    private readonly WeatherSourceSwitch weatherMode = new();
    private readonly StackPanel serviceWeatherControls = new(), importWeatherControls = new();
    private readonly Button stop = new() { Content = "Stop", IsEnabled = false };
    private Evaluation? completed { get => controller.Completed; set => controller.Completed = value; }
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly HashSet<string> invalidFields = new();
    private readonly TextBlock status = Label("", 13), detail = Label("", 12), maskInfo = Label("Choose a sky photo", 12), calibrationInfo = Label("", 12);
    private readonly Button reviewCalibration = new() { Content = "Review checkerboard photos", IsEnabled = false };
    private readonly Button loadProfile = new() { Content = "Load camera profile…" };
    private CalibrationReview? calibrationReview;
    private CalibrationReviewWindow? calibrationReviewWindow;
    private long profileSelectionRevision;
    private string? reviewedProfile;
    private string? reviewedProfileFingerprint;
    private readonly TextBlock beforeEnergy = Label("—", 27), afterEnergy = Label("—", 27), reduction = Label("—", 27), hover = Label("", 12);
    private readonly TextBlock provenance = Label("", 12);
    private readonly SkyPhotoViewport skyViewport = new();
    private ImageOverlayPreview photo => skyViewport.Photo;
    private readonly CheckBox showCardinals = new() { Content = "Show cardinal directions", VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 8, 0) };
    private readonly TextBlock cardinalInfo = Label("", 12);
    private readonly TextBlock fieldOfViewInfo = Label("FOV unavailable", 11);
    private SkyPhotoFieldOfView currentFieldOfView = SkyPhotoFieldOfView.Unavailable("Choose a compatible sky photo.");
    private int orientedPhotoWidth, orientedPhotoHeight;
    private long orientationRevision;
    private byte[]? displayedPhoto, displayedCardinals;
    private CardinalDirectionOverlayResult? currentCardinals;
    private readonly MaskViewport maskViewport = new();
    private ImageOverlayPreview maskPreview => maskViewport.Layers;
    private readonly TextBlock sunPathInfo = Label("", 12);
    private readonly CheckBox showSunPath = new() { Content = "Show sun path", VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 16, 0) };
    private bool sunPathPublishedEarly;
    private bool sunPathIsCurrent;
    private byte[]? displayedMaskPng;
    private MaskAsset? displayedAsset;
    private readonly Button editMask = new() { Content = "Edit mask", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
    private readonly Button savedStudies = new() { Content = "Saved studies" };
    private readonly Button choosePhoto = new() { Content = "Choose sky photo…", Margin = new(0, 4, 6, 4), Padding = new(10, 6, 10, 6) };
    private readonly Button importMask = new() { Content = "Import existing mask…", Margin = new(0, 4, 6, 4), Padding = new(10, 6, 10, 6) };
    private readonly ComboBox maskChoice = new() { MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
    private sealed record MaskChoice(string? Id, string Title, bool Imported = false) { public override string ToString() => Title; }
    private readonly IrradianceChart chart = new();
    private readonly Button export = new() { Content = "Export data", IsEnabled = false };
    private ComboBox provider = null!, model = null!, resolution = null!, images = null!, zone = null!, window = null!;
    private CheckBox isotropic = null!, useSystemZone = null!;
    private DatePicker graphDay = null!;
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
        settings = WeatherSourceSelection.Normalize(AppData.ReadSettings() ?? PortablePaths.Example());
        TimeZoneSelection.ApplySystemZone(settings, systemZone());
        var root = new DockPanel(); Content = root;
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        Actions = actions;
        actions.Children.Add(Button("Load example", async () => await LoadExample()));
        actions.Children.Add(Button("Help", ShowHelp));
        savedStudies.Click += (_, _) => ShowSavedStudies(); actions.Children.Add(savedStudies);
        export.Click += async (_, _) =>
        {
            await RecheckSourcesAsync();
            var snapshot = completed; if (snapshot == null || !export.IsEnabled) return;
            var rows = snapshot.Run.Rows;
            bool hasSourceGhi = rows.Count > 0 && snapshot.Raw.Intervals.Any(r => r.GlobalHorizontal.HasValue &&
                r.End > rows[0].Start && r.Start < rows[^1].End);
            var selection = new ExportDataWindow(snapshot.Settings.Isotropic, owner, hasSourceGhi: hasSourceGhi);
            if (selection.ShowDialog() != true || selection.SelectedOptions == null || selection.DestinationDirectory == null) return;
            await RecheckSourcesAsync(); if (!export.IsEnabled || completed != snapshot) return;
            exporting = true; RefreshButtons();
            try
            {
                string folder = await Task.Run(() => AppServices.Export(snapshot, selection.DestinationDirectory, selection.SelectedOptions));
                if (!closing) { status.Text = "Exported irradiance.xlsx"; detail.Text = folder; }
            }
            catch (Exception ex) { if (!closing) { status.Text = "Export could not finish"; detail.Text = ex.Message; } }
            finally { exporting = false; if (!closing) { RecheckSources(); RefreshButtons(); } }
        };
        actions.Children.Add(export);
        var footer = new StackPanel { Margin = new(14, 8, 0, 8) }; footer.Children.Add(status); footer.Children.Add(detail); detail.TextWrapping = TextWrapping.Wrap;
        var footerViewport = new ScrollViewer { Content = footer, MaxHeight = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var fixedFooter = new Grid { Margin = new(16, 2, 16, 2) };
        fixedFooter.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        fixedFooter.ColumnDefinitions.Add(new());
        var updateControls = new StackPanel();
        var updateBlock = new Border { Child = updateControls, MaxWidth = 348, Padding = new(12, 6, 10, 4),
            Background = StudioTheme.Surface, BorderBrush = StudioTheme.Line, BorderThickness = new(1), CornerRadius = new(6) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(updateBlock, "IrradianceUpdateControls");
        var updateHeading = Label("Calculation", 13, StudioTheme.Ink); updateHeading.FontWeight = FontWeights.SemiBold;
        var updateHeader = new WrapPanel(); updateHeader.Children.Add(updateHeading);
        updateIndicator.FontWeight = FontWeights.Bold; updateIndicator.Margin = new(10, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(updateIndicator, "IrradianceUpdateState");
        updateHeader.Children.Add(updateIndicator); updateControls.Children.Add(updateHeader);
        fixedFooter.Children.Add(updateBlock); Grid.SetColumn(footerViewport, 1); fixedFooter.Children.Add(footerViewport);
        DockPanel.SetDock(fixedFooter, Dock.Bottom); root.Children.Add(fixedFooter);
        var body = new Grid { Margin = new(16, 12, 16, 0) };
        var inputs = new StackPanel { Margin = new(14) };
        var inputHost = Card(); inputHost.VerticalAlignment = VerticalAlignment.Top; body.Children.Add(inputHost);
        var tuning = new StackPanel { Margin = new(0, 0, 0, 8) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = inputs }; inputHost.Child = scroll;
        System.Windows.Automation.AutomationProperties.SetAutomationId(scroll, "IrradianceInputs");
        Section(inputs, "Images & calibration");
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
        PathField(inputs, "Sky photo folder", "SkyFolder", PickSkyFolder);
        var imageButtons = new WrapPanel(); imageButtons.Children.Add(choosePhoto); imageButtons.Children.Add(importMask); inputs.Children.Add(imageButtons);
        choosePhoto.Click += (_, _) => PickSkyPhoto(); importMask.Click += (_, _) => PickMask();
        StudioTheme.Help(choosePhoto, "Choose the original sky photograph taken with the calibrated lens.");
        StudioTheme.Help(importMask, "Select a sky photograph first. Import an opaque black-and-white PNG at the same oriented dimensions. White means open sky by default. You can invert the mask during import.");
        images = Combo(inputs, "Active sky photo", []); images.SelectionChanged += (_, _) => { if (!loading && images.SelectedItem is string name) { settings.SkyImage = Path.Combine(settings.SkyFolder, name); RestorePhotoMaskSelection(); Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview(); } };
        model = Combo(inputs, "Sky segmentation", Enum.GetNames<SkyModel>()); model.SelectionChanged += (_, _) => { if (!loading && model.SelectedIndex >= 0) { settings.Model = (SkyModel)model.SelectedIndex; settings.SelectedMaskId = null; Changed(); RefreshMaskChoices(); } };
        resolution = Combo(inputs, "Mask quality", ["Original Quality (Slower)", "Reduced Quality (Faster)"]); resolution.SelectionChanged += (_, _) => { if (!loading) { settings.Resolution = resolution.SelectedIndex == 1 ? 512 : 1024; settings.SelectedMaskId = null; Changed(); RefreshMaskChoices(); } };
        AddPhoneControls(inputs);
        Section(inputs, "Site location");
        Field(inputs, "Latitude (°)", "Latitude"); Field(inputs, "Longitude (°)", "Longitude"); Field(inputs, "Elevation (m)", "Elevation");
        StudioTheme.Help(fields["Latitude"], "Enter geographic latitude in decimal degrees. North is positive, south is negative.");
        StudioTheme.Help(fields["Longitude"], "Enter geographic longitude in decimal degrees. East is positive, west is negative.");
        StudioTheme.Help(fields["Elevation"], "Height above sea level in metres.");
        useSystemZone = new() { Content = "Use Windows time zone", Margin = new(0, 8, 0, 4) };
        useSystemZone.Checked += (_, _) => ChangeTimeZoneMode(true);
        useSystemZone.Unchecked += (_, _) => ChangeTimeZoneMode(false);
        inputs.Children.Add(useSystemZone);
        StudioTheme.Help(useSystemZone, "Use the Windows time zone for study dates and the graph. Clear this option to choose a different region or a fixed UTC offset.");
        zone = Combo(inputs, "Study time zone", []);
        zone.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName); zone.SelectedValuePath = nameof(TimeZoneInfo.Id);
        zone.ItemsSource = TimeZoneSelection.Choices();
        zone.DropDownOpened += (_, _) =>
        {
            if (zone.Template.FindName("PART_Popup", zone) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement list })
            {
                double available = Math.Max(160, owner.ActualWidth - 48);
                list.MinWidth = Math.Min(600, available);
                list.MaxWidth = Math.Min(760, available);
            }
        };
        zone.SelectionChanged += (_, _) => { if (!loading && !settings.UseSystemTimeZone && zone.SelectedValue is string z) { settings.Zone = z; Changed(); } };
        StudioTheme.Help(zone, "Regional time zones follow daylight saving time. A fixed UTC offset applies year-round. Site coordinates set the weather location.");
        Section(inputs, "Weather data");
        inputs.Children.Add(weatherMode);
        StudioTheme.Help(weatherMode, "Choose a weather service or import a local file containing direct horizontal and diffuse horizontal irradiance.");
        weatherMode.Checked += (_, _) => SelectWeatherMode(true);
        weatherMode.Unchecked += (_, _) => SelectWeatherMode(false);
        inputs.Children.Add(serviceWeatherControls); inputs.Children.Add(importWeatherControls);
        Field(serviceWeatherControls, "Start date · DD.MM.YYYY", "Start"); Field(serviceWeatherControls, "End date · inclusive · DD.MM.YYYY", "End");
        provider = Combo(serviceWeatherControls, "Weather data service", ["NASA POWER", "Open-Meteo · ERA5"]);
        System.Windows.Automation.AutomationProperties.SetName(provider, "Weather data source");
        provider.SelectionChanged += (_, _) =>
        {
            if (!loading && provider.SelectedIndex >= 0) { settings.Provider = provider.SelectedIndex; Changed(); }
        };
        var fileActions = new WrapPanel();
        importWeatherControls.Children.Add(fileActions);
        var template = Button("Download template…", SaveWeatherTemplate);
        StudioTheme.Help(template, "Save an editable Excel file with three sample hourly readings.");
        fileActions.Children.Add(template);
        chooseWeatherFile.Click += (_, _) => PickImport(); fileActions.Children.Add(chooseWeatherFile);
        StudioTheme.Help(chooseWeatherFile, "Open a local Excel or CSV file with timestamps, direct horizontal and diffuse horizontal irradiance in W/m². Its intervals set the study period.");
        window = Combo(importWeatherControls, "Imported rows represent", ["Preceding interval · end labels", "Following interval · start labels", "Centered interval"]); window.SelectionChanged += (_, _) => { if (!loading) { settings.ImportWindow = window.SelectedIndex; Changed(); } };
        Field(importWeatherControls, "Interval length (min)", "ImportIntervalMinutes");
        StudioTheme.Help(window, "Use the source file’s timestamp convention. Native workbook metadata takes precedence over these interval settings.");
        StudioTheme.Help(fields["ImportIntervalMinutes"], "For a file without interval metadata, enter the duration represented by one source row in minutes.");
        inputs.Children.Add(weatherSource);
        isotropic = new() { Content = "Use isotropic diffuse light", Margin = new(0, 8, 0, 8) }; isotropic.Checked += (_, _) => { if (!loading) { settings.Isotropic = true; Changed(); } }; isotropic.Unchecked += (_, _) => { if (!loading) { settings.Isotropic = false; Changed(); } }; inputs.Children.Add(isotropic);
        StudioTheme.Help(isotropic, "Treat diffuse light as uniform across the sky. The default Hay–Davies model includes brighter sky around the sun.");
        inputs.Children.Add(tuning);
        AddPanelControls(tuning);
        update.Background = StudioTheme.Blue; update.Foreground = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(update, "UpdateResults");
        System.Windows.Automation.AutomationProperties.SetAutomationId(refreshData, "RefreshData");
        System.Windows.Automation.AutomationProperties.SetAutomationId(stop, "StopUpdate");
        update.FontWeight = stop.FontWeight = refreshData.FontWeight = FontWeights.Bold;
        foreach (var button in new[] { update, stop, refreshData })
        {
            button.Padding = new(10, 4, 10, 4);
            button.Margin = new(0, 2, 6, 2);
        }
        update.FontSize = 14;
        refreshData.FontSize = 12;
        refreshData.Content = new TextBlock { Text = "Download weather again & update", TextWrapping = TextWrapping.Wrap };
        update.Click += (_, _) => Calculate(); refreshData.Click += (_, _) => Begin(true); stop.Click += (_, _) => StopUpdate();
        var runButtons = new WrapPanel(); runButtons.Children.Add(update); runButtons.Children.Add(stop); updateControls.Children.Add(runButtons);
        updateControls.Children.Add(refreshData);

        var right = new Grid(); body.Children.Add(right);
        root.Children.Add(new WorkspaceViewport(body, inputHost, right));
        right.RowDefinitions.Add(new() { Height = GridLength.Auto }); right.RowDefinitions.Add(new() { Height = new GridLength(10) }); right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var preview = new Grid(); right.Children.Add(Card(preview)); preview.RowDefinitions.Add(new() { Height = GridLength.Auto }); preview.RowDefinitions.Add(new() { Height = new GridLength(220) }); preview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var previewHeader = new WrapPanel { Margin = new(12, 8, 12, 6) }; preview.Children.Add(previewHeader);
        var previewTitle = Label("Sky photo & mask", 16, StudioTheme.Ink); previewTitle.FontWeight = FontWeights.SemiBold; previewTitle.Margin = new(4, 3, 12, 4); previewHeader.Children.Add(previewTitle);
        previewHeader.Children.Add(showCardinals);
        showCardinals.Checked += (_, _) => SetCardinalVisibility(); showCardinals.Unchecked += (_, _) => SetCardinalVisibility();
        StudioTheme.Help(showCardinals, "Update the study to project the compass at the edge of the calibrated view. The bearings use the same camera pose as shading.");
        previewHeader.Children.Add(showSunPath);
        showSunPath.Checked += (_, _) => SetSunPathVisibility(); showSunPath.Unchecked += (_, _) => SetSunPathVisibility();
        StudioTheme.Help(showSunPath, "Update the study to project the sun path for the study period onto the mask. This switch controls its visibility.");
        var pair = CreatePreviewPair(preview.RowDefinitions[1]);
        Grid.SetRow(pair, 1);
        preview.Children.Add(pair);
        editMask.Click += (_, _) => OpenMaskEditor();
        var previewInfo = new StackPanel { Margin = new(16, 6, 16, 10) };
        maskInfo.Focusable = true;
        previewInfo.Children.Add(fieldOfViewInfo); previewInfo.Children.Add(maskInfo);
        sunPathInfo.Visibility = cardinalInfo.Visibility = Visibility.Collapsed;
        previewInfo.Children.Add(sunPathInfo); previewInfo.Children.Add(cardinalInfo);
        System.Windows.Automation.AutomationProperties.SetAutomationId(fieldOfViewInfo, "SkyPhotoFieldOfView");
        var maskPicker = new StackPanel(); maskPicker.Children.Add(Label("Mask for calculation", 11)); maskPicker.Children.Add(maskChoice);
        AutomationProperties.SetName(maskChoice, "Mask for calculation");
        maskChoice.SelectionChanged += (_, _) => SelectMaskChoice(); previewInfo.Children.Add(maskPicker);
        Grid.SetRow(previewInfo, 2); preview.Children.Add(previewInfo);
        var graph = new Grid(); var graphCard = Card(graph); Grid.SetRow(graphCard, 2); right.Children.Add(graphCard);
        graph.RowDefinitions.Add(new() { Height = GridLength.Auto }); graph.RowDefinitions.Add(new() { Height = GridLength.Auto }); graph.RowDefinitions.Add(new() { Height = new GridLength(260) }); graph.RowDefinitions.Add(new() { Height = GridLength.Auto }); graph.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var cards = new UniformGridCompat(3) { Margin = new(18, 10, 18, 0) }; graph.Children.Add(cards);
        cards.Children.Add(Metric("Before shading · kWh/m²", beforeEnergy, IrradianceChart.BeforeBrush));
        cards.Children.Add(Metric("After shading · kWh/m²", afterEnergy, IrradianceChart.AfterBrush));
        cards.Children.Add(Metric("Estimated loss", reduction, StudioTheme.Ink));
        var toolbar = new WrapPanel { Margin = new(14, 4, 10, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(toolbar, 1); graph.Children.Add(toolbar);
        var component = new ComboBox { ItemsSource = new[] { "Total irradiance", "Direct component", "Sky diffuse component" }, SelectedIndex = 0, Width = 170 }; component.SelectionChanged += (_, _) => { chart.Component = component.SelectedIndex; chart.InvalidateVisual(); }; toolbar.Children.Add(component);
        AutomationProperties.SetName(component, "Irradiance component");
        var fullPeriod = Button("Full period", () => { graphDay.SelectedDate = null; chart.Reset(); });
        StudioTheme.Help(fullPeriod, "Show the entire study period. Scroll over the plot to zoom, drag to pan, or choose a day.");
        StudioTheme.Help(component, "Both curves show irradiance on the panel. The model treats unobserved sky as blocked and excludes ground reflection.");
        toolbar.Children.Add(fullPeriod); graphDay = new DatePicker { Width = 125, Margin = new(4, 0, 0, 0) }; graphDay.SelectedDateChanged += (_, _) => { if (graphDay.SelectedDate is { } d) chart.Day(d); }; toolbar.Children.Add(graphDay);
        AutomationProperties.SetName(graphDay, "Day to display");
        toolbar.Children.Add(Label("  ● Before", 12, IrradianceChart.BeforeBrush)); toolbar.Children.Add(Label("  ● After", 12, IrradianceChart.AfterBrush));
        chart.HoverText = text => hover.Text = text; Grid.SetRow(chart, 2); graph.Children.Add(chart); hover.Margin = new(18, 4, 10, 0); Grid.SetRow(hover, 3); graph.Children.Add(hover);
        var graphNotes = new StackPanel { Margin = new(18, 0, 14, 6) };
        Grid.SetRow(graphNotes, 4); graph.Children.Add(graphNotes);
        provenance.Visibility = Visibility.Collapsed; graphNotes.Children.Add(provenance);
        var studyNotes = new StackPanel { Margin = new(18, 2, 0, 0) };
        foreach (var note in new[] { runDetails, modelDetails, lastWeatherResult })
        { note.TextWrapping = TextWrapping.Wrap; studyNotes.Children.Add(note); }
        studyDetails.Content = studyNotes; graphNotes.Children.Add(studyDetails);
        
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
        skyViewport.ClearPhoto();
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
    private static Border Card(UIElement? content = null) => new()
    {
        Child = content, Background = StudioTheme.Surface, BorderBrush = StudioTheme.Line, BorderThickness = new(1), CornerRadius = new(7)
    };
    public static TextBlock Label(string text, double size = 13, Brush? brush = null) => new() { Text = text, FontSize = size, Foreground = brush ?? StudioTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Metric(string name, TextBlock value, Brush color)
    {
        value.Foreground = color; value.FontWeight = FontWeights.SemiBold;
        var metric = new StackPanel(); metric.Children.Add(Label(name, 12)); metric.Children.Add(value); return metric;
    }
    private static void Section(Panel parent, string text)
    {
        var title = Label(text, 15, StudioTheme.Ink); title.FontWeight = FontWeights.SemiBold;
        parent.Children.Add(new Border { Child = title, BorderBrush = StudioTheme.Line, BorderThickness = new(0, 0, 0, 1),
            Padding = new(0, 0, 0, 6), Margin = new(0, parent.Children.Count == 0 ? 0 : 15, 0, 8) });
    }
    private Button Button(string text, Action click, bool primary = false) { var b = new Button { Content = text }; if (primary) { b.Background = StudioTheme.Blue; b.Foreground = Brushes.White; } b.Click += (_, _) => click(); return b; }
    private void Field(Panel parent, string label, string property) { parent.Children.Add(Label(label, 12)); var t = MakeField(property); System.Windows.Automation.AutomationProperties.SetName(t, label); parent.Children.Add(t); }
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
                if (value is double number && (!double.IsFinite(number) ||
                    (property is nameof(UserSettings.PhoneInclination) or nameof(UserSettings.PanelTilt) && (number < 0 || number > 90))))
                    throw new FormatException();
                bool changed = !Equals(p.GetValue(settings), value) || invalidFields.Contains(property) ||
                    (property == nameof(UserSettings.PanelTopHeading) && settings.PanelAzimuth is not (>= 0 and <= 360));
                if (property == nameof(UserSettings.PhoneInclination))
                {
                    // Typing "0.8" passes through "0". Apply each draft to the frame at the start
                    // of this edit, so only committing an actual zero flattens a rolled camera.
                    phoneTiltEditOrigin ??= settings with { };
                    var draft = phoneTiltEditOrigin with { PhoneInclination = (double)value };
                    settings.BottomAzimuth = draft.BottomAzimuth;
                    settings.CameraTilt = draft.CameraTilt;
                    settings.CameraRoll = draft.CameraRoll;
                }
                else
                {
                    phoneTiltEditOrigin = null;
                    p.SetValue(settings, value);
                }
                invalidFields.Remove(property); t.ClearValue(Control.BorderBrushProperty);
                if (IsEquipmentField(property)) RefreshEquipmentControls(property);
                if (changed) Changed(saveSettings: false);
            }
            catch
            {
                invalidFields.Add(property); t.BorderBrush = Brushes.Firebrick;
                if (IsEquipmentField(property)) RefreshEquipmentControls(property);
                Changed(saveSettings: false);
            }
        }
        t.TextChanged += (_, _) => ReadText();
        void CommitText()
        {
            if (loading) return;
            phoneTiltEditOrigin = null;
            if (IsEquipmentField(property)) RefreshEquipmentControls();
            SaveInputs();
        }
        t.LostKeyboardFocus += (_, _) => CommitText();
        t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } }; return t;
    }
    private void SmallField(Grid grid, int col, string label, string property) { var p = new StackPanel(); Grid.SetColumn(p, col); grid.Children.Add(p); Field(p, label, property); }
    private void PathField(Panel p, string label, string property, Action browse)
    {
        p.Children.Add(Label(label, 12));
        var row = new DockPanel(); var b = Button("…", browse);
        AutomationProperties.SetName(b, "Choose " + label.ToLowerInvariant());
        DockPanel.SetDock(b, Dock.Right); row.Children.Add(b);
        var t = MakeField(property); t.IsReadOnly = true;
        AutomationProperties.SetName(t, label); row.Children.Add(t); p.Children.Add(row);
    }
    private static ComboBox Combo(Panel p, string label, string[] items)
    {
        p.Children.Add(Label(label, 12)); var c = new ComboBox { ItemsSource = items };
        AutomationProperties.SetName(c, label); p.Children.Add(c); return c;
    }
    private void LoadFields()
    {
        loading = true; invalidFields.Clear(); phoneTiltEditOrigin = null;
        foreach (var (name, box) in fields) { box.ClearValue(Control.BorderBrushProperty); var value = typeof(UserSettings).GetProperty(name)!.GetValue(settings); box.Text = value is DateTime d ? d.ToString("dd.MM.yyyy") : Convert.ToString(value, CultureInfo.InvariantCulture); }
        RefreshEquipmentControls();
        model.SelectedIndex = (int)settings.Model; resolution.SelectedIndex = settings.Resolution == 512 ? 1 : 0; provider.SelectedIndex = settings.Provider; UpdateTimeZoneControls(); window.SelectedIndex = settings.ImportWindow; isotropic.IsChecked = settings.Isotropic;
        showSunPath.IsChecked = settings.ShowSunPath; maskPreview.ShowOverlay = settings.ShowSunPath;
        showCardinals.IsChecked = settings.ShowCardinalDirections; photo.ShowOverlay = settings.ShowCardinalDirections;
        PopulateImages();
        RefreshMaskChoices();
        RefreshCalibrationReview();
        loading = false;
        RefreshWeatherSource();
    }
    private void RefreshWeatherSource()
    {
        bool imported = !WeatherSourceSelection.UsesServices(settings), previous = loading; loading = true;
        weatherMode.IsChecked = !imported; weatherMode.Content = imported ? "Import Own Excel" : "Use Data Services";
        System.Windows.Automation.AutomationProperties.SetName(weatherMode, (string)weatherMode.Content);
        provider.SelectedIndex = settings.Provider;
        serviceWeatherControls.Visibility = imported ? Visibility.Collapsed : Visibility.Visible;
        importWeatherControls.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        weatherSource.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        weatherSource.Text = !imported ? "" : string.IsNullOrWhiteSpace(settings.ImportPath) ? "Choose an Excel or CSV file."
            : "Selected file: " + Path.GetFileName(settings.ImportPath);
        weatherSource.ToolTip = imported && !string.IsNullOrWhiteSpace(settings.ImportPath) ? PortablePaths.Resolve(settings.ImportPath) : null;
        loading = previous;
        RefreshButtons();
    }
    public void SelectWeatherMode(bool useServices)
    {
        if (loading) return;
        WeatherSourceSelection.SetMode(settings, useServices);
        Changed(); RefreshWeatherSource();
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
        SetMaskOptions((maskChoice.SelectedItem as MaskChoice)?.Imported == true);
    }
    private void SetMaskOptions(bool imported)
    {
        const string importedHelp = "This imported mask is used directly. Choose the AI mask to enable model and resolution settings.";
        model.IsEnabled = resolution.IsEnabled = !imported;
        StudioTheme.Help(model, imported ? importedHelp : "Select the AI model used to separate open sky from obstructions.");
        StudioTheme.Help(resolution, imported ? importedHelp : "Original uses a 1024 px AI input. Reduced uses 512 px. The saved mask matches the photo size.");
        StudioTheme.Help(maskChoice, imported ? importedHelp : "Use Update results to generate the AI mask, or choose a saved mask for this photograph.");
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
            SetMaskOptions(choice.Imported); Changed(); ShowSelectedMaskPreview();
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
                    skyViewport.SetPhoto(Bitmap(displayedPhoto), unmaskedPhoto.Width, unmaskedPhoto.Height, PortablePaths.Resolve(settings.SkyImage));
                    orientedPhotoWidth = unmaskedPhoto.Width; orientedPhotoHeight = unmaskedPhoto.Height;
                    RefreshFieldOfView();
                    ShowMaskPending();
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
                }) { CalibratedOutline = currentFieldOfView.CalibratedOutline, UsableOutline = currentFieldOfView.UsableOutline,
                    FieldOfViewLabel = currentFieldOfView.OutlineLabel, FieldOfViewHelp = currentFieldOfView.Explanation }) { Owner = owner };
            WindowWorkArea.FitToOwner(editor, owner); editor.ShowDialog();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Changed(bool saveSettings = true, bool refreshSources = false, bool verifyArtifacts = false)
    {
        if (loading || closing) return;
        try
        {
            if (settings.LastCalibrationFailure is { } failure && !failure.Matches(settings))
            {
                settings.LastCalibrationFailure = null;
                RefreshCalibrationReview();
                RefreshFieldOfView();
                RefreshInputState();
            }
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
        var previous = inputWork; var snapshot = settings with { };
        var invalid = invalidFields.Where(f => InputDependencies.ForField(f, snapshot) != ArtifactGroup.None).ToArray();
        long observedRevision = revision;
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
                    foreach (string name in new[] { "CalibrationFolder", "SkyFolder", "SkyImage", "ProfilePath", "ImportPath", "RememberedImportPath" })
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
                if (result.restored != null && !updating && !calibrating && settings.LastCalibrationFailure == null)
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
            if (geometryChanged) { skyViewport.ClearPhoto(); displayedPhoto = null; orientedPhotoWidth = orientedPhotoHeight = 0; }
            maskViewport.ClearImage(); displayedMaskPng = null;
            displayedAsset = null; editMask.IsEnabled = false;
            ShowMaskPending();
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
        provenance.Visibility = Visibility.Visible;
    }
    private string? InputProblem() => invalidFields.Any(f => InputDependencies.ForField(f, settings) != ArtifactGroup.None)
        ? "Correct the highlighted calculation fields: " + string.Join(", ", invalidFields.Where(f => InputDependencies.ForField(f, settings) != ArtifactGroup.None).Order()) : UpdateInputs.Problem(settings);
    private void RefreshInputState()
    {
        string? problem = InputProblem();
        SetUpdateState(problem == null ? UpdateState.UpdateRequired : UpdateState.NeedsAttention,
            problem ?? "");
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
        updateIndicator.Foreground = state == UpdateState.UpToDate ? StudioTheme.Teal
            : state is UpdateState.NeedsAttention or UpdateState.Failed ? Brushes.Firebrick : StudioTheme.Amber;
        System.Windows.Automation.AutomationProperties.SetName(updateIndicator, label);
        status.Text = ""; detail.Text = message;
        RefreshButtons();
    }
    private void RefreshButtons()
    {
        update.IsEnabled = refreshData.IsEnabled = !controller.DependentBusy && !updating && !calibrating && InputProblem() == null;
        refreshData.IsEnabled &= WeatherSourceSelection.UsesServices(settings);
        StudioTheme.Help(update, controller.DependentBusy ? "Wait for PV Autonomy or stop its operation first." : "Update results reuses matching weather data, calibration and masks. Changed inputs are recalculated.");
        StudioTheme.Help(refreshData, controller.DependentBusy ? "Wait for PV Autonomy or stop its operation first." : WeatherSourceSelection.UsesServices(settings)
            ? "Download fresh weather from the selected provider, then update. Unchanged calibration and masks are reused."
            : "Use Update results to reread the imported file. Weather downloads are available when a data service is selected.");
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
        var zone = TimeZoneSelection.Resolve(result.Settings.Zone);
        if (chart.SetData(result.Run.Rows, zone)) graphDay.SelectedDate = null;
        if (result.Run.Rows.Count > 0)
        {
            var (first, last) = new StudyPeriod(result.Run.Rows[0].Start, result.Run.Rows[^1].End).LocalDates(zone);
            if (graphDay.DisplayDateStart != first || graphDay.DisplayDateEnd != last)
            {
                graphDay.SelectedDate = null;
                graphDay.DisplayDateEnd = null;
                graphDay.DisplayDateStart = first; graphDay.DisplayDateEnd = last;
                graphDay.DisplayDate = first;
            }
        }
        beforeEnergy.Text = result.Run.BeforeEnergy.ToString("F2"); afterEnergy.Text = result.Run.AfterEnergy?.ToString("F2") ?? "—";
        reduction.Text = result.Run.LossPercent is { } loss ? loss.ToString("F1") + "%" : "—";
        modelDetails.Text = result.Run.SkyCoverage is { } coverage ? $"{result.Run.Model} · observed sky coverage {coverage:P1}" : "Baseline only";
        runDetails.Text = restored ? $"{result.Run.Rows.Count:N0} intervals · saved result restored · {result.Settings.Zone}"
            : $"{result.Run.Rows.Count:N0} intervals · {result.TotalMilliseconds / 1000:F2} s · Debug Data saved · {result.Settings.Zone}";
        studyDetails.Visibility = Visibility.Visible;
        provenance.Visibility = Visibility.Collapsed;
        bool complete = IrradianceReadiness.Inspect(services, result).IsReady;
        SetUpdateState(complete ? UpdateState.UpToDate : UpdateState.NeedsAttention,
            complete ? "" : "Shading is incomplete. Check the camera profile and sky photo.");
    }
    private void ShowMaskPending()
    {
        maskInfo.Text = "Mask pending";
        StudioTheme.Help(maskInfo, "Update results to prepare the selected mask.");
    }
    public void ShowMask(MaskAsset asset)
    {
        if (displayedPhoto != null) asset = asset with { OriginalPreview = displayedPhoto };
        if (!ReferenceEquals(displayedPhoto, asset.OriginalPreview))
        {
            skyViewport.SetPhoto(Bitmap(asset.OriginalPreview), asset.Result.Width, asset.Result.Height, PortablePaths.Resolve(settings.SkyImage));
            displayedPhoto = asset.OriginalPreview; displayedCardinals = null; currentCardinals = null;
        }
        // The mask and overlay share one native-resolution viewport; preview rounding cannot misalign them.
        if (!ReferenceEquals(displayedMaskPng, asset.Png))
        {
            var bitmap = Bitmap(asset.Png);
            maskViewport.SetImage(bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
            displayedMaskPng = asset.Png;
            if (sunPathIsCurrent && completed?.SunPath != null) ShowSunPath(completed.SunPath);
        }
        maskInfo.Text = "Mask ready";
        StudioTheme.Help(maskInfo, asset.Description);
        displayedAsset = asset; orientedPhotoWidth = asset.Result.Width; orientedPhotoHeight = asset.Result.Height;
        RefreshFieldOfView(); RefreshButtons();
    }
    private void RefreshFieldOfView()
    {
        var disk = displayedAsset?.Disk;
        currentFieldOfView = settings.LastCalibrationFailure is { } failure && failure.Matches(settings)
            ? SkyPhotoFieldOfView.CalibrationFailed(failure.Reason)
            : SkyPhotoFieldOfView.Load(settings, orientedPhotoWidth, orientedPhotoHeight,
                disk == null ? null : new ImageDisk(disk.CenterX, disk.CenterY, disk.Radius));
        RefreshCalibrationOutcome();
        if (currentFieldOfView.StampLabel != null)
        {
            if (currentCardinals != null) InvalidateOrientation(currentFieldOfView.Explanation);
            if (sunPathIsCurrent) ClearSunPath(currentFieldOfView.Explanation);
        }
        skyViewport.SetFieldOfView(currentFieldOfView);
        fieldOfViewInfo.Text = currentFieldOfView.Text;
        fieldOfViewInfo.Foreground = currentFieldOfView.StampLabel != null ? Brushes.Firebrick :
            currentFieldOfView.CalibratedOutline == null ? StudioTheme.Muted : Brushes.DarkGreen;
        StudioTheme.Help(fieldOfViewInfo, currentFieldOfView.OutlineLabel + ". " + currentFieldOfView.Explanation);
        StudioTheme.Help(images, currentFieldOfView.Explanation);
    }
    public static string OrientationInputKey(UserSettings s) => AppData.Key(new
    { s.SkyImage, s.ProfilePath, s.Model, s.Resolution, s.CenteredDisk, s.CoverageAngle, s.BottomAzimuth, s.CameraTilt, s.CameraRoll });
    private void InvalidateOrientation(string message)
    {
        orientationRevision++;
        photo.ClearOverlay(); displayedCardinals = null; currentCardinals = null;
        SetOverlayStatus(cardinalInfo, showCardinals, message, false);
    }
    private void ShowCardinals(CardinalDirectionOverlayResult? result)
    {
        if (currentFieldOfView.StampLabel != null) result = null;
        currentCardinals = result;
        if (result == null)
        {
            photo.ClearOverlay(); displayedCardinals = null;
            SetOverlayStatus(cardinalInfo, showCardinals, "Add a calibrated sky photo to project compass bearings.", false); return;
        }
        if (!ReferenceEquals(displayedCardinals, result.Png)) { photo.SetOverlay(Bitmap(result.Png)); displayedCardinals = result.Png; }
        photo.ShowOverlay = settings.ShowCardinalDirections;
        SetOverlayStatus(cardinalInfo, showCardinals, result.HasMarkers ? "Each tick marks a projected compass bearing and follows its compass axis in the photo." : "No cardinal bearings inside this view.", !result.HasMarkers);
    }
    private void SetCardinalVisibility()
    {
        if (loading) return;
        settings.ShowCardinalDirections = showCardinals.IsChecked == true;
        photo.ShowOverlay = settings.ShowCardinalDirections;
        try { AppData.SaveSettings(settings); } catch (Exception ex) { detail.Text = "Could not save settings: " + ex.Message; }
    }
    private void ClearSunPath(string message)
    { maskPreview.ClearOverlay(); sunPathIsCurrent = false; SetOverlayStatus(sunPathInfo, showSunPath, message, false); }
    private void ShowSunPath(SunPathOverlayResult? result)
    {
        if (currentFieldOfView.StampLabel != null) { ClearSunPath(currentFieldOfView.Explanation); return; }
        if (result == null) { ClearSunPath("Sun path unavailable · add a calibrated sky photo"); return; }
        maskPreview.SetOverlay(Bitmap(result.Png)); maskPreview.ShowOverlay = settings.ShowSunPath;
        sunPathIsCurrent = true;
        SetOverlayStatus(sunPathInfo, showSunPath, result.HasPaths ? "The sun path shows the study period on the calibrated mask." : "No sun path inside this view for the selected period.", !result.HasPaths);
    }
    private static void SetOverlayStatus(TextBlock notice, CheckBox toggle, string text, bool showNotice)
    {
        StudioTheme.Help(toggle, text);
        notice.Text = showNotice ? text : "";
        notice.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
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
        var dialog = new Window { Title = "Import existing mask · no AI", Width = 510, Height = 600,
            ResizeMode = ResizeMode.CanResize, MinWidth = 280, MinHeight = 240 };
        WindowWorkArea.FitToOwner(dialog, owner);
        var panel = new StackPanel { Margin = new(20) }; dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(Label("Photo: " + Path.GetFileName(settings.SkyImage), 13));
        panel.Children.Add(Label("Mask: " + Path.GetFileName(pick.FileName), 13));
        panel.Children.Add(Label("Mask name", 12)); var name = new TextBox { Text = Path.GetFileNameWithoutExtension(pick.FileName), MaxLength = 100 }; panel.Children.Add(name);
        var polarity = Combo(panel, "Meaning of the source pixels", ["White = open sky; black = obstruction", "Black = open sky; white = obstruction (invert)"]); polarity.SelectedIndex = 0;
        StudioTheme.Help(polarity, "Import an opaque black-and-white mask with the same pixel dimensions as the oriented photo. Choose which pixel color marks open sky.");
        var disk = Combo(panel, "Lens disk in the photograph", ["Detect from photo", "Centered full disk"]); disk.SelectedIndex = settings.CenteredDisk ? 1 : 0;
        StudioTheme.Help(disk, "Pixels outside the photograph’s lens disk become black in the imported mask.");
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
        SelectProfile(picker.FileName);
    }
    private void SelectProfile(string path)
    {
        profileSelectionRevision++;
        settings.ProfilePath = PortablePaths.Store(path);
        settings.CoverageAngle = 0;
        settings.LastCalibrationFailure = null;
        LoadFields(); Changed(); RefreshFieldOfView();
    }
    private void RefreshCalibrationReview()
    {
        string? fingerprint = ProfileFingerprint();
        CalibrationReview? savedReview = null;
        if (settings.LastCalibrationFailure is { ReviewDirectory: { } directory } failure && failure.Matches(settings))
        {
            var failed = CalibrationReview.TryLoad(PortablePaths.Resolve(directory));
            if (failed?.Report.Failure != null && failed.EvidenceFingerprint == failure.EvidenceFingerprint) savedReview = failed;
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
        RefreshCalibrationOutcome();
        const string profileHelp = "Choose camera-profile.json for this lens and oriented image size. The profile includes the calibrated field of view.";
        StudioTheme.Help(loadProfile, string.IsNullOrWhiteSpace(settings.ProfilePath) ? profileHelp :
            profileHelp + "\n" + PortablePaths.Resolve(settings.ProfilePath));
    }
    private void RefreshCalibrationOutcome()
    {
        (string text, Brush color, string help) outcome;
        if (calibrating)
            outcome = ("Calibrating…", StudioTheme.Muted, "Checking the board photos and fitting the lens.");
        else if (settings.LastCalibrationFailure is { } failure && failure.Matches(settings))
            outcome = ("Calibration Failed", Brushes.Firebrick, failure.Reason);
        else if (string.IsNullOrWhiteSpace(settings.ProfilePath))
            outcome = ("No calibration", StudioTheme.Muted, "Calibrate with checkerboard photos or load a saved camera profile.");
        else if (reviewedProfileFingerprint == null)
            outcome = ("Profile unavailable", Brushes.Firebrick, "The selected camera profile could not be read.");
        else if (currentFieldOfView.StampLabel == CalibrationStamp.UnusableLabel)
            outcome = ("Profile unusable", Brushes.Firebrick, currentFieldOfView.Explanation);
        else if (currentFieldOfView.CalibratedFullDegrees.HasValue || calibrationReview is { Report.Failure: null })
            outcome = ("Calibration Success", Brushes.DarkGreen, calibrationReview != null ?
                "Review checkerboard photos for detections and fit details." : "The saved camera profile applies to this photo.");
        else
            outcome = ("Profile selected", StudioTheme.Muted, "Choose a sky photo to check the saved profile.");
        calibrationInfo.Text = outcome.text;
        calibrationInfo.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : outcome.color;
        StudioTheme.Help(calibrationInfo, outcome.help);
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
    private void SaveWeatherTemplate()
    {
        var dialog = new SaveFileDialog { Title = "Save weather template", FileName = "weather-template.xlsx", DefaultExt = ".xlsx", Filter = "Excel workbook|*.xlsx" };
        if (dialog.ShowDialog(owner) != true) return;
        try { WeatherSourceSelection.WriteImportTemplate(dialog.FileName); status.Text = "Weather template saved"; detail.Text = dialog.FileName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { status.Text = "Could not save template"; detail.Text = ex.Message; }
    }
    public void SelectWeatherFile(string path)
    { WeatherSourceSelection.SelectFile(settings, path); Changed(); RefreshWeatherSource(); status.Text = "Weather file selected"; detail.Text = path; }
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
    internal async Task Calibrate()
    {
        if (calibrating || updating || controller.DependentBusy) return;
        if (invalidFields.Any(f => f is "Columns" or "Rows" or "SquareMm" || InputDependencies.ForField(f, settings) != ArtifactGroup.None))
        { status.Text = "Correct the highlighted fields first"; return; }
        calibrating = true;
        RefreshCalibrationOutcome();
        pending?.Cancel(); revision++; export.IsEnabled = false;
        MarkPreviousResults(); SetUpdateState(UpdateState.Updating, "Calibrating checkerboard photographs…");
        ClearSunPath("Sun path waiting for the new calibration");
        InvalidateOrientation("Cardinal directions waiting for the new calibration");
        settings.FirstRun = false;
        var snapshot = settings with { };
        long selectionAtStart = profileSelectionRevision;
        bool IsCurrentAttempt() => selectionAtStart == profileSelectionRevision &&
            string.Equals(settings.CalibrationFolder, snapshot.CalibrationFolder, StringComparison.OrdinalIgnoreCase) &&
            settings.Columns == snapshot.Columns && settings.Rows == snapshot.Rows && settings.SquareMm == snapshot.SquareMm;
        status.Text = "Calibrating checkerboard photographs…";
        try
        {
            (CalibrationProfile Profile, string Path, string Details) result;
            try { result = await services.Calibrate(snapshot); }
            catch (OperationCanceledException)
            {
                if (!closing && IsCurrentAttempt()) SetUpdateState(UpdateState.Stopped, "Calibration stopped.");
                return;
            }
            catch (CalibrationReviewException ex)
            {
                if (closing) return;
                if (IsCurrentAttempt()) RecordCalibrationFailure(ex);
                else { status.Text = "Calibration failed for the previous setup"; detail.Text = ex.Message; }
                return;
            }
            if (closing) return;
            if (!IsCurrentAttempt())
            { status.Text = "Calibration saved for the previous setup"; detail.Text = result.Path; return; }
            SelectProfile(result.Path);
            status.Text = "Calibration saved"; detail.Text = result.Path; ShowCalibrationReview();
        }
        catch (Exception ex) { if (!closing) ShowError(ex); }
        finally { calibrating = false; if (!closing) { RefreshCalibrationOutcome(); if (updateState == UpdateState.Updating) RefreshInputState(); else RefreshButtons(); } }
    }
    private void RecordCalibrationFailure(CalibrationReviewException error)
    {
        var review = CalibrationReview.TryLoad(error.DiagnosticsDirectory);
        settings.LastCalibrationFailure = new(PortablePaths.Store(settings.CalibrationFolder), settings.Columns,
            settings.Rows, settings.SquareMm, error.Message, review == null ? null : PortablePaths.Store(review.Directory),
            review?.EvidenceFingerprint);
        // The earlier profile still exists on disk. It must be selected explicitly to use it again.
        settings.ProfilePath = "";
        settings.CoverageAngle = 0;
        RefreshCalibrationReview();
        Changed(); RefreshFieldOfView(); ShowError(error); ShowCalibrationReview();
    }
    public Task LoadExample()
    {
        pending?.Cancel();
        profileSelectionRevision++;
        TimeZoneSelection.ApplySystemZone(settings, systemZone());
        settings = PortablePaths.Example() with { FirstRun = false, Zone = settings.Zone, UseSystemTimeZone = settings.UseSystemTimeZone, ShowSunPath = settings.ShowSunPath, ShowCardinalDirections = settings.ShowCardinalDirections };
        LoadFields(); Changed(); RefreshFieldOfView();
        return Task.CompletedTask;
    }
    private void ShowHelp()
    {
        HelpWindow.OpenGuide(ref helpWindow, owner, $"Using SolarShade · preview {Program.PreviewVersion}",
        [
            ("Camera calibration", "1. Select checkerboard photographs. Enter INNER columns/rows and the size of ONE square in mm, then Calibrate. Calibrate saves native calibration.yml, camera-profile.json and diagnostics in Debug Data/01-calibration, with durable copies under Data/Profiles."),
            ("Sky photos and masks", "2. Select your sky photo folder, then its active photo and segmentation model. The physical lens and oriented image dimensions must match calibration. The mask is saved as a black-and-white PNG. Show sun path adds a separate transparent preview for the selected period as soon as solar geometry is ready. The toggle only changes its visibility; the binary mask remains unchanged. Show cardinal directions adds a separate orientation overlay to the original colored photograph. It is generated during Update results before irradiance retrieval, and reused when its inputs are unchanged. The letters follow the same camera convention as sun paths and shading. Scroll over either preview or use + and − to zoom, drag to pan, and choose Fit to see the whole frame. Expand gives the selected preview the width of both panes. The mask and its sun path zoom together. The solid green line marks the calibrated limit around the lens axis. A dashed line marks the usable boundary when the detected disk or image edges limit coverage.\n\nChoosing photos and existing masks: Choose sky photo opens a photograph directly; the folder and active-photo list follow your choice. Import existing mask reads an already segmented black-and-white PNG without AI. Choose whether white or black means open sky; white is the default. The mask must match the oriented photo's dimensions, use exact black/white grayscale pixels and be opaque. Lens-disk detection uses the photo, not the mask; choose Centered full disk only for a centered full-disk photo. Outside-lens pixels become black. Give the mask a name. Import selects a durable copy immediately, ready for Update results; the original import file is no longer required. AI settings are unavailable while an imported mask is selected. Choose AI mask in Mask for calculation to use segmentation again.\n\nMask editing: Click Edit mask on an imported mask or a generated AI mask. The separate window blocks main-study changes until you save or cancel. Paint black for obstruction or white for open sky; change brush size, mask opacity and zoom while editing. Saving creates a new named variant and selects it in Mask for calculation. Original masks stay available. A new variant needs Update results; returning to a previously accepted exact photo, mask and study restores verified results and export without recalculation. The internal Debug Data includes mask-provenance.json, identifying imported pixels, explicit inversion and subsequent edits. Keep Data/Masks and Data/AcceptedResults when moving the application."),
            ("Phone, panel and site orientation", "Lay the panel flat, front up. Its top heading runs from its center toward the marked edge that you raise for tilt. On the phone, use the lens end as the top. Measure both headings clockwise from true north, with east 90°, south 180° and west 270°. Enter tilt from horizontal, 0° flat to 90° vertical. The top heading is always the bearing you would measure with the equipment laid flat. At vertical the raised edge points straight up, and the retained heading still determines the direction the front faces.\n\nThe calculated lens and panel front azimuths follow the outward axes, clockwise from geographic south. South is 0°, west +90°, north ±180° and east −90°. A south-facing 28° panel has a north top heading (0°) and a south front azimuth (0°). When flat, the front points up and has no azimuth. Turning the panel still rotates its marked top. Turning the phone rotates its photograph and overlays. With image top north, east appears on the left.\n\nCopy panel pose to phone aligns the phone’s lens end with the marked panel top and copies its tilt and heading once, including at 0° tilt. A saved camera with roll keeps its original image orientation. The pose readout identifies older placements that need the explicit uphill placement control before editing.\n\nEnter site coordinates in decimal degrees, for example latitude 43.56, longitude 1.48. Southern latitudes and western longitudes are negative. Solar azimuth is clockwise from true north. Study dates include the complete end day."),
            ("Weather, imports and calculation", "4. Edit your inputs, then click Update results. Edits never start calculations. Yellow means an update is needed or running, red means attention is needed, and green means complete results match the current inputs. Export is available only when green. Irradiance data determines the output intervals: hourly inputs stay hourly and 15-minute inputs stay 15-minute. The current NASA POWER and Open-Meteo endpoints supply hourly means. Solar integration samples improve geometry within each interval; they do not create finer weather data.\n\nImport XLSX/CSV uses a header followed by timestamp, BHI and DHI (W/m²). Native exported workbooks carry their own interval IDs, bounds and conventions. For a three-column file, set the interval duration in minutes and choose start, end or center labels. Local Excel dates and DD.MM.YYYY HH:mm use the selected time zone; ambiguous daylight-saving times need an explicit offset. Download template saves an editable example. Upload weather file opens your XLSX or CSV. Its intervals set the study period, including partial days. Gaps are reported.\n\n5. Tune the panel. Both graph curves are irradiance ON THE PANEL, using Hay–Davies or the isotropic option in Weather data. Circumsolar diffuse follows the 0.25° solar disk. Unseen sky is conservatively blocked; fitted calibration coverage is provisional."),
            ("Data exports", "Choose the Excel columns to export and a destination folder. Every row includes the source timestamp and its UTC offset. Choose source interval start and end to compare providers by the same physical hour. The default columns are direct and diffuse panel irradiance, the two shading factors and the final shaded total. A factor is blank when its unshaded component is zero. For weather diagnostics select Source GHI, Direct horizontal or Diffuse horizontal. Source GHI is available when the weather input saved the original global horizontal readings. The Weather source sheet identifies the source and explains the timestamp convention. The workbook and its receipt are saved in a new scenario folder after a complete shaded update. Cancel closes the window without writing files. The full diagnostic snapshot includes Summary.pdf and SunPathReport.pdf."),
            ("Diagnostics and saved files", "Each explicit update maintains one current Debug Data set beside APPLICATION.exe: 01-calibration (YAML/profile), 02-sky-mask (PNG), 02-orientation (cardinal PNG/XLSX/JSON), 03-irradiance (XLSX), 04-solar-positions (XLSX), 05-transposition (XLSX), and 06-shading (visibility, transmission and shaded results). run.json records stage origins, saved files, fingerprints and the current status. Only a complete calculation is marked Complete; calibration or orientation alone is Partial. Unavailable stages are marked skipped. Edits remove affected files from the latest run, hide stale overlays and disable export. Camera rotation preserves the mask and numerical solar positions; panel edits preserve upstream files. Source-file changes are checked without recalculating. Close any locked diagnostic workbook and retry Update. Valid files are kept unchanged. Replacements are staged before publication. Verified old run folders are migrated after protecting selected inputs under Data/Inputs. Close the other SolarShade instance if this dataset is already in use.\n\nKeep APPLICATION.exe, Example, Data and Debug Data together when moving the package. Data stores settings, caches, extracted model weights and durable Profiles/Inputs. Keep these durable inputs when moving or backing up the application. Your own inputs may be anywhere. Data location:\n" + AppData.Root + ""),
            ("PV Autonomy", "Solar Irradiance estimates historical direct + sky-diffuse irradiance. Once its complete shaded dataset is current, open PV Autonomy to enter a daily consumption profile, panel area and efficiencies, battery capacity and initial charge. Evaluate system computes hourly battery charge and unmet load for this study. First model use is slower; later panel edits reuse expensive work."),
        ], panel =>
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
    public void SetProfileForTest(string path) => SelectProfile(path);
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
