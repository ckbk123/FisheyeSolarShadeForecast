using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SolarShade.Core.Models;

namespace SolarShade.Desktop;

/// <summary>Refines the single edge of the selected study photo in native image coordinates.</summary>
internal sealed class MaskEdgeWindow : Window
{
    private readonly string photoPath;
    private readonly MaskAsset? mask;
    private readonly CalibrationResult calibration;
    private readonly Task<AppliedMaskEdge?> proposal;
    private readonly Func<CancellationToken, Task<MaskAsset>> prepareMask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SkyPhotoViewport preview = new();
    private readonly Slider slider = new() { SmallChange = .1, LargeChange = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox degrees = new() { Width = 78, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock hint = IrradianceWorkspace.Label("", 12);
    private readonly Button reset = new() { Content = "Use suggestion", Visibility = Visibility.Collapsed };
    private readonly Button save = new() { Content = "Use edge for shading", MinWidth = 80, IsEnabled = false, Margin = new Thickness(0, 0, 6, 0) };
    private double? suggested;
    private bool editable, syncing, edited, loading = true;
    private string? failure;
    internal MaskAsset? PreparedMask { get; private set; }

    internal Task Analysis { get; private set; } = Task.CompletedTask;

    internal MaskEdgeWindow(Window owner, string photoPath, BitmapSource photo, MaskAsset? mask,
        SkyPhotoFieldOfView field, Task<AppliedMaskEdge?> proposal, Func<CancellationToken, Task<MaskAsset>> prepareMask,
        double? currentFullDegrees, Action<double?> saveEdge)
    {
        this.photoPath = photoPath;
        this.mask = mask;
        this.proposal = proposal; this.prepareMask = prepareMask;
        calibration = field.EffectiveCalibration ?? throw new ArgumentException("Choose a usable camera profile.", nameof(field));
        Title = "Refine edge"; Width = 740; Height = 640;
        WindowWorkArea.FitToOwner(this, owner);
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var input = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        input.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new()); input.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        input.Children.Add(new Label { Content = "Full FOV (°)", Target = degrees, VerticalAlignment = VerticalAlignment.Center });
        slider.Maximum = CalibrationRadialMap.MaximumApparentFullDegrees(calibration);
        slider.Margin = new(8, 0, 12, 0);
        Grid.SetColumn(slider, 1); input.Children.Add(slider);
        Grid.SetColumn(degrees, 2); input.Children.Add(degrees);
        AutomationProperties.SetName(slider, "Full field of view in degrees");
        AutomationProperties.SetName(degrees, "Full field of view in degrees");
        AutomationProperties.SetAutomationId(slider, "ApparentFovSlider");
        AutomationProperties.SetAutomationId(degrees, "ApparentFullFovDegrees");
        StudioTheme.Help(degrees, "Full angle across the lens. Align the green circle with the visible rim.");
        DockPanel.SetDock(input, Dock.Top); root.Children.Add(input);

        var controls = new DockPanel { Margin = new(2, 0, 0, 6) };
        var showMask = new CheckBox { Content = "Show mask", IsEnabled = mask != null, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(showMask, Dock.Right); controls.Children.Add(showMask);
        showMask.Checked += (_, _) => preview.Photo.ShowOverlay = true;
        showMask.Unchecked += (_, _) => preview.Photo.ShowOverlay = false;
        reset.Margin = new(8, 0, 0, 0); reset.Click += (_, _) =>
        {
            if (suggested is not double value) return;
            edited = false; SetAngle(value);
        };
        var suggestion = new StackPanel { Orientation = Orientation.Horizontal };
        hint.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(hint, "EdgeProposal");
        suggestion.Children.Add(hint); suggestion.Children.Add(reset); controls.Children.Add(suggestion);
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        AutomationProperties.SetAutomationId(save, "SaveRefinedEdge");
        StudioTheme.Help(save, "Save this photo’s field of view. Update results to recalculate shading with this edge.");
        save.Click += (_, _) =>
        {
            if (!save.IsEnabled) return;
            double? chosen = ReadAngle();
            saveEdge(edited ? chosen : null);
            Close();
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 70 }; cancel.Click += (_, _) => Close();
        actions.Children.Add(save); actions.Children.Add(cancel);

        var pane = new Grid { Background = StudioTheme.Navy, ClipToBounds = true };
        pane.Children.Add(preview); pane.Children.Add(new ImageZoomTools(preview, "MaskEdge", "edge"));
        root.Children.Add(pane);
        preview.SetPhoto(photo, calibration.ImageWidth, calibration.ImageHeight, PortablePaths.Resolve(photoPath));
        showMask.IsEnabled = false;
        preview.Photo.ShowOverlay = false;

        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            edited = true;
            SetAngle(slider.Value == slider.Maximum ? slider.Maximum : Math.Round(slider.Value, 1));
        };
        degrees.TextChanged += (_, _) =>
        {
            if (syncing) return;
            edited = true;
            if (ReadAngle() is double full)
            {
                syncing = true; slider.Value = full; syncing = false;
            }
            Refresh();
        };
        edited = currentFullDegrees.HasValue;
        SetAngle(currentFullDegrees ?? Math.Min(2 * calibration.MaximumIncidentAngleDegrees, slider.Maximum));
        Loaded += (_, _) => Analysis = ShowProposalAsync(showMask);
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal bool Matches(string path, MaskAsset? asset, CalibrationResult? profile) =>
        string.Equals(photoPath, path, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(mask?.Png, asset?.Png) &&
        profile != null && calibration.MaximumIncidentAngleDegrees == profile.MaximumIncidentAngleDegrees &&
        calibration.PrincipalPoint.AsSpan().SequenceEqual(profile.PrincipalPoint) &&
        calibration.IncidentAngleToRadiusPolynomial.AsSpan().SequenceEqual(profile.IncidentAngleToRadiusPolynomial);

    internal void SetStudyEditable(bool value) { editable = value; Refresh(); }

    private async Task ShowProposalAsync(CheckBox showMask)
    {
        var ct = lifetime.Token;
        try
        {
            var active = mask ?? await prepareMask(ct);
            ct.ThrowIfCancellationRequested();
            var bitmap = await Task.Run(() => IrradianceWorkspace.Bitmap(active.Png), ct);
            ct.ThrowIfCancellationRequested();
            preview.Photo.SetOverlay(bitmap); showMask.IsEnabled = true;
            var result = mask == null ? await Task.Run(() => active.SuggestEdge(calibration, ct), ct) : await proposal;
            ct.ThrowIfCancellationRequested();
            if (mask == null) PreparedMask = active;
            suggested = result?.EffectiveFullDegrees;
            if (result != null)
            {
                string help = "The suggestion uses a 95% black threshold on the study mask. " + result.Explanation;
                if (result.ObservedSkyAtCap is int sky && result.UnknownAtCap is int unknown)
                    help += $" At the 180° scan cap, {sky:N0} samples show sky and {unknown:N0} are unobserved.";
                StudioTheme.Help(hint, help);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex.Message; }
        finally
        {
            loading = false;
            if (!ct.IsCancellationRequested)
            {
                if (!edited && suggested is double full) SetAngle(full);
                else Refresh();
                if (failure != null) StudioTheme.Help(hint, failure);
            }
        }
    }

    private double? ReadAngle() => double.TryParse(degrees.Text.Trim().Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value / 2 > 0 && value <= slider.Maximum ? value : null;

    private void SetAngle(double value)
    {
        syncing = true;
        degrees.Text = value.ToString("G", CultureInfo.CurrentCulture);
        slider.Value = Math.Clamp(value, 0, slider.Maximum);
        syncing = false;
        Refresh();
    }

    private void Refresh()
    {
        double? angle = ReadAngle();
        double? radius = angle is double full ? SkyPhotoFieldOfView.RadiusForApparentFov(calibration, full) : null;
        preview.SetEdge(calibration.PrincipalPoint[0], calibration.PrincipalPoint[1], radius);
        save.IsEnabled = editable && angle.HasValue && (edited || suggested.HasValue);
        reset.Visibility = suggested.HasValue && edited ? Visibility.Visible : Visibility.Collapsed;
        hint.Text = !angle.HasValue ? $"Enter a full angle above 0° and up to {slider.Maximum:0.###}°." :
            suggested.HasValue ? $"Suggested {suggested:0.###}°" :
            loading ? "Finding the edge…" : "Mask suggestion unavailable. Set the edge on the photo.";
    }
}
