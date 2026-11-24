using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SolarShade.Desktop;

/// <summary>Select deliverables and an existing destination before writing any export files.</summary>
public sealed class ExportDataWindow : Window
{
    private readonly Dictionary<IrradianceFields, CheckBox> fields = new();
    private readonly TextBox destination = new() { Padding = new(8, 6, 8, 6), Margin = new(0, 4, 0, 6) };
    private readonly TextBlock validation = IrradianceWorkspace.Label("", 12);
    private readonly Button export = new() { Content = "Export data", IsDefault = true, IsEnabled = false };
    private readonly Func<Window, string?> chooseFolder;
    private string? validatedDirectory;

    public DataExportOptions? SelectedOptions { get; private set; }
    public string? DestinationDirectory { get; private set; }

    public ExportDataWindow(bool isotropic, Window? owner = null, Func<Window, string?>? folderPicker = null, bool hasSourceGhi = false)
    {
        Title = "Export data"; Width = 540; Height = 585; MinWidth = 360; MinHeight = 300;
        Background = StudioTheme.Surface;
        if (owner != null) WindowWorkArea.FitToOwner(this, owner);
        else { StudioTheme.Apply(this); WindowWorkArea.FitOnStartup(this); }
        chooseFolder = folderPicker ?? PickFolder;

        var panel = new StackPanel { Margin = new(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var title = IrradianceWorkspace.Label("Export irradiance.xlsx", 21, StudioTheme.Ink);
        title.FontWeight = FontWeights.SemiBold; panel.Children.Add(title);
        string diffuseModel = isotropic ? "Isotropic" : "Hay–Davies";
        panel.Children.Add(IrradianceWorkspace.Label("Each row includes the source timestamp and UTC offset.", 13));
        var columns = IrradianceExportColumn.ForModel(diffuseModel);
        AddFields(panel, "Panel results", columns.Where(c => IrradianceFields.Panel.HasFlag(c.Field)), true, hasSourceGhi);
        AddFields(panel, "Horizontal weather data", columns.Where(c => IrradianceFields.Weather.HasFlag(c.Field)), false, hasSourceGhi);
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
            SelectedOptions = new(Fields: SelectedFields());
            DialogResult = true;
        };
        commands.Children.Add(export); panel.Children.Add(commands);
        destination.TextChanged += (_, _) => Validate();
        Validate();
    }

    private void AddFields(StackPanel panel, string heading, IEnumerable<IrradianceExportColumn> columns, bool selected, bool hasSourceGhi)
    {
        var label = IrradianceWorkspace.Label(heading, 14, StudioTheme.Ink);
        label.FontWeight = FontWeights.SemiBold; label.Margin = new(0, 12, 0, 4); panel.Children.Add(label);
        foreach (var column in columns)
        {
            bool available = column.Field != IrradianceFields.GlobalHorizontal || hasSourceGhi;
            var check = new CheckBox { Content = available ? column.Header : column.Header + " · unavailable in this study",
                IsChecked = selected, IsEnabled = available, Margin = new(0, 2, 0, 2) };
            fields.Add(column.Field, check);
            AutomationProperties.SetAutomationId(check, "Export" + column.Field);
            StudioTheme.Help(check, available ? column.Help :
                "This saved weather input contains no original GHI. Its direct and diffuse horizontal components are available.");
            ToolTipService.SetShowOnDisabled(check, true);
            check.Checked += (_, _) => Validate(); check.Unchecked += (_, _) => Validate();
            panel.Children.Add(check);
        }
    }

    private IrradianceFields SelectedFields()
    {
        var selection = IrradianceFields.None;
        foreach (var (field, checkbox) in fields)
            if (checkbox.IsEnabled && checkbox.IsChecked == true) selection |= field;
        return selection;
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
        if (SelectedFields() == IrradianceFields.None) { validation.Text = "Select at least one data column."; return; }
        string path = destination.Text.Trim();
        if (string.IsNullOrEmpty(path)) { validation.Text = "Choose a destination folder."; return; }
        if (!Path.IsPathFullyQualified(path)) { validation.Text = "Enter a full folder path, or choose Browse."; return; }
        if (!Directory.Exists(path)) { validation.Text = "The destination folder does not exist or cannot be opened. Choose an existing folder."; return; }
        validatedDirectory = Path.GetFullPath(path);
        validation.Text = "Ready to export irradiance.xlsx."; export.IsEnabled = true;
    }
}
