using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class ExportDataWindowTests
{
    [Theory]
    [InlineData(false, "Hay–Davies")]
    [InlineData(true, "Isotropic")]
    public void PanelFieldsStartSelectedAndWeatherShowsOriginalGhiAvailability(bool isotropic, string model) => Sta(() =>
    {
        var dialog = new ExportDataWindow(isotropic, hasSourceGhi: isotropic);
        try
        {
            var fields = Descendants(dialog).OfType<CheckBox>().ToArray();
            Assert.Equal(9, fields.Length);
            Assert.Equal(5, fields.Count(c => c.IsChecked == true));
            Assert.Contains(model, Find<CheckBox>(dialog, "ExportPanelDiffuse").Content.ToString());
            Assert.Contains("zero", AutomationProperties.GetHelpText(Find<CheckBox>(dialog, "ExportDiffuseFactor")));
            var ghi = Find<CheckBox>(dialog, "ExportGlobalHorizontal");
            Assert.False(ghi.IsChecked); Assert.Equal(isotropic, ghi.IsEnabled);
            Assert.Equal(AutomationProperties.GetHelpText(ghi), ghi.ToolTip);
            Assert.Contains("by source interval start", AutomationProperties.GetHelpText(Find<CheckBox>(dialog, "ExportSourceIntervalBounds")));
            Assert.False(Find<Button>(dialog, "ConfirmExportData").IsEnabled);
            Assert.Null(dialog.SelectedOptions); Assert.Null(dialog.DestinationDirectory);
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void ExportRequiresASelectionAndAnAbsoluteExistingFolder() => Sta(() =>
    {
        string folder = NewFolder(); var dialog = new ExportDataWindow(false);
        try
        {
            var destination = Find<TextBox>(dialog, "ExportDestination");
            var export = Find<Button>(dialog, "ConfirmExportData");
            destination.Text = "."; Assert.False(export.IsEnabled);
            destination.Text = Path.Combine(folder, "missing"); Assert.False(export.IsEnabled);
            string file = Path.Combine(folder, "file.txt"); File.WriteAllText(file, "not a folder");
            destination.Text = file; Assert.False(export.IsEnabled);
            destination.Text = folder; Assert.True(export.IsEnabled);
            foreach (var field in Descendants(dialog).OfType<CheckBox>()) field.IsChecked = false;
            Find<CheckBox>(dialog, "ExportSourceIntervalBounds").IsChecked = true;
            Assert.False(export.IsEnabled);
            Assert.Contains("Select at least one", Find<TextBlock>(dialog, "ExportValidation").Text);
            Find<CheckBox>(dialog, "ExportDirectHorizontal").IsChecked = true; Assert.True(export.IsEnabled);
            destination.Text = " "; Assert.False(export.IsEnabled);
            Assert.Null(dialog.SelectedOptions); Assert.Null(dialog.DestinationDirectory);
            Assert.Single(Directory.GetFileSystemEntries(folder));
        }
        finally { dialog.Close(); Directory.Delete(folder, true); }
        return Task.CompletedTask;
    });

    [Fact]
    public void BrowsingUsesTheChosenFolderAndCancellingThePickerKeepsIt() => Sta(() =>
    {
        string folder = NewFolder(); int calls = 0;
        var dialog = new ExportDataWindow(false, folderPicker: pickerOwner =>
        {
            Assert.IsType<ExportDataWindow>(pickerOwner);
            return ++calls == 1 ? folder : null;
        });
        try
        {
            var browse = Find<Button>(dialog, "BrowseExportFolder");
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(folder, Find<TextBox>(dialog, "ExportDestination").Text);
            Assert.True(Find<Button>(dialog, "ConfirmExportData").IsEnabled);
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, calls); Assert.Equal(folder, Find<TextBox>(dialog, "ExportDestination").Text);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally { dialog.Close(); Directory.Delete(folder, true); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModalOnlyReturnsASelectionWhenExportIsConfirmedAndNeverWritesFiles(bool confirm) => Sta(() =>
    {
        string folder = NewFolder(); var dialog = new ExportDataWindow(true, hasSourceGhi: true) { ShowActivated = false };
        try
        {
            Find<TextBox>(dialog, "ExportDestination").Text = folder;
            foreach (var field in Descendants(dialog).OfType<CheckBox>()) field.IsChecked = false;
            Find<CheckBox>(dialog, "ExportGlobalHorizontal").IsChecked = true;
            Find<CheckBox>(dialog, "ExportSourceIntervalBounds").IsChecked = true;
            dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                var button = confirm ? Find<Button>(dialog, "ConfirmExportData") :
                    Assert.Single(Descendants(dialog).OfType<Button>(), b => b.IsCancel);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(confirm, dialog.ShowDialog());
            if (confirm)
            {
                Assert.True(dialog.SelectedOptions?.Irradiance);
                Assert.Equal(IrradianceFields.GlobalHorizontal, dialog.SelectedOptions!.Fields);
                Assert.True(dialog.SelectedOptions.SourceIntervalBounds);
                Assert.Equal(Path.GetFullPath(folder), dialog.DestinationDirectory);
            }
            else
            {
                Assert.Null(dialog.SelectedOptions); Assert.Null(dialog.DestinationDirectory);
            }
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally { dialog.Close(); Directory.Delete(folder, true); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkspaceExportButtonOpensSelectionAndOnlyConfirmedExportWritesWorkbook(bool confirm) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string destination = Path.Combine(fixture.Root, "exports"); Directory.CreateDirectory(destination);
        var owner = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service) { ShowActivated = false };
        var modalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        Exception? modalFailure = null; bool handled = false;
        try
        {
            var rendered = new TaskCompletionSource();
            owner.ContentRendered += (_, _) => rendered.TrySetResult();
            owner.Show(); await rendered.Task;
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            await owner.RecheckSourcesAsync();
            Assert.True(owner.ExportEnabledForTest);
            modalTimer.Tick += (_, _) =>
            {
                var dialog = owner.OwnedWindows.OfType<ExportDataWindow>().SingleOrDefault();
                if (dialog?.IsLoaded != true || !dialog.IsVisible) return;
                modalTimer.Stop();
                try
                {
                    Find<TextBox>(dialog, "ExportDestination").Text = destination;
                    Assert.True(Find<CheckBox>(dialog, "ExportShadedTotal").IsChecked);
                    Assert.False(Find<CheckBox>(dialog, "ExportGlobalHorizontal").IsEnabled);
                    foreach (var field in Descendants(dialog).OfType<CheckBox>()) field.IsChecked = false;
                    Find<CheckBox>(dialog, "ExportDirectHorizontal").IsChecked = true;
                    SaveDialogPreview(dialog, confirm);
                    var button = confirm ? Find<Button>(dialog, "ConfirmExportData") :
                        Assert.Single(Descendants(dialog).OfType<Button>(), b => b.IsCancel);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { modalFailure = ex; dialog.Close(); }
                finally { handled = true; }
            };
            modalTimer.Start();
            var action = Assert.Single(((Panel)owner.Irradiance.Actions).Children.OfType<Button>(), b => Equals(b.Content, "Export data"));
            Assert.True(action.IsEnabled, $"Export unavailable before click: {owner.UpdateStateForTest}.");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try { await Until(() => handled); }
            catch (Xunit.Sdk.XunitException)
            {
                string messages = string.Join(" | ", Descendants(owner.Irradiance).OfType<TextBlock>().Select(t => t.Text));
                throw new Xunit.Sdk.XunitException($"Export modal did not open. State: {owner.UpdateStateForTest}; enabled: {action.IsEnabled}; owned windows: {owner.OwnedWindows.Count}. {messages}");
            }
            if (modalFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(modalFailure).Throw();
            Assert.Empty(owner.OwnedWindows.OfType<ExportDataWindow>());
            if (confirm)
            {
                await Until(() => Directory.GetFiles(destination, "irradiance.xlsx", SearchOption.AllDirectories).Length == 1 && owner.ExportEnabledForTest);
                string workbook = Assert.Single(Directory.GetFiles(destination, "irradiance.xlsx", SearchOption.AllDirectories));
                Assert.True(new FileInfo(workbook).Length > 0);
                using var book = System.IO.Compression.ZipFile.OpenRead(workbook);
                using var values = book.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var sheet = System.Xml.Linq.XDocument.Load(values);
                var headers = sheet.Descendants().First(e => e.Name.LocalName == "row").Descendants()
                    .Where(e => e.Name.LocalName == "t").Select(e => e.Value).ToArray();
                Assert.Equal(new[] { "Timeseries", "Direct horizontal (W/m²)" }, headers);
                Assert.Single(Directory.GetDirectories(destination));
                Assert.Contains(Descendants(owner.Irradiance).OfType<TextBlock>(), t => t.Text == "Exported irradiance.xlsx");
            }
            else Assert.Empty(Directory.GetFileSystemEntries(destination));
        }
        finally
        {
            modalTimer.Stop(); owner.Close(); await owner.InputsSettled;
        }
    });

    private static void SaveDialogPreview(ExportDataWindow dialog, bool confirm)
    {
        string? folder = Environment.GetEnvironmentVariable("SOLARSHADE_EXPORT_UI_QA");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var content = (FrameworkElement)dialog.Content; content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(dialog.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, confirm ? "export-data-confirm.png" : "export-data-cancel.png"));
        encoder.Save(file);
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "solar-export-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Assert.Single(Descendants(root).OfType<T>(), element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
