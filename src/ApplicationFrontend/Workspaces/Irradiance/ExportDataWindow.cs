using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SolarShade.Desktop;

/// <summary>Select deliverables and an existing destination before writing any export files.</summary>
public sealed class ExportDataWindow : Window
{
    private readonly CheckBox irradiance = new() { Content = "Irradiance (.xlsx)", IsChecked = true, Margin = new(0, 12, 0, 8) };
    private readonly TextBox destination = new() { Padding = new(8, 6, 8, 6), Margin = new(0, 4, 0, 6) };
    private readonly TextBlock validation = IrradianceWorkspace.Label("", 12);
    private readonly Button export = new() { Content = "Export data", IsDefault = true, IsEnabled = false };
    private readonly Func<Window, string?> chooseFolder;
    private string? validatedDirectory;

    public DataExportOptions? SelectedOptions { get; private set; }
    public string? DestinationDirectory { get; private set; }

    public ExportDataWindow(bool isotropic, Window? owner = null, Func<Window, string?>? folderPicker = null)
    {
        Title = "Export data"; Width = 620; Height = 620; MinWidth = 360; MinHeight = 300;
        Background = Brushes.White;
        if (owner != null) WindowWorkArea.FitToOwner(this, owner);
        else WindowWorkArea.FitOnStartup(this);
        chooseFolder = folderPicker ?? PickFolder;

        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        buttonStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 4, 8, 4)));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(191, 209, 214))));
        buttonStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        Resources.Add(typeof(Button), buttonStyle);

        var panel = new StackPanel { Margin = new(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(IrradianceWorkspace.Label("Choose data to export", 20));
        panel.Children.Add(IrradianceWorkspace.Label("Export the current complete result for the full selected period.", 13));
        AutomationProperties.SetAutomationId(irradiance, "ExportIrradiance");
        panel.Children.Add(irradiance);
        string diffuseModel = isotropic ? "Isotropic" : "Hay–Davies";
        panel.Children.Add(IrradianceWorkspace.Label(
            $"irradiance.xlsx · 6 columns\nTimeseries, direct, diffuse ({diffuseModel}), shading factor direct, shading factor diffuse, and final total irradiance.", 13));
        panel.Children.Add(IrradianceWorkspace.Label(
            "Direct and diffuse: unshaded irradiance on the panel (W/m²). Factors: transmission from 0 to 1. Final total: shaded irradiance (W/m²). Timeseries includes UTC offsets.", 12));
        panel.Children.Add(IrradianceWorkspace.Label("When an unshaded component is zero, its shading factor is blank.", 12));
        panel.Children.Add(new Separator { Margin = new(0, 16, 0, 12) });

        var folderLabel = new Label { Content = "Destination folder", Target = destination, Padding = new(0), FontSize = 13 };
        panel.Children.Add(folderLabel);
        AutomationProperties.SetName(destination, "Destination folder");
        AutomationProperties.SetAutomationId(destination, "ExportDestination");
        panel.Children.Add(destination);
        var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(browse, "BrowseExportFolder");
        browse.Click += (_, _) =>
        {
            string? selected = chooseFolder(this);
            if (selected != null) destination.Text = selected;
        };
        panel.Children.Add(browse);
        panel.Children.Add(IrradianceWorkspace.Label("A new, uniquely named export subfolder will be created inside this folder.", 12));
        validation.Margin = new(0, 10, 0, 8);
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(validation, "ExportValidation");
        panel.Children.Add(validation);

        var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        commands.Children.Add(cancel);
        AutomationProperties.SetAutomationId(export, "ConfirmExportData");
        export.Background = new SolidColorBrush(Color.FromRgb(15, 125, 111)); export.Foreground = Brushes.White;
        export.Click += (_, _) =>
        {
            Validate();
            if (!export.IsEnabled) return;
            DestinationDirectory = validatedDirectory;
            SelectedOptions = new(Irradiance: irradiance.IsChecked == true);
            DialogResult = true;
        };
        commands.Children.Add(export); panel.Children.Add(commands);
        irradiance.Checked += (_, _) => Validate(); irradiance.Unchecked += (_, _) => Validate();
        destination.TextChanged += (_, _) => Validate();
        Validate();
    }

    private string? PickFolder(Window owner)
    {
        var picker = new OpenFolderDialog { Title = "Choose a destination for the exported data" };
        if (validatedDirectory != null) picker.InitialDirectory = validatedDirectory;
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
    }

    private void Validate()
    {
        export.IsEnabled = false; validatedDirectory = null;
        if (irradiance.IsChecked != true) { validation.Text = "Select at least one type of data to export."; return; }
        string path = destination.Text.Trim();
        if (string.IsNullOrEmpty(path)) { validation.Text = "Choose a destination folder."; return; }
        if (!Path.IsPathFullyQualified(path)) { validation.Text = "Enter a full folder path, or choose Browse."; return; }
        if (!Directory.Exists(path)) { validation.Text = "The destination folder does not exist or cannot be opened. Choose an existing folder."; return; }
        validatedDirectory = Path.GetFullPath(path);
        validation.Text = "Ready to export irradiance.xlsx."; export.IsEnabled = true;
    }
}
