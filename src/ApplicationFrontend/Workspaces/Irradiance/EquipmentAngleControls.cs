using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private const string EquipmentCompass = "S 0°  ·  W +90°  ·  N ±180°  ·  E −90°";
    private readonly Dictionary<string, Slider> angleSliders = new();
    private readonly StackPanel flatHeadingInputs = new();
    private readonly TextBlock phonePoseInfo = Label("", 12);
    private readonly TextBlock panelPoseInfo = Label("", 11);
    private Button useUphillPlacement = null!;
    private Button placePhoneOnPanel = null!;
    private UserSettings? phoneTiltEditOrigin;

    private void AddPhoneControls(Panel parent)
    {
        Section(parent, "Camera pose");
        parent.Children.Add(Label(EquipmentCompass, 11));
        AddAngleField(parent, "Phone tilt from horizontal, °", nameof(UserSettings.PhoneInclination),
            "Raise the lens end uphill. At 0° the phone is flat with the lens pointing up. At 90° the lens points toward the horizon.");
        AddAngleField(parent, "Lens azimuth, ° clockwise from south", nameof(UserSettings.PhoneFacingAzimuth),
            "Use the horizontal direction of the outward lens axis. Measure clockwise from geographic south. East can be entered as −90° or 270°.");
        AddAngleField(flatHeadingInputs, "Image-top bearing, ° clockwise from true north", nameof(UserSettings.FlatImageTopBearing),
            "With a flat phone, this bearing orients the photograph and its sun path. When the image top points north, east appears on the left.");
        parent.Children.Add(flatHeadingInputs);
        parent.Children.Add(phonePoseInfo);
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
        StudioTheme.Help(placePhoneOnPanel, "Copy the panel’s current tilt and front direction once. The lens faces outward and the lens end goes uphill. With a flat panel the photograph keeps its heading.");
        parent.Children.Add(placePhoneOnPanel);
    }

    private void AddPanelControls(Panel parent)
    {
        Section(parent, "Panel installation");
        parent.Children.Add(Label(EquipmentCompass, 11));
        AddAngleField(parent, "Panel tilt from horizontal, °", nameof(UserSettings.PanelTilt),
            "Measure the angle between the panel surface and horizontal. Enter 0° for a flat panel or 90° for a vertical panel.", 0, 90);
        AddAngleField(parent, "Panel front azimuth, ° clockwise from south", nameof(UserSettings.PanelFacingAzimuth),
            "Use the horizontal direction of the outward normal on the solar-cell face. A 28° south-facing panel has tilt 28° and front azimuth 0°. East can be entered as −90° or 270°.", -180, 180);
        parent.Children.Add(panelPoseInfo);
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
        or nameof(UserSettings.PhoneFacingAzimuth) or nameof(UserSettings.FlatImageTopBearing)
        or nameof(UserSettings.PanelTilt) or nameof(UserSettings.PanelFacingAzimuth);

    private void ApplyPhonePlacement()
    {
        phoneTiltEditOrigin = null;
        foreach (string name in new[] { nameof(UserSettings.PhoneInclination), nameof(UserSettings.PhoneFacingAzimuth), nameof(UserSettings.FlatImageTopBearing) })
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
            bool flat = pose.PhoneTilt == 0;
            Show(nameof(UserSettings.PhoneInclination), pose.PhoneTilt is >= 0 and <= 90 ? pose.PhoneTilt : null, pose.IsVincentPhonePose);
            Show(nameof(UserSettings.PhoneFacingAzimuth), pose.PhoneAzimuth, pose.IsVincentPhonePose && pose.PhoneAzimuth.HasValue);
            Show(nameof(UserSettings.FlatImageTopBearing), pose.ImageTopBearing, flat && pose.IsVincentPhonePose);
            Show(nameof(UserSettings.PanelTilt), pose.PanelTilt, true);
            Show(nameof(UserSettings.PanelFacingAzimuth), pose.PanelAzimuth, pose.PanelTilt > 0);
            flatHeadingInputs.Visibility = flat ? Visibility.Visible : Visibility.Collapsed;
            useUphillPlacement.Visibility = pose.IsVincentPhonePose ? Visibility.Collapsed : Visibility.Visible;
            placePhoneOnPanel.IsEnabled = settings.PanelTilt is >= 0 and <= 90 &&
                settings.PanelAzimuth is >= 0 and <= 360 && !invalidFields.Contains(nameof(UserSettings.PanelTilt)) &&
                !invalidFields.Contains(nameof(UserSettings.PanelFacingAzimuth));
            if (!pose.IsVincentPhonePose)
            {
                string reset = pose.PhoneTilt is > 90 and <= 180
                    ? $"Lens zenith {pose.PhoneTilt:G}° from vertical up. Use lens-end-uphill placement to bring the lens to 90° at the horizon."
                    : "Use lens-end-uphill placement to set the image top uphill.";
                if (pose.PhoneTilt == 180)
                    reset += $" At nadir the facing azimuth is undefined. The new horizon pose will face {EquipmentOrientation.FromNorth(settings.BottomAzimuth):0.##}° from south.";
                phonePoseInfo.Text = $"Saved pose · {pose.PhonePlacement}. Its original orientation is in use. {reset}";
            }
            else if (flat)
                phonePoseInfo.Text = "Flat phone · lens azimuth is undefined.";
            else
                phonePoseInfo.Text = settings.CameraRoll != 0 && pose.ImageTopBearing is { } top
                    ? $"Image top {top:0.##}° clockwise from true north · saved roll {settings.CameraRoll:0.##}°"
                    : "Lens end uphill";
            panelPoseInfo.Text = "Flat panel · front azimuth is undefined.";
            panelPoseInfo.Visibility = pose.PanelTilt == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                    // North occupies both ends of the signed slider. Keep the user's active end.
                    slider.Value = editing == nameof(UserSettings.PanelFacingAzimuth) && property == editing &&
                        numeric == 180 && box.Text == "-180" ? -180 : numeric;
                }
            }
        }
    }
}
