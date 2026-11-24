using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SolarShade.Desktop;

public sealed class DailyLoadEditor : UserControl
{
    private readonly TextBox[] values = new TextBox[24];
    private readonly TextBlock total = PvAutonomyWorkspace.Text("Nominal daily total —");
    private readonly TextBlock error = PvAutonomyWorkspace.Text("");
    private readonly TextBox constant = new() { Padding = new(5), Margin = new(0), MinWidth = 64 };
    private readonly ComboBox constantUnit = new() { Width = 92, Margin = new(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private HourlyConsumptionUnit selectedUnit;
    private bool changingUnit;
    private bool loading;
    public event Action<string[]>? Edited;
    public string[] Values => values.Select(v => v.Text).ToArray();
    public DailyLoadEditor()
    {
        var root = new StackPanel { Margin = new(14) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(PvAutonomyWorkspace.Heading("Daily consumption", 18));
        var footer = new StackPanel(); root.Children.Add(footer);
        total.Margin = new(0, 3, 0, 8);
        footer.Children.Add(total);
        footer.Children.Add(PvAutonomyWorkspace.Button("Paste 24 values", () => TryPaste(Clipboard.GetText()),
            "Paste 24 hourly energy values in Wh, starting at midnight. The pattern repeats in the study time zone."));
        footer.Children.Add(PvAutonomyWorkspace.Text("Same consumption every hour"));
        var fill = new StackPanel(); var entry = new Grid(); entry.ColumnDefinitions.Add(new()); entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var number = new Grid(); number.Children.Add(constant);
        var placeholder = new TextBlock { Text = "0", Foreground = StudioTheme.Muted, Margin = new(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        number.Children.Add(placeholder); entry.Children.Add(number);
        constant.TextChanged += (_, _) => placeholder.Visibility = constant.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(constant, "Same consumption every hour");
        System.Windows.Automation.AutomationProperties.SetName(constantUnit, "Hourly consumption unit");
        foreach (string unit in new[] { "Wh", "J", "W", "VA (DC)" }) constantUnit.Items.Add(unit);
        constantUnit.SelectedIndex = 0; constantUnit.SelectionChanged += (_, _) => ChangeConstantUnit();
        StudioTheme.Help(constantUnit, "Changing units converts the entry and preserves its energy per hour.");
        Grid.SetColumn(constantUnit, 1); entry.Children.Add(constantUnit); fill.Children.Add(entry);
        UpdateConstantHint();
        fill.Children.Add(PvAutonomyWorkspace.Button("Apply to all 24 hours", ApplyConstant, "Replace every hourly slot with the energy entered above."));
        error.Foreground = Brushes.Firebrick;
        footer.Children.Add(fill); footer.Children.Add(error);
        var rows = new StackPanel { Margin = new(0, 8, 0, 0) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = new(123) }); header.ColumnDefinitions.Add(new());
        header.Children.Add(PvAutonomyWorkspace.Heading("Study hour", 12));
        var energyLabel = PvAutonomyWorkspace.Heading("Consumption · Wh", 12);
        Grid.SetColumn(energyLabel, 1); header.Children.Add(energyLabel); rows.Children.Add(header);
        for (int i = 0; i < 24; i++)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new(123) }); row.ColumnDefinitions.Add(new());
            var label = PvAutonomyWorkspace.Text($"{i:00}:00 – {i + 1:00}:00"); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
            var box = new TextBox { Padding = new(6, 3, 6, 3), Margin = new(2, 2, 0, 2), MinHeight = 28 };
            System.Windows.Automation.AutomationProperties.SetName(box, $"Consumption {i:00}:00 to {i + 1:00}:00 in Wh");
            StudioTheme.Help(box, "Enter the energy used in this one-hour slot in Wh. This schedule repeats by local hour in the study time zone.");
            values[i] = box; Grid.SetColumn(box, 1); row.Children.Add(box); rows.Children.Add(row);
            box.TextChanged += (_, _) => { if (loading) return; RefreshTotal(); Edited?.Invoke(Values); };
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                if (e.DataObject.GetData(DataFormats.UnicodeText) is string text && (text.Contains('\n') || text.Contains('\t')))
                { e.CancelCommand(); TryPaste(text); }
            });
        }
        root.Children.Add(rows);
    }
    private void ApplyConstant()
    {
        try
        {
            string wh = HourlyConsumption.Format(HourlyConsumption.ToWh(constant.Text, selectedUnit));
            SetValues(Enumerable.Repeat(wh, 24).ToArray()); Edited?.Invoke(Values); error.Text = "";
        }
        catch (ArgumentException ex) { error.Text = ex.Message; }
    }
    private void ChangeConstantUnit()
    {
        if (changingUnit || constantUnit.SelectedIndex < 0) return;
        var next = (HourlyConsumptionUnit)constantUnit.SelectedIndex;
        try
        {
            string converted = HourlyConsumption.Convert(constant.Text, selectedUnit, next);
            selectedUnit = next; constant.Text = converted; UpdateConstantHint(); error.Text = "";
        }
        catch (ArgumentException ex)
        {
            changingUnit = true;
            try { constantUnit.SelectedIndex = (int)selectedUnit; }
            finally { changingUnit = false; }
            error.Text = ex.Message;
        }
    }
    private void UpdateConstantHint()
    {
        string conversion = selectedUnit switch
        {
            HourlyConsumptionUnit.J => "3,600 J in one hourly slot equals 1 Wh.",
            HourlyConsumptionUnit.W => "An average draw of 1 W for an hour uses 1 Wh.",
            HourlyConsumptionUnit.VaDc => "For DC loads, 1 VA for an hour uses 1 Wh.",
            _ => "Enter the energy consumed in one hour, in Wh."
        };
        StudioTheme.Help(constant, conversion + " Applying this value replaces all 24 slots. A 100 Wh hourly load uses 2,400 Wh in a 24-hour day. Daylight-saving days may have 23 or 25 hours.");
    }
    public void SetValues(string[] load)
    { loading = true; for (int i = 0; i < 24; i++) values[i].Text = load[i]; loading = false; RefreshTotal(); }
    public bool TryPaste(string text)
    {
        try { var load = PvSettingsDraft.ParsePaste(text); SetValues(load); Edited?.Invoke(load); error.Text = ""; return true; }
        catch (ArgumentException ex) { error.Text = ex.Message; return false; }
    }
    private void RefreshTotal()
    {
        try { var load = Values.Select(v => PvSettingsDraft.Number(v, "Consumption")).ToArray(); total.Text = load.Any(v => v < 0) ? "Correct negative consumption values." : $"Nominal daily total: {load.Sum():0.##} Wh"; }
        catch (ArgumentException) { total.Text = "Complete all 24 slots for the daily total."; }
    }
}
