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
    public void IrradianceStartsSelectedAndDescribesTheSavedDiffuseModel(bool isotropic, string model) => Sta(() =>
    {
        var dialog = new ExportDataWindow(isotropic);
        try
        {
            Assert.True(Find<CheckBox>(dialog, "ExportIrradiance").IsChecked);
            Assert.False(Find<Button>(dialog, "ConfirmExportData").IsEnabled);
            var description = Assert.Single(Descendants(dialog).OfType<TextBlock>(), t => t.Text.Contains("6 columns"));
            Assert.Contains($"diffuse ({model})", description.Text);
            Assert.Contains("Timeseries, direct", description.Text);
            Assert.Contains("shading factor direct, shading factor diffuse, and final total irradiance", description.Text);
            Assert.Contains(Descendants(dialog).OfType<TextBlock>(), t => t.Text.Contains("W/m²") && t.Text.Contains("0 to 1"));
            Assert.Contains(Descendants(dialog).OfType<TextBlock>(), t => t.Text.Contains("zero") && t.Text.Contains("blank"));
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
            var irradiance = Find<CheckBox>(dialog, "ExportIrradiance");
            destination.Text = "."; Assert.False(export.IsEnabled);
            destination.Text = Path.Combine(folder, "missing"); Assert.False(export.IsEnabled);
            string file = Path.Combine(folder, "file.txt"); File.WriteAllText(file, "not a folder");
            destination.Text = file; Assert.False(export.IsEnabled);
            destination.Text = folder; Assert.True(export.IsEnabled);
            irradiance.IsChecked = false; Assert.False(export.IsEnabled);
            Assert.Contains("Select at least one", Find<TextBlock>(dialog, "ExportValidation").Text);
            irradiance.IsChecked = true; Assert.True(export.IsEnabled);
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
        string folder = NewFolder(); var dialog = new ExportDataWindow(true) { ShowActivated = false };
        try
        {
            Find<TextBox>(dialog, "ExportDestination").Text = folder;
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
                    Assert.True(Find<CheckBox>(dialog, "ExportIrradiance").IsChecked);
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
