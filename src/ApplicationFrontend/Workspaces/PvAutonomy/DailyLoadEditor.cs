using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SolarShade.Desktop;

public sealed class DailyLoadEditor : UserControl
{
    private readonly TextBox[] values = new TextBox[24];
    private readonly TextBlock total = PvAutonomyWorkspace.Text("Nominal daily total: —");
    private readonly TextBlock error = PvAutonomyWorkspace.Text("");
    private readonly TextBox constant = new() { Padding = new(5), MinWidth = 64 };
    private readonly ComboBox constantUnit = new() { Width = 92, Margin = new(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock constantHint = PvAutonomyWorkspace.Text("", 11);
    private HourlyConsumptionUnit selectedUnit;
    private bool changingUnit;
    private bool loading;
    public event Action<string[]>? Edited;
    public string[] Values => values.Select(v => v.Text).ToArray();
    public DailyLoadEditor()
    {
        var root = new StackPanel { Margin = new(12) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heading = new StackPanel(); root.Children.Add(heading);
        heading.Children.Add(PvAutonomyWorkspace.Text("Daily consumption", 18));
        heading.Children.Add(PvAutonomyWorkspace.Text("Repeats each day in study time.\nEnergy used in each one-hour slot.", 11));
        var footer = new StackPanel(); root.Children.Add(footer);
        footer.Children.Add(total);
        footer.Children.Add(PvAutonomyWorkspace.Button("Paste 24 values", () => TryPaste(Clipboard.GetText())));
        footer.Children.Add(PvAutonomyWorkspace.Text("Same consumption every hour", 11));
        var fill = new StackPanel(); var entry = new Grid(); entry.ColumnDefinitions.Add(new()); entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var number = new Grid(); number.Children.Add(constant);
        var placeholder = new TextBlock { Text = "0", Foreground = Brushes.Gray, Margin = new(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        number.Children.Add(placeholder); entry.Children.Add(number);
        constant.TextChanged += (_, _) => placeholder.Visibility = constant.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(constant, "Same consumption every hour");
        System.Windows.Automation.AutomationProperties.SetName(constantUnit, "Hourly consumption unit");
        foreach (string unit in new[] { "Wh", "J", "W", "VA (DC)" }) constantUnit.Items.Add(unit);
        constantUnit.SelectedIndex = 0; constantUnit.SelectionChanged += (_, _) => ChangeConstantUnit();
        constantUnit.ToolTip = "Changing units converts the entered value and keeps the same consumption.";
        Grid.SetColumn(constantUnit, 1); entry.Children.Add(constantUnit); fill.Children.Add(entry);
        fill.Children.Add(constantHint); UpdateConstantHint();
        fill.Children.Add(PvAutonomyWorkspace.Button("Apply to all 24 hours", ApplyConstant));
        footer.Children.Add(fill); footer.Children.Add(error);
        var rows = new StackPanel { Margin = new(0, 8, 0, 0) };
        rows.Children.Add(PvAutonomyWorkspace.Text("Hour (study time)       Consumption (Wh)", 11));
        for (int i = 0; i < 24; i++)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new(123) }); row.ColumnDefinitions.Add(new());
            var label = PvAutonomyWorkspace.Text($"{i:00}:00 – {i + 1:00}:00"); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
            var box = new TextBox { Padding = new(6, 3, 6, 3), Margin = new(2, 2, 0, 2), MinHeight = 28 };
            System.Windows.Automation.AutomationProperties.SetName(box, $"Consumption {i:00}:00 to {i + 1:00}:00 in Wh");
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
        constantHint.Text = selectedUnit switch
        {
            HourlyConsumptionUnit.J => "3600 J = 1 Wh per one-hour slot.",
            HourlyConsumptionUnit.W => "Average power: 1 W = 1 Wh per one-hour slot.",
            HourlyConsumptionUnit.VaDc => "DC voltage × current: 1 VA = 1 W = 1 Wh per one-hour slot.",
            _ => "Energy in Wh per one-hour slot."
        };
        constantHint.Text += "\nApplying replaces all 24 hourly values.";
        constant.ToolTip = constantHint.Text + " For example, 100 Wh each hour gives 2400 Wh per nominal 24-hour day. Daylight-saving days may have 23 or 25 hours.";
        System.Windows.Automation.AutomationProperties.SetHelpText(constant, constantHint.Text);
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
