using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SolarShade.Desktop;

public sealed class PvAutonomyWorkspace : UserControl, IDisposable
{
    private readonly Window owner;
    private readonly IrradianceWorkspaceController source;
    private readonly Action recheck;
    private readonly TextBlock sourceText = Text(""), status = Text(""), summary = Text("", 15), details = Text("", 11), hover = Text("Scroll to zoom · drag to pan. Red marks identify intervals with unmet load.", 11);
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
    public PvAutonomyWorkspace(Window owner, IrradianceWorkspaceController source, PvWorkspaceController controller, Action recheck, Action goToSource)
    {
        this.owner = owner; this.source = source; Controller = controller; this.recheck = recheck;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Example system", () => { Controller.SetDraft(PvSettingsDraft.Example()); LoadFields(); }));
        actions.Children.Add(Button("Help", ShowHelp)); export = Button("Export PV results", async () =>
        {
            recheck(); Controller.VerifyPublication(); if (!Controller.CanExport) return;
            var dialog = new OpenFolderDialog { Title = "Choose a folder for PV results" };
            if (dialog.ShowDialog(owner) == true) await Controller.ExportAsync(dialog.FolderName);
        }); actions.Children.Add(export); Actions = actions;
        var root = new DockPanel { Margin = new(16, 10, 16, 10) }; Content = root;
        var sourceBar = new DockPanel { Margin = new(0, 0, 0, 8) }; DockPanel.SetDock(sourceBar, Dock.Top); root.Children.Add(sourceBar);
        var go = Button("Go to irradiance", goToSource); DockPanel.SetDock(go, Dock.Right); sourceBar.Children.Add(go); sourceBar.Children.Add(sourceText);
        status.Margin = new(0, 7, 0, 0); DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new(280) }); columns.ColumnDefinitions.Add(new() { Width = new(14) }); columns.ColumnDefinitions.Add(new()); root.Children.Add(columns);
        var left = new Border { Background = Brushes.White, Child = load }; columns.Children.Add(left);
        var right = new DockPanel { Background = Brushes.White, Margin = new(0), LastChildFill = true }; Grid.SetColumn(right, 2); columns.Children.Add(right);
        var top = new StackPanel { Margin = new(12, 8, 12, 0) }; DockPanel.SetDock(top, Dock.Top); right.Children.Add(top);
        top.Children.Add(Text("PV System Autonomy", 21));
        top.ToolTip = "Evaluate energy supply and battery charge over the study period.";
        var settings = new WrapPanel(); top.Children.Add(settings);
        AddField(settings, "Panel area · m²", d => d.Area, (d, v) => d with { Area = v });
        AddField(settings, "Panel efficiency · %", d => d.PanelEfficiency, (d, v) => d with { PanelEfficiency = v });
        AddField(settings, "Conversion · %", d => d.ConversionEfficiency, (d, v) => d with { ConversionEfficiency = v });
        AddField(settings, "Battery capacity · Wh", d => d.Capacity, (d, v) => d with { Capacity = v });
        AddField(settings, "Initial charge · %", d => d.InitialSoc, (d, v) => d with { InitialSoc = v });
        fields[4].ToolTip = "Charge at the start of the entire study, not the displayed day.";
        fields[2].ToolTip = "Total PV-side conversion efficiency. Battery charging/discharging is ideal in this model.";
        var commands = new WrapPanel(); top.Children.Add(commands);
        evaluate = Button("Evaluate system", async () => { recheck(); await Controller.EvaluateAsync(); }); evaluate.Background = Brushes.Teal; evaluate.Foreground = Brushes.White;
        stop = Button("Stop", Controller.Stop); commands.Children.Add(evaluate); commands.Children.Add(stop);
        commands.Children.Add(Text("Initial charge applies at the study start.", 11));
        commands.Children.Add(Button("Model & totals", () => MessageBox.Show(owner, Controller.Result == null ? "Evaluate the system to see totals. Battery charge/discharge is ideal; conversion efficiency applies to PV only." : details.Text,
            Controller.IsCurrent ? "PV Autonomy · Model and totals" : "PV Autonomy · Previous totals (stale)", MessageBoxButton.OK, MessageBoxImage.Information)));
        summary.Margin = new(0, 5, 0, 2); top.Children.Add(summary);
        top.Children.Add(Text("Hourly energy and battery charge", 15));
        var legend = new WrapPanel(); top.Children.Add(legend);
        Legend(legend, "Load demand · Wh", PvSystemChart.LoadBrush, b => chart.ShowLoad = b);
        Legend(legend, "PV available · Wh", PvSystemChart.PvBrush, b => chart.ShowPv = b);
        Legend(legend, "Battery SoC · %", PvSystemChart.SocBrush, b => chart.ShowSoc = b);
        var bottom = new StackPanel { Margin = new(12, 0, 12, 8) }; DockPanel.SetDock(bottom, Dock.Bottom); right.Children.Add(bottom);
        var navigation = new WrapPanel(); bottom.Children.Add(navigation);
        navigation.Children.Add(Button("Full period", chart.Reset)); navigation.Children.Add(day);
        navigation.Children.Add(Button("←", () => MoveDay(-1)));
        navigation.Children.Add(Button("→", () => MoveDay(1)));
        day.SelectedDateChanged += (_, _) => { if (day.SelectedDate is { } date) chart.Day(date); };
        navigation.Children.Add(Button("First shortfall", () => { if (Controller.Result?.Summary.FirstShortfallIntervalStart is { } start) { day.SelectedDate = start.Date; chart.Day(start.Date); } }));
        navigation.Children.Add(Button("Hourly values", ShowTable));
        hover.MinHeight = 34; bottom.Children.Add(hover); chart.Hover += value => hover.Text = value;
        chart.Margin = new(10, 0, 10, 0); right.Children.Add(chart);
        load.Edited += values => Controller.SetDraft(Controller.Draft with { Load = values });
        Controller.Changed += OnChanged; source.SourceChanged += OnSourceChanged; owner.Activated += OnActivated;
        LoadFields(); Refresh();
    }
    private void AddField(Panel panel, string label, Func<PvSettingsDraft, string> get, Func<PvSettingsDraft, string, PvSettingsDraft> set)
    {
        var group = new StackPanel { Width = 132, Margin = new(0, 8, 6, 0) }; group.Children.Add(Text(label, 11));
        var box = new TextBox { Padding = new(6, 4, 6, 4), Margin = new(0, 3, 0, 4), Tag = get };
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
    private void OnActivated(object? sender, EventArgs e) { if (!disposed) Controller.VerifyPublication(); }
    private void Refresh()
    {
        var draft = Controller.Draft;
        if (fields.Any(f => f.Text != ((Func<PvSettingsDraft, string>)f.Tag)(draft)) || !load.Values.SequenceEqual(draft.Load)) LoadFields();
        var snapshot = source.Source.Snapshot;
        sourceText.Text = snapshot == null ? source.Source.Reason : $"Shaded irradiance ready · {snapshot.Settings.Start:yyyy-MM-dd} – {snapshot.Settings.End:yyyy-MM-dd} · {snapshot.TimeZoneId} · tilt {snapshot.Settings.PanelTilt:0.#}° · azimuth {snapshot.Settings.PanelAzimuth:0.#}°";
        evaluate.IsEnabled = Controller.CanEvaluate; stop.IsEnabled = Controller.IsBusy; export.IsEnabled = Controller.CanExport;
        status.Text = snapshot == null ? source.Source.Reason : Controller.Problem ?? Controller.Status;
        chart.SetResult(Controller.Result); chart.Stale = !Controller.IsCurrent; chart.Opacity = Controller.IsCurrent ? 1 : .55; chart.InvalidateVisual();
        if (Controller.Result is { } result)
        {
            var s = result.Summary;
            summary.Text = $"{(Controller.IsCurrent ? "" : "Previous · ")}Minimum SoC {s.MinimumSocPercent:0.#}%    Final {result.Hours[^1].EndSocPercent:0.#}%    Unmet {s.UnmetLoadWh:0.##} Wh";
            details.Text = $"PV available {s.PvWh:0.##} Wh · Load demand {s.LoadWh:0.##} Wh · Served {s.ServedLoadWh:0.##} Wh · Curtailed {s.CurtailedWh:0.##} Wh\n{s.HoursContainingShortfall} intervals contain unmet load. First: {s.FirstShortfallIntervalStart?.ToString("yyyy-MM-dd HH:mm zzz") ?? "none"}.\nMinimum charge includes subhour steps and the initial charge. The first shortfall identifies an interval, not an exact outage instant.\n{result.Assumptions}\nConservative shading assumptions carry through from Solar Irradiance. No unmet load in this study does not guarantee supply in unseen weather.";
            day.DisplayDateStart = result.Hours[0].Start.Date; day.DisplayDateEnd = result.Hours[^1].Start.Date;
        }
        else summary.Text = "Minimum SoC —    Final SoC —    Unmet energy —";
    }
    private void ShowTable()
    {
        if (Controller.Result is not { } result) return;
        var table = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            ItemsSource = result.Hours, CanUserAddRows = false };
        foreach (var (title, property, format) in new[] { ("Start", "Start", "yyyy-MM-dd HH:mm zzz"), ("End", "End", "yyyy-MM-dd HH:mm zzz"),
            ("Partial hour", "IsPartialHour", ""), ("Load · Wh", "LoadWh", "0.###"), ("PV · Wh", "PvWh", "0.###"), ("End SoC · %", "EndSocPercent", "0.###"), ("Unmet · Wh", "UnmetLoadWh", "0.###") })
            table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(property) { StringFormat = format } });
        new Window { Owner = owner, Title = Controller.IsCurrent ? "PV Autonomy · Hourly values" : "PV Autonomy · Previous hourly values (stale)",
            Content = table, Width = 1000, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private void ShowHelp() => MessageBox.Show(owner,
        "1. Complete and update Solar Irradiance.\n2. Enter 24 hourly consumption values in Wh. They repeat by study-local hour (DST days can have 23 or 25 hours).\n3. Enter panel area, efficiencies, battery capacity in Wh and charge at the study start. Click Evaluate system.\n\nThe graph overlays load demand and PV energy available on the Wh axis, with battery charge on the fixed 0–100% axis. Red marks mean unmet load, even if charge recovered before the hour ended. Hover for actual interval bounds and duration. Zooming never resets the battery.\n\nConversion losses apply to PV only. Battery charge/discharge is ideal, all capacity is accessible, and power limits, ageing and temperature effects are omitted. Results describe the supplied study period only.\n\nExport PV results saves CSV, JSON and XLSX, including inputs, assumptions and the full irradiance source in JSON. Settings are restored on restart; results require an explicit evaluation.",
        "PV Autonomy · Help", MessageBoxButton.OK, MessageBoxImage.Information);
    internal static TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 2), Foreground = Brushes.DarkSlateGray };
    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new(10, 6, 10, 6), Margin = new(0, 3, 6, 3), Background = Brushes.White, Foreground = Brushes.DarkSlateGray, BorderBrush = Brushes.LightSteelBlue };
        button.Click += (_, _) => action(); return button;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; owner.Activated -= OnActivated;
        source.SourceChanged -= OnSourceChanged; Controller.Changed -= OnChanged; Controller.Dispose();
    }
}
