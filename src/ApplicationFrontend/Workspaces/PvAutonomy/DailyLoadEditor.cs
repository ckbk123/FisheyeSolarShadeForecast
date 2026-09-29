using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SolarShade.Desktop;

public sealed class DailyLoadEditor : UserControl
{
    private readonly TextBox[] values = new TextBox[24];
    private readonly TextBlock total = PvAutonomyWorkspace.Text("Nominal daily total: —");
    private readonly TextBlock error = PvAutonomyWorkspace.Text("");
    private bool loading;
    public event Action<string[]>? Edited;
    public string[] Values => values.Select(v => v.Text).ToArray();
    public DailyLoadEditor()
    {
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(PvAutonomyWorkspace.Text("Daily consumption", 18));
        heading.Children.Add(PvAutonomyWorkspace.Text("Repeats each day in study time.\nEnergy used in each one-hour slot.", 11));
        heading.Children.Add(PvAutonomyWorkspace.Text("Hour (study time)       Consumption (Wh)", 11));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(total);
        footer.Children.Add(PvAutonomyWorkspace.Button("Paste 24 values", () => TryPaste(Clipboard.GetText())));
        var fill = new DockPanel(); var constant = new TextBox { Width = 80, Padding = new(5), ToolTip = "Constant consumption in Wh for every hour" };
        System.Windows.Automation.AutomationProperties.SetName(constant, "Constant hourly consumption in Wh");
        fill.Children.Add(constant); fill.Children.Add(PvAutonomyWorkspace.Button("Fill all hours", () =>
        {
            try { if (PvSettingsDraft.Number(constant.Text, "Consumption") < 0) throw new ArgumentException("Consumption cannot be negative."); SetValues(Enumerable.Repeat(constant.Text, 24).ToArray()); Edited?.Invoke(Values); error.Text = ""; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        })); footer.Children.Add(fill); footer.Children.Add(error);
        var rows = new StackPanel();
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
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
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
