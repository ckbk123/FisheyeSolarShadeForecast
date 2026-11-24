using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private const string HeadingCompass = "N 0°  ·  E 90°  ·  S 180°  ·  W 270°";
    private readonly Dictionary<string, Slider> angleSliders = new();
    private readonly TextBlock phonePoseInfo = Label("", 12);
    private readonly TextBlock panelPoseInfo = Label("", 11);
    private Button useUphillPlacement = null!;
    private Button placePhoneOnPanel = null!;
    private UserSettings? phoneTiltEditOrigin;

    private void AddPhoneControls(Panel parent)
    {
        Section(parent, "Camera pose");
        parent.Children.Add(Label(HeadingCompass, 11));
        AddAngleField(parent, "Phone tilt from horizontal, °", nameof(UserSettings.PhoneInclination),
            "Raise the lens end uphill. At 0° the phone is flat with the lens pointing up. At 90° the lens points toward the horizon.");
        AddAngleField(parent, "Phone top heading, ° clockwise from true north", nameof(UserSettings.PhoneTopHeading),
            "Lay the phone flat with the lens up. Measure the compass direction from the phone’s center toward its lens end. Keep this heading as you raise that end, even to vertical. A flat phone with its image top north shows east on the left.");
        StudioTheme.Help(phonePoseInfo, "The horizontal projection of the outward lens axis gives its azimuth. South is 0°, west +90°, north ±180° and east −90°. With the lens pointing straight up its azimuth is undefined.");
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
        StudioTheme.Help(placePhoneOnPanel, "Copy the panel’s tilt and top heading to the phone once. Place the phone lens-out with its lens end toward the marked panel top. The tops also align when both devices are flat.");
        parent.Children.Add(placePhoneOnPanel);
    }

    private void AddPanelControls(Panel parent)
    {
        Section(parent, "Panel installation");
        parent.Children.Add(Label(HeadingCompass, 11));
        AddAngleField(parent, "Panel tilt from horizontal, °", nameof(UserSettings.PanelTilt),
            "Measure the angle between the panel surface and horizontal. Enter 0° for a flat panel or 90° for a vertical panel.", 0, 90);
        AddAngleField(parent, "Panel top heading, ° clockwise from true north", nameof(UserSettings.PanelTopHeading),
            "With the solar-cell face up and the panel flat, measure from the center toward the marked top edge. Raise that edge for tilt and keep the same heading all the way to vertical. A south-facing tilted panel has a north top heading (0°).", 0, 360);
        StudioTheme.Help(panelPoseInfo, "The horizontal projection of the outward solar-cell normal gives its azimuth. South is 0°, west +90°, north ±180° and east −90°. On a flat panel the front points straight up and its azimuth is undefined.");
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
                phonePoseInfo.Text = $"{lens}\nSaved pose · {pose.PhonePlacement}. Its original orientation is in use. {reset}";
            }
            else
                phonePoseInfo.Text = settings.CameraRoll != 0 && pose.ImageTopBearing is { } top
                    ? $"{lens}\nProjected image top {top:0.##}° from true north · saved roll {settings.CameraRoll:0.##}°"
                    : lens;
            panelPoseInfo.Text = validPanelBearing
                ? AzimuthReadout("Panel front azimuth", pose.PanelAzimuth, pose.PanelTilt == 0)
                : "Enter the panel top heading.";
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
