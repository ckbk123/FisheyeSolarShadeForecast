using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SolarShade.Desktop;

public sealed class PvAutonomyWorkspace : UserControl, IDisposable
{
    private readonly Window owner;
    private HelpWindow? helpWindow;
    private readonly IrradianceWorkspaceController source;
    private readonly Func<Task> recheck;
    private readonly TextBlock sourceText = Text(""), status = Text(""), summary = Text("", 15), hover = Text("");
    private readonly TextBlock shortfallKey = Text("━  Unmet load");
    private readonly Button evaluate, stop, export;
    private readonly List<TextBox> fields = [];
    private readonly DailyLoadEditor load = new();
    private readonly PvSystemChart chart = new();
    private readonly DatePicker day = new() { Width = 135, Margin = new(6, 3, 6, 3) };
    private bool loading, disposed;
    public PvWorkspaceController Controller { get; }
    public FrameworkElement Actions { get; }
    public DailyLoadEditor LoadEditor => load;
    public PvSystemChart Chart => chart;
    public PvAutonomyWorkspace(Window owner, IrradianceWorkspaceController source, PvWorkspaceController controller, Func<Task> recheck, Action goToSource)
    {
        this.owner = owner; this.source = source; Controller = controller; this.recheck = recheck;
        var actions = new WrapPanel();
        actions.Children.Add(Button("Example system", () => { Controller.SetDraft(PvSettingsDraft.Example()); LoadFields(); }));
        actions.Children.Add(Button("Help", ShowHelp)); export = Button("Export PV results", async () =>
        {
            await recheck(); await Controller.VerifyPublicationAsync(); if (!Controller.CanExport) return;
            var dialog = new OpenFolderDialog { Title = "Choose a folder for PV results" };
            if (dialog.ShowDialog(owner) == true) await Controller.ExportAsync(dialog.FolderName);
        }); actions.Children.Add(export); Actions = actions;
        var root = new DockPanel { Margin = new(16, 10, 16, 10) }; Content = root;
        var columns = new Grid();
        var left = Card(load); columns.Children.Add(left);
        var right = new Grid();
        var results = Card(right); columns.Children.Add(results);
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { MinHeight = 220 });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new WorkspaceViewport(columns, left, results, sidebarWidth: 300, contentMinimumWidth: 500,
            sidebarMinimumHeight: 600, contentMinimumHeight: 600));
        var top = new StackPanel { Margin = new(16, 12, 16, 0) }; right.Children.Add(top);
        top.Children.Add(Heading("System settings", 19));
        sourceText.Focusable = true; sourceText.Margin = new(0, 2, 0, 4); top.Children.Add(sourceText);
        var settings = new WrapPanel(); top.Children.Add(settings);
        AddField(settings, "Panel area · m²", "Active panel area receiving the study’s shaded irradiance.", d => d.Area, (d, v) => d with { Area = v });
        AddField(settings, "Panel efficiency · %", "Percentage of incident solar energy converted to electricity by the panel.", d => d.PanelEfficiency, (d, v) => d with { PanelEfficiency = v });
        AddField(settings, "PV conversion efficiency · %", "Percentage of the panel’s electrical output available to the load and battery. Battery charging and discharging are ideal in this model.", d => d.ConversionEfficiency, (d, v) => d with { ConversionEfficiency = v });
        AddField(settings, "Battery capacity · Wh", "Usable energy the battery can store when fully charged.", d => d.Capacity, (d, v) => d with { Capacity = v });
        AddField(settings, "Initial charge · %", "Battery charge at the start of the entire study period. Navigating the chart preserves the simulated charge.", d => d.InitialSoc, (d, v) => d with { InitialSoc = v });
        var commands = new WrapPanel(); top.Children.Add(commands);
        evaluate = Button("Evaluate system", async () => { await recheck(); await Controller.EvaluateAsync(); });
        evaluate.Background = StudioTheme.Blue; evaluate.BorderBrush = StudioTheme.Blue; evaluate.Foreground = Brushes.White;
        StudioTheme.Help(evaluate, "Calculate hourly PV supply, consumption and battery charge from the current shaded irradiance study.");
        stop = Button("Stop", Controller.Stop); commands.Children.Add(evaluate); commands.Children.Add(stop);
        commands.Children.Add(Button("Model & totals", ShowModelTotals));
        summary.Margin = new(0, 5, 0, 2); top.Children.Add(summary);
        var chartHeading = Heading("Hourly energy and battery charge", 16);
        chartHeading.Margin = new(0, 10, 0, 2); top.Children.Add(chartHeading);
        var legend = new WrapPanel(); top.Children.Add(legend);
        Legend(legend, "Load demand · Wh", PvSystemChart.LoadBrush, b => chart.ShowLoad = b);
        Legend(legend, "PV available · Wh", PvSystemChart.PvBrush, b => chart.ShowPv = b);
        Legend(legend, "Battery charge · %", PvSystemChart.SocBrush, b => chart.ShowSoc = b);
        shortfallKey.Foreground = Brushes.Crimson; shortfallKey.VerticalAlignment = VerticalAlignment.Center;
        legend.Children.Add(shortfallKey);
        var bottom = new StackPanel { Margin = new(12, 0, 12, 8) }; Grid.SetRow(bottom, 2); right.Children.Add(bottom);
        var navigation = new WrapPanel(); bottom.Children.Add(navigation);
        navigation.Children.Add(Button("Full period", chart.Reset, "Show the whole study. Scroll over the chart to zoom and drag to pan.")); navigation.Children.Add(day);
        System.Windows.Automation.AutomationProperties.SetName(day, "Displayed study day");
        var previousDay = Button("←", () => MoveDay(-1), "Show the previous day.");
        var nextDay = Button("→", () => MoveDay(1), "Show the next day.");
        System.Windows.Automation.AutomationProperties.SetName(previousDay, "Previous day");
        System.Windows.Automation.AutomationProperties.SetName(nextDay, "Next day");
        navigation.Children.Add(previousDay); navigation.Children.Add(nextDay);
        day.SelectedDateChanged += (_, _) => { if (day.SelectedDate is { } date) chart.Day(date); };
        navigation.Children.Add(Button("First shortfall", () => { if (Controller.Result?.Summary.FirstShortfallIntervalStart is { } start) { day.SelectedDate = start.Date; chart.Day(start.Date); } },
            "Show the first interval with unmet consumption. Red marks along the chart identify these intervals."));
        navigation.Children.Add(Button("Hourly values", ShowTable, "Inspect the actual interval bounds, energy values, battery charge and unmet consumption."));
        hover.MinHeight = 20; bottom.Children.Add(hover); chart.Hover += value => hover.Text = value;
        status.Margin = new(0, 7, 0, 0); bottom.Children.Add(status);
        chart.Margin = new(10, 0, 10, 0); Grid.SetRow(chart, 1); right.Children.Add(chart);
        load.Edited += values => Controller.SetDraft(Controller.Draft with { Load = values });
        Controller.Changed += OnChanged; source.SourceChanged += OnSourceChanged; owner.Activated += OnActivated;
        LoadFields(); Refresh();
    }
    private void AddField(Panel panel, string label, string help, Func<PvSettingsDraft, string> get, Func<PvSettingsDraft, string, PvSettingsDraft> set)
    {
        var group = new StackPanel { Width = 150, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 8, 8, 0) }; group.Children.Add(Text(label));
        var box = new TextBox { Padding = new(6, 4, 6, 4), Margin = new(0, 3, 0, 4), Tag = get };
        StudioTheme.Help(box, help);
        System.Windows.Automation.AutomationProperties.SetName(box, label); fields.Add(box); group.Children.Add(box); panel.Children.Add(group);
        box.TextChanged += (_, _) => { if (!loading) Controller.SetDraft(set(Controller.Draft, box.Text)); };
    }
    private void LoadFields()
    { loading = true; var draft = Controller.Draft; foreach (var field in fields) field.Text = ((Func<PvSettingsDraft, string>)field.Tag)(draft); load.SetValues(draft.Load); loading = false; }
    private void Legend(Panel parent, string label, Brush color, Action<bool> change)
    {
        var check = new CheckBox { Content = label, Foreground = color, IsChecked = true, Margin = new(0, 5, 16, 5) };
        check.Click += (_, _) => { change(check.IsChecked == true); chart.InvalidateVisual(); }; parent.Children.Add(check);
    }
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void MoveDay(int offset)
    {
        if (Controller.Result is not { } r) return;
        var next = (day.SelectedDate ?? r.Hours[0].Start.Date).AddDays(offset);
        day.SelectedDate = next < r.Hours[0].Start.Date ? r.Hours[0].Start.Date : next > r.Hours[^1].Start.Date ? r.Hours[^1].Start.Date : next;
    }
    private void OnSourceChanged(object? sender, EventArgs e) => Refresh();
    private async void OnActivated(object? sender, EventArgs e) { await recheck(); if (!disposed) await Controller.VerifyPublicationAsync(); }
    private void Refresh()
    {
        var draft = Controller.Draft;
        if (fields.Any(f => f.Text != ((Func<PvSettingsDraft, string>)f.Tag)(draft)) || !load.Values.SequenceEqual(draft.Load)) LoadFields();
        var snapshot = source.Source.Snapshot;
        sourceText.Visibility = snapshot == null ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot != null)
        {
            var study = snapshot.Settings;
            var angles = EquipmentOrientation.Describe(study);
            string facing = angles.PanelAzimuth is double azimuth ? $"{azimuth:0.##}° from south" : "undefined (flat)";
            sourceText.Text = study.Start.Date == study.End.Date ? $"Study · {study.Start:dd.MM.yyyy}" : $"Study · {study.Start:dd.MM.yyyy} – {study.End:dd.MM.yyyy}";
            StudioTheme.Help(sourceText, $"{snapshot.TimeZoneId} · panel tilt {angles.PanelTilt:0.##}° · top heading {study.PanelTopHeading:0.##}° clockwise from true north · front azimuth {facing}");
        }
        evaluate.IsEnabled = Controller.CanEvaluate; stop.IsEnabled = Controller.IsBusy; export.IsEnabled = Controller.CanExport;
        status.Text = snapshot == null ? source.Source.Reason : Controller.Problem ?? Controller.Status;
        chart.SetResult(Controller.Result); chart.Stale = !Controller.IsCurrent; chart.Opacity = Controller.IsCurrent ? 1 : .55; chart.InvalidateVisual();
        summary.Visibility = Controller.Result == null ? Visibility.Collapsed : Visibility.Visible;
        shortfallKey.Visibility = Controller.Result?.Summary.UnmetLoadWh > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Controller.Result is { } result)
        {
            var s = result.Summary;
            summary.Text = $"{(Controller.IsCurrent ? "" : "Previous · ")}Minimum charge {s.MinimumSocPercent:0.#}%    Final charge {result.Hours[^1].EndSocPercent:0.#}%    Unmet load {s.UnmetLoadWh:0.##} Wh";
            day.DisplayDateStart = result.Hours[0].Start.Date; day.DisplayDateEnd = result.Hours[^1].Start.Date;
        }
    }
    private void ShowModelTotals()
    {
        var result = Controller.Result;
        string title = "PV Autonomy · Model and totals";
        string message = "Evaluate the system to see totals. Conversion efficiency applies to PV generation. Battery charging and discharging are ideal in this model.";
        if (result != null)
        {
            var s = result.Summary;
            if (!Controller.IsCurrent) title = "PV Autonomy · Previous totals (stale)";
            message = $"PV available {s.PvWh:0.##} Wh · Load demand {s.LoadWh:0.##} Wh · Served {s.ServedLoadWh:0.##} Wh · Curtailed {s.CurtailedWh:0.##} Wh\n" +
                $"Intervals with unmet load: {s.HoursContainingShortfall}. First interval start: {s.FirstShortfallIntervalStart?.ToString("yyyy-MM-dd HH:mm zzz") ?? "none"}.\n" +
                $"Minimum charge includes the initial charge and all subhour steps.\n{result.Assumptions}\n" +
                "The calculation uses Solar Irradiance’s conservative shading assumptions. Its supply estimate applies to this study period.";
        }
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ShowTable()
    {
        if (Controller.Result is not { } result) return;
        var table = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            ItemsSource = result.Hours, CanUserAddRows = false };
        foreach (var (title, property, format) in new[] { ("Start", "Start", "yyyy-MM-dd HH:mm zzz"), ("End", "End", "yyyy-MM-dd HH:mm zzz"),
            ("Partial hour", "IsPartialHour", ""), ("Consumption · Wh", "LoadWh", "0.###"), ("PV available · Wh", "PvWh", "0.###"), ("End battery charge · %", "EndSocPercent", "0.###"), ("Unmet consumption · Wh", "UnmetLoadWh", "0.###") })
            table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(property) { StringFormat = format } });
        var dialog = new Window { Owner = owner, Title = Controller.IsCurrent ? "PV Autonomy · Hourly values" : "PV Autonomy · Previous hourly values (stale)",
            Content = table, Width = 1000, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowWorkArea.FitToOwner(dialog, owner); dialog.ShowDialog();
    }
    private void ShowHelp() => HelpWindow.OpenGuide(ref helpWindow, owner, "PV Autonomy · Help",
        [
            ("Set up a study", "1. Complete and update Solar Irradiance.\n2. Enter 24 hourly consumption values in Wh. They repeat by study-local hour (DST days can have 23 or 25 hours).\n3. Enter panel area, efficiencies, battery capacity in Wh and charge at the study start. Click Evaluate system."),
            ("Read the energy chart", "The Wh axis shows demand and available PV energy. The % axis shows battery charge. Red marks flag unmet load, even if charge recovered before the hour ended. Hover for interval readings and the duration of partial hours. Open Hourly values to compare rows."),
            ("Battery assumptions", "Conversion losses apply to PV only. Battery charge/discharge is ideal, all capacity is accessible, and power limits, ageing and temperature effects are omitted. Results describe the supplied study period only."),
            ("Export and restore", "Export PV results saves CSV, JSON and XLSX, including inputs, assumptions and the full irradiance source in JSON. Settings are restored on restart; results require an explicit evaluation."),
        ]);
    private static Border Card(UIElement child) => new() { Child = child, Background = StudioTheme.Surface, BorderBrush = StudioTheme.Line, BorderThickness = new(1), CornerRadius = new(8) };
    internal static TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 2), Foreground = StudioTheme.Muted };
    internal static TextBlock Heading(string value, double size) => new() { Text = value, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = StudioTheme.Ink, TextWrapping = TextWrapping.Wrap };
    internal static Button Button(string label, Action action, string? help = null)
    {
        var button = new Button { Content = label, Padding = new(10, 6, 10, 6), Margin = new(0, 3, 6, 3) };
        if (help != null) StudioTheme.Help(button, help);
        button.Click += (_, _) => action(); return button;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; owner.Activated -= OnActivated;
        source.SourceChanged -= OnSourceChanged; Controller.Changed -= OnChanged; Controller.Dispose();
    }
}
