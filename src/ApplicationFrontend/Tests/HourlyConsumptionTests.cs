using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class HourlyConsumptionTests
{
    [Theory]
    [InlineData("100", "Wh", 100)]
    [InlineData("360000", "J", 100)]
    [InlineData("100", "W", 100)]
    [InlineData("100", "VaDc", 100)]
    [InlineData("1,5", "Wh", 1.5)]
    [InlineData("5400", "J", 1.5)]
    [InlineData("0", "VaDc", 0)]
    public void ConvertsConsumptionToOneHourEnergy(string value, string unit, double wh)
        => Assert.Equal(wh, HourlyConsumption.ToWh(value, Enum.Parse<HourlyConsumptionUnit>(unit)));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1e308")]
    public void InvalidConsumptionCannotBecomeAnHourlyPattern(string value)
        => Assert.Throws<ArgumentException>(() => HourlyConsumption.ToWh(value, HourlyConsumptionUnit.Wh));

    [Fact]
    public void ConversionRejectsUnrepresentableValues()
    {
        Assert.Throws<ArgumentException>(() => HourlyConsumption.Convert("1e305", HourlyConsumptionUnit.Wh, HourlyConsumptionUnit.J));
        Assert.Throws<ArgumentException>(() => HourlyConsumption.ToWh("1e-323", HourlyConsumptionUnit.J));
        Assert.Equal("", HourlyConsumption.Convert("", HourlyConsumptionUnit.Wh, HourlyConsumptionUnit.J));
    }

    [Fact]
    public void UnitSelectionPreservesConsumptionAndOnlyApplyChangesSlots() => Sta(() =>
    {
        var editor = new DailyLoadEditor(); var before = PvSettingsDraft.Example().Load; editor.SetValues(before);
        var entry = Named<TextBox>(editor, "Same consumption every hour");
        var units = Named<ComboBox>(editor, "Hourly consumption unit");
        var apply = Find<Button>(editor, b => Equals(b.Content, "Apply to all 24 hours"));
        Assert.Equal(new[] { "Wh", "J", "W", "VA (DC)" }, units.Items.Cast<string>());
        int edits = 0; editor.Edited += _ => edits++;
        entry.Text = "1,5"; units.SelectedIndex = 1; Assert.Equal("5400", entry.Text);
        units.SelectedIndex = 2; Assert.Equal("1.5", entry.Text);
        units.SelectedIndex = 3; Assert.Equal("1.5", entry.Text);
        Assert.Equal(before, editor.Values); Assert.Equal(0, edits);
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(editor.Values, value => Assert.Equal("1.5", value)); Assert.Equal(1, edits);
        units.SelectedIndex = 1; entry.Text = "7200"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(editor.Values, value => Assert.Equal("2", value)); Assert.Equal(2, edits);
        return Task.CompletedTask;
    });

    [Fact]
    public void EmptyWatermarkIsNotZeroAndInvalidApplyIsAtomic() => Sta(() =>
    {
        var editor = new DailyLoadEditor(); var before = PvSettingsDraft.Example().Load; editor.SetValues(before);
        var entry = Named<TextBox>(editor, "Same consumption every hour");
        var units = Named<ComboBox>(editor, "Hourly consumption unit");
        var apply = Find<Button>(editor, b => Equals(b.Content, "Apply to all 24 hours"));
        var placeholder = Find<TextBlock>(editor, t => t.Text == "0");
        int edits = 0; editor.Edited += _ => edits++;
        Assert.Equal("", entry.Text); Assert.Equal(Visibility.Visible, placeholder.Visibility);
        units.SelectedIndex = 1; Assert.Equal("", entry.Text); units.SelectedIndex = 0;
        foreach (string bad in new[] { "", "-1", "NaN", "Infinity", "1e309", "1e308" })
        {
            entry.Text = bad; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(before, editor.Values); Assert.Equal(0, edits);
        }
        entry.Text = "1e305"; units.SelectedIndex = 1;
        Assert.Equal(0, units.SelectedIndex); Assert.Equal("1e305", entry.Text); Assert.Equal(before, editor.Values);
        entry.Text = "0"; Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(editor.Values, value => Assert.Equal("0", value)); Assert.Equal(1, edits);
        return Task.CompletedTask;
    });

    [Fact]
    public void ShortEditorScrollsBothQuickFillAndLastHour() => Sta(() =>
    {
        var editor = new DailyLoadEditor(); editor.Measure(new Size(260, 240)); editor.Arrange(new Rect(0, 0, 260, 240)); editor.UpdateLayout();
        var scroller = Assert.IsType<ScrollViewer>(editor.Content);
        Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
        Assert.True(scroller.ScrollableHeight > 0);
        Assert.Single(Descendants(editor).OfType<ScrollViewer>());
        var last = Named<TextBox>(editor, "Consumption 23:00 to 24:00 in Wh");
        scroller.ScrollToBottom(); editor.UpdateLayout();
        var bottom = last.TransformToAncestor(scroller).Transform(new Point(0, last.ActualHeight));
        Assert.InRange(bottom.Y, 0, scroller.ActualHeight);
        scroller.ScrollToTop(); editor.UpdateLayout();
        var first = Named<TextBox>(editor, "Same consumption every hour");
        var top = first.TransformToAncestor(scroller).Transform(new Point(0, 0));
        Assert.InRange(top.Y, 0, scroller.ActualHeight);
        return Task.CompletedTask;
    });

    private static T Named<T>(DependencyObject root, string name) where T : DependencyObject
        => Find<T>(root, item => AutomationProperties.GetName(item) == name);
    private static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
        => Assert.Single(Descendants(root).OfType<T>(), item => predicate(item));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
