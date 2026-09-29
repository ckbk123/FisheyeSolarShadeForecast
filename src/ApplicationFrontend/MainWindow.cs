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
    private readonly ContentControl actions = new();
    public IrradianceWorkspace Irradiance { get; }
    public PvAutonomyWorkspace PvAutonomy { get; }
    public IrradianceWorkspaceController IrradianceController => Irradiance.Controller;
    public WorkspaceHost Workspaces => host;

    public MainWindow(Func<TimeZoneInfo>? systemZoneProvider = null, bool loadExampleOnFirstRun = true, AppServices? applicationServices = null)
    {
        Title = "SolarShade · Solar Forecast Estimator"; Width = 1440; Height = 960; MinWidth = 1080; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(242, 246, 246));
        FontFamily = new("Segoe UI"); FontSize = 13; Foreground = new SolidColorBrush(Color.FromRgb(25, 48, 58));
        UseLayoutRounding = true;
        var root = new DockPanel(); Content = root;
        var header = new Grid { Height = 72, Background = Foreground };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new StackPanel { Margin = new(24, 10, 0, 0) };
        title.Children.Add(Label("SOLARSHADE", 24, Brushes.White));
        title.Children.Add(Label("Solar energy studies", 12, Brushes.LightGray)); header.Children.Add(title);
        actions.Margin = new(8, 0, 20, 0); actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1); header.Children.Add(actions);
        root.Children.Add(host);
        Irradiance = new(this, new(applicationServices ?? new()), systemZoneProvider, loadExampleOnFirstRun);
        host.Register(new("irradiance", "Solar Irradiance", Irradiance, Irradiance.Actions));
        var pvService = new PvEvaluationService(IrradianceController);
        PvAutonomy = new(this, IrradianceController, new(IrradianceController, pvService), Irradiance.RecheckSources, () => host.Select("irradiance"));
        host.Register(new("pv", "PV Autonomy", PvAutonomy, PvAutonomy.Actions));
        var availability = Label("", 11); availability.Margin = new(20, 3, 0, 3); DockPanel.SetDock(availability, Dock.Top);
        root.Children.Insert(1, availability);
        void RefreshAvailability()
        {
            var state = IrradianceController.Source; host.SetAvailability("pv", state.IsReady, state.Reason);
            availability.Text = state.IsReady ? "" : "PV Autonomy · " + state.Reason;
            availability.Visibility = state.IsReady ? Visibility.Collapsed : Visibility.Visible;
        }
        IrradianceController.SourceChanged += (_, _) => RefreshAvailability(); RefreshAvailability();
        host.ActiveChanged += (_, _) => actions.Content = host.Active?.Actions;
        actions.Content = Irradiance.Actions;
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

