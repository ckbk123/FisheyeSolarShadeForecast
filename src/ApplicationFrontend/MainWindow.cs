using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Camera;
using SolarShade.Shading;

namespace SolarShade.Desktop;

/// <summary>Application lifetime and navigation. Scientific and editing state belong to workspaces.</summary>
public sealed class MainWindow : Window
{
    private readonly WorkspaceHost host = new();
    private readonly Grid actions = new();
    public IrradianceWorkspace Irradiance { get; }
    public PvAutonomyWorkspace PvAutonomy { get; }
    public IrradianceWorkspaceController IrradianceController => Irradiance.Controller;
    public WorkspaceHost Workspaces => host;

    public MainWindow(Func<TimeZoneInfo>? systemZoneProvider = null, bool loadExampleOnFirstRun = true, AppServices? applicationServices = null)
    {
        Title = $"SolarShade · Solar Forecast Estimator · Preview {Program.PreviewVersion}"; Width = 1440; Height = 960; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowWorkArea.FitOnStartup(this);
        StudioTheme.Apply(this);
        Background = StudioTheme.Canvas;
        FontSize = 13;
        UseLayoutRounding = true;
        var root = new DockPanel(); Content = root;
        var header = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var headerBand = new Border { MinHeight = 58, Background = StudioTheme.Navy, Child = header };
        DockPanel.SetDock(headerBand, Dock.Top); root.Children.Add(headerBand);
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(18, 6, 10, 6) };
        title.Children.Add(new Image { Source = StudioTheme.MakeLogo(), Width = 31, Height = 31, Margin = new(0, 0, 9, 0), IsHitTestVisible = false });
        title.Children.Add(new TextBlock { Text = "SolarShade", Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(title);
        if (TryFindResource("HeaderButton") is Style headerButton) actions.Resources[typeof(Button)] = headerButton;
        actions.Margin = new(8, 4, 14, 4); actions.VerticalAlignment = VerticalAlignment.Center;
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(actions, 1); header.Children.Add(actions);
        bool? stackedHeader = null;
        FrameworkElement? visibleActions = null;
        void FitWordmark() => title.Visibility = header.ActualWidth < 600 && visibleActions != null
            ? Visibility.Collapsed : Visibility.Visible;
        header.SizeChanged += (_, _) =>
        {
            bool narrow = header.ActualWidth < 850;
            // The native title bar still identifies the app; reserve short, narrow windows for actions.
            FitWordmark();
            if (narrow == stackedHeader) return;
            stackedHeader = narrow;
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
            Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            header.ColumnDefinitions[1].Width = narrow ? new(0) : GridLength.Auto;
        };
        root.Children.Add(host);
        Irradiance = new(this, new(applicationServices ?? new()), systemZoneProvider, loadExampleOnFirstRun);
        host.Register(new("images", "Images & calibration", Irradiance.ImagesAndCalibration));
        var orientation = new OrientationWorkspace();
        var orientationTab = host.Register(new("orientation", "Orientation map", orientation));
        orientationTab.MouseEnter += async (_, _) =>
        {
            await Task.Delay(120);
            if (orientationTab.IsMouseOver) await orientation.Prepare();
        };
        orientationTab.GotKeyboardFocus += async (_, _) => await orientation.Prepare();

        host.Register(new("irradiance", "Solar Irradiance", Irradiance, Irradiance.Actions));
        host.Select("irradiance");
        var pvService = new PvEvaluationService(IrradianceController);
        PvAutonomy = new(this, IrradianceController, new(IrradianceController, pvService), Irradiance.RecheckSourcesAsync, () => host.Select("irradiance"));
        host.Register(new("pv", "PV Autonomy", PvAutonomy, PvAutonomy.Actions));
        void RefreshAvailability()
        {
            var state = IrradianceController.Source; host.SetAvailability("pv", state.IsReady, state.Reason);
        }
        IrradianceController.SourceChanged += (_, _) => RefreshAvailability(); RefreshAvailability();
        // Keep each toolbar attached after its first visit so switching tabs can reuse its visual tree.
        void ShowActions(FrameworkElement? next)
        {
            if (ReferenceEquals(visibleActions, next)) return;
            if (visibleActions != null) visibleActions.Visibility = Visibility.Collapsed;
            visibleActions = next;
            actions.Visibility = next == null ? Visibility.Collapsed : Visibility.Visible;
            FitWordmark();
            if (next == null) return;
            if (!actions.Children.Contains(next)) actions.Children.Add(next);
            next.Visibility = Visibility.Visible;
        }
        host.ActiveChanged += (_, _) =>
        {
            Irradiance.ShowImageSetup(host.Active?.Id == "images");
            ShowActions(host.Active?.Actions);
        };
        ShowActions(Irradiance.Actions);
        Closed += (_, _) => host.Dispose();
    }

    // Compatibility surface for the packaged smoke test and existing regression tests.
    public Evaluation? Completed => Irradiance.Completed;
    public Task LoadExample() => Irradiance.LoadExample();
    public void Calculate() => Irradiance.Calculate();
    public void Begin(bool refresh) => Irradiance.Begin(refresh);
    public void StopUpdate() => Irradiance.StopUpdate();
    public void RefreshSystemTimeZone() => Irradiance.RefreshSystemTimeZone();
    public void RecheckSources() => Irradiance.RecheckSources();
    public Task RecheckSourcesAsync() => Irradiance.RecheckSourcesAsync();
    public Task InputsSettled => Irradiance.InputsSettled;
    public void ShowMask(MaskAsset asset) => Irradiance.ShowMask(asset);
    public Task<(CalibrationProfile Profile, string Path, string Details)> CalibrateForTest(UserSettings inputs) => Irradiance.CalibrateForTest(inputs);
    public void SetProfileForTest(string path) => Irradiance.SetProfileForTest(path);
    public void SetTimeZoneForTest(bool automatic, string zoneId) => Irradiance.SetTimeZoneForTest(automatic, zoneId);
    public void SetPanelForTest(double tilt, double azimuth) => Irradiance.SetPanelForTest(tilt, azimuth);
    public void SetDiffuseModelForTest(bool value) => Irradiance.SetDiffuseModelForTest(value);
    public void CommitFieldForTest(string property, string value) => Irradiance.CommitFieldForTest(property, value);
    public void SetCameraPoseForTest(double bearing, double tilt, double roll) => Irradiance.SetCameraPoseForTest(bearing, tilt, roll);
    public void SetSunPathVisibleForTest(bool value) => Irradiance.SetSunPathVisibleForTest(value);
    public bool SunPathVisibleForTest => Irradiance.SunPathVisibleForTest;
    public bool SunPathPublishedEarlyForTest => Irradiance.SunPathPublishedEarlyForTest;
    public long RevisionForTest => Irradiance.RevisionForTest;
    public UpdateState UpdateStateForTest => Irradiance.UpdateStateForTest;
    public bool ExportEnabledForTest => Irradiance.ExportEnabledForTest;
    public bool UpdateEnabledForTest => Irradiance.UpdateEnabledForTest;
    public bool UpdatingForTest => Irradiance.UpdatingForTest;
    public void EditFieldForTest(string property, string value) => Irradiance.EditFieldForTest(property, value);
    public void SetCardinalsVisibleForTest(bool value) => Irradiance.SetCardinalsVisibleForTest(value);
    public bool CardinalsVisibleForTest => Irradiance.CardinalsVisibleForTest;
    public CardinalDirectionOverlayResult? CardinalsForTest => Irradiance.CardinalsForTest;
    public long OrientationRevisionForTest => Irradiance.OrientationRevisionForTest;
    public int CardinalGenerationCountForTest => Irradiance.CardinalGenerationCountForTest;
    public static BitmapImage Bitmap(byte[] bytes) => IrradianceWorkspace.Bitmap(bytes);
    public static string OrientationInputKey(UserSettings s) => IrradianceWorkspace.OrientationInputKey(s);
    public static TextBlock Label(string text, double size = 13, Brush? brush = null) => IrradianceWorkspace.Label(text, size, brush);
}

