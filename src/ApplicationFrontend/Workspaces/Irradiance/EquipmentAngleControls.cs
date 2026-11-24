using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private const string PhoneHeadingHelp = "With the phone flat, measure clockwise from true north toward the lens end (0–360°). See Orientation map.";
    private const string PanelHeadingHelp = "With the panel flat and face up, measure clockwise from true north toward its top edge (0–360°). See Orientation map.";
    private readonly Dictionary<string, Slider> angleSliders = new();
    private Button useUphillPlacement = null!;
    private Button placePhoneOnPanel = null!;
    private UserSettings? phoneTiltEditOrigin;

    private void AddPhoneControls(Panel parent)
    {
        Section(parent, "Camera pose");
        AddAngleField(parent, "Phone tilt", nameof(UserSettings.PhoneInclination),
            "Raise the lens end from 0° (flat) to 90° (vertical). See Orientation map.");
        AddAngleField(parent, "Phone top heading", nameof(UserSettings.PhoneTopHeading), PhoneHeadingHelp);
        useUphillPlacement = Button("Use lens-end-uphill placement", () =>
        {
            EquipmentOrientation.UseUphillPlacement(settings);
            ApplyPhonePlacement();
        });
        parent.Children.Add(useUphillPlacement);
        placePhoneOnPanel = Button("Copy panel pose to phone", () =>
        {
            EquipmentOrientation.PlacePhoneOnPanel(settings);
            ApplyPhonePlacement();
        });
        StudioTheme.Help(placePhoneOnPanel, "Copy the panel’s current tilt and top heading to the phone.");
        parent.Children.Add(placePhoneOnPanel);
    }

    private void AddPanelControls(Panel parent)
    {
        Section(parent, "Panel installation");
        AddAngleField(parent, "Panel tilt", nameof(UserSettings.PanelTilt),
            "Raise the top edge from 0° (flat) to 90° (vertical). See Orientation map.", 0, 90);
        AddAngleField(parent, "Panel top heading", nameof(UserSettings.PanelTopHeading), PanelHeadingHelp, 0, 360);
    }

    private void AddAngleField(Panel parent, string label, string property, string help, double? minimum = null, double? maximum = null)
    {
        var row = new DockPanel();
        var number = MakeField(property);
        number.Width = 80;
        AutomationProperties.SetName(number, label);
        StudioTheme.Help(number, help);
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        var caption = Label(label, 12);
        StudioTheme.Help(caption, help);
        caption.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(caption);
        parent.Children.Add(row);
        if (minimum is not { } min || maximum is not { } max) return;
        var slider = new Slider {
            Minimum = min, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true,
            Margin = new(0, 0, 8, 7), Tag = property
        };
        AutomationProperties.SetName(slider, label);
        StudioTheme.Help(slider, help);
        angleSliders.Add(property, slider);
        slider.ValueChanged += (_, _) =>
        {
            if (loading) return;
            number.Text = slider.Value.ToString("G", CultureInfo.InvariantCulture);
            SaveInputs();
        };
        parent.Children.Add(slider);
    }

    private static bool IsEquipmentField(string property) => property is nameof(UserSettings.PhoneInclination)
        or nameof(UserSettings.PhoneTopHeading) or nameof(UserSettings.PanelTilt) or nameof(UserSettings.PanelTopHeading);

    private void ApplyPhonePlacement()
    {
        phoneTiltEditOrigin = null;
        foreach (string name in new[] { nameof(UserSettings.PhoneInclination), nameof(UserSettings.PhoneTopHeading) })
        {
            invalidFields.Remove(name);
            fields[name].ClearValue(Control.BorderBrushProperty);
        }
        RefreshEquipmentControls();
        Changed();
    }

    private void RefreshEquipmentControls(string? editing = null)
    {
        bool wasLoading = loading;
        loading = true;
        try
        {
            var pose = EquipmentOrientation.Describe(settings);
            bool validPanelBearing = settings.PanelAzimuth is >= 0 and <= 360;
            Show(nameof(UserSettings.PhoneInclination), pose.PhoneTilt is >= 0 and <= 90 ? pose.PhoneTilt : null, pose.IsVincentPhonePose);
            Show(nameof(UserSettings.PhoneTopHeading), pose.IsVincentPhonePose ? settings.PhoneTopHeading : null, pose.IsVincentPhonePose);
            Show(nameof(UserSettings.PanelTilt), pose.PanelTilt, true);
            Show(nameof(UserSettings.PanelTopHeading), validPanelBearing ? settings.PanelTopHeading : null, true);
            useUphillPlacement.Visibility = pose.IsVincentPhonePose ? Visibility.Collapsed : Visibility.Visible;
            placePhoneOnPanel.IsEnabled = settings.PanelTilt is >= 0 and <= 90 && validPanelBearing &&
                !invalidFields.Contains(nameof(UserSettings.PanelTilt)) && !invalidFields.Contains(nameof(UserSettings.PanelTopHeading));
            string lens = AzimuthReadout("Lens azimuth", pose.PhoneAzimuth, pose.PhoneTilt == 0);
            if (!pose.IsVincentPhonePose)
            {
                string reset = pose.PhoneTilt is > 90 and <= 180
                    ? $"Lens zenith {pose.PhoneTilt:G}° from vertical up. Use lens-end-uphill placement to bring the lens to 90° at the horizon."
                    : "Use lens-end-uphill placement to set the image top uphill.";
                if (pose.PhoneTilt == 180)
                    reset += $" At nadir the facing azimuth is undefined. The new horizon pose will face {EquipmentOrientation.FromNorth(settings.BottomAzimuth):0.##}° from south.";
                StudioTheme.Help(useUphillPlacement, $"Saved pose · {pose.PhonePlacement}. {reset}");
            }
            if (settings.CameraRoll != 0 && pose.ImageTopBearing is { } top)
                lens += $"\nProjected image top {top:0.##}° from true north · saved roll {settings.CameraRoll:0.##}°";
            StudioTheme.Help(fields[nameof(UserSettings.PhoneTopHeading)], $"{PhoneHeadingHelp}\n{lens}");
            string panelAzimuth = validPanelBearing
                ? AzimuthReadout("Panel front azimuth", pose.PanelAzimuth, pose.PanelTilt == 0)
                : "Enter the panel top heading.";
            string panelHelp = $"{PanelHeadingHelp}\n{panelAzimuth}";
            StudioTheme.Help(fields[nameof(UserSettings.PanelTopHeading)], panelHelp);
            StudioTheme.Help(angleSliders[nameof(UserSettings.PanelTopHeading)], panelHelp);
        }
        finally { loading = wasLoading; }

        void Show(string property, double? value, bool enabled)
        {
            var box = fields[property];
            box.IsEnabled = enabled;
            if (!enabled && invalidFields.Remove(property)) box.ClearValue(Control.BorderBrushProperty);
            if ((!enabled || property != editing) && !invalidFields.Contains(property))
                box.Text = value is { } n && double.IsFinite(n) ? n.ToString("G", CultureInfo.InvariantCulture) : "—";
            if (angleSliders.TryGetValue(property, out var slider))
            {
                slider.IsEnabled = enabled;
                if (value is { } numeric && double.IsFinite(numeric))
                {
                    // North occupies both ends of the heading slider. Keep the user's active end.
                    slider.Value = editing == nameof(UserSettings.PanelTopHeading) && property == editing &&
                        numeric == 0 && box.Text == "360" ? 360 : numeric;
                }
            }
        }
    }

    private static string AzimuthReadout(string name, double? value, bool flat) => value is { } azimuth
        ? $"{name} {azimuth:0.##}° clockwise from south"
        : flat ? $"{name} undefined · flat" : $"{name} undefined";
}
