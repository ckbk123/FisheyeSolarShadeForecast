using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        Title = "Export data"; Width = 560; Height = 415; MinWidth = 360; MinHeight = 300;
        Background = StudioTheme.Surface;
        if (owner != null) WindowWorkArea.FitToOwner(this, owner);
        else { StudioTheme.Apply(this); WindowWorkArea.FitOnStartup(this); }
        chooseFolder = folderPicker ?? PickFolder;

        var panel = new StackPanel { Margin = new(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var title = IrradianceWorkspace.Label("Export results", 21, StudioTheme.Ink);
        title.FontWeight = FontWeights.SemiBold; panel.Children.Add(title);
        AutomationProperties.SetAutomationId(irradiance, "ExportIrradiance");
        panel.Children.Add(irradiance);
        string diffuseModel = isotropic ? "Isotropic" : "Hay–Davies";
        panel.Children.Add(IrradianceWorkspace.Label($"Full study period · {diffuseModel} diffuse", 13));
        StudioTheme.Help(irradiance,
            $"The workbook has 6 columns. Timeseries, direct, diffuse ({diffuseModel}), shading factor direct, shading factor diffuse, and final total irradiance. " +
            "Times include UTC offsets. Direct and diffuse are unshaded panel irradiance in W/m². Factors give transmission from 0 to 1. " +
            "A factor is blank when its unshaded component is zero. The final total is shaded irradiance in W/m².");
        panel.Children.Add(new Separator { Margin = new(0, 16, 0, 12) });

        var folderLabel = new Label { Content = "Destination folder", Target = destination, Padding = new(0), FontSize = 13 };
        panel.Children.Add(folderLabel);
        AutomationProperties.SetName(destination, "Destination folder");
        StudioTheme.Help(destination, "Choose an existing folder. SolarShade creates a uniquely named subfolder there for irradiance.xlsx.");
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
        validation.Margin = new(0, 10, 0, 8);
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(validation, "ExportValidation");
        panel.Children.Add(validation);

        var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        commands.Children.Add(cancel);
        AutomationProperties.SetAutomationId(export, "ConfirmExportData");
        export.Background = StudioTheme.Blue; export.Foreground = Brushes.White;
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
