using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SolarShade.Core.Models;
using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

/// <summary>On-demand mask experiment on an immutable photo, calibration and selected-mask snapshot.</summary>
internal sealed class MaskEdgeWindow : Window
{
    private readonly string photoPath;
    private readonly MaskAsset? studyMask;
    private readonly CalibrationResult calibration;
    private readonly SkyPhotoViewport preview = new();
    private readonly ComboBox threshold = new() { ItemsSource = new[] { 80, 90, 95, 100 }, SelectedItem = 100, MinWidth = 60 };
    private readonly RadioButton automatic = new() { Content = "Automatic", VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton manual = new() { Content = "Set apparent FOV", VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 12, 0) };
    private readonly Slider angleSlider = new() { SmallChange = .1, LargeChange = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox angleText = new() { Width = 70, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Grid angleControls = new() { Margin = new(2, 6, 2, 0) };
    private readonly StackPanel thresholdControls = new() { Orientation = Orientation.Horizontal };
    private readonly Button useStudyMask = new() { Content = "Use study mask", Visibility = Visibility.Collapsed };
    private readonly Button apply = new() { Content = "Use for calculation", IsEnabled = false, Margin = new Thickness(0, 0, 6, 0) };
    private bool inspectingStudyPhoto, studyEditable;
    private bool syncingAngle, hasManualInput;
    private readonly double? savedManual;
    private string automaticStatus = "", automaticDetail = "";
    private readonly CheckBox showMask = new() { Content = "Show mask", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock source = IrradianceWorkspace.Label("", 12);
    private readonly TextBlock status = IrradianceWorkspace.Label("", 15, StudioTheme.Ink);
    private readonly TextBlock explanation = IrradianceWorkspace.Label("", 12);
    private readonly TextBlock outerSky = IrradianceWorkspace.Label("", 12, StudioTheme.Ink);
    private readonly TextBlock legend = IrradianceWorkspace.Label("", 12);
    private string CalibratedLegend => $"Green · calibrated {2 * calibration.MaximumIncidentAngleDegrees:0.##}° full FOV";
    private CancellationTokenSource? pending;
    private MaskEdgeScan? scan;
    internal Task Analysis { get; private set; } = Task.CompletedTask;

    internal MaskEdgeWindow(Window owner, string photoPath, BitmapSource photo, MaskAsset? studyMask, SkyPhotoFieldOfView fov,
        int? currentThreshold, double? currentManual, Action<int?, double?> applyCoverage)
    {
        this.photoPath = photoPath;
        this.studyMask = studyMask;
        calibration = fov.EffectiveCalibration ?? throw new ArgumentException("A usable camera profile is required.", nameof(fov));
        savedManual = currentManual;
        threshold.SelectedItem = currentThreshold is 80 or 90 or 95 or 100 ? currentThreshold : 100;
        angleSlider.Maximum = CalibrationRadialMap.MaximumApparentFullDegrees(calibration);
        automatic.IsChecked = !currentManual.HasValue;
        manual.IsChecked = currentManual.HasValue;
        Title = "Estimate edge from mask";
        Width = 780; Height = 650;
        WindowWorkArea.FitToOwner(this, owner);
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var controls = new WrapPanel();
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        AutomationProperties.SetAutomationId(automatic, "AutomaticMaskEdgeMode");
        AutomationProperties.SetAutomationId(manual, "ManualApparentFovMode");
        controls.Children.Add(automatic); controls.Children.Add(manual);
        var label = new Label { Content = "Black pixels (%)", Target = threshold, VerticalAlignment = VerticalAlignment.Center };
        thresholdControls.Children.Add(label); thresholdControls.Children.Add(threshold);
        controls.Children.Add(thresholdControls);
        AutomationProperties.SetName(threshold, "Minimum black pixels (%)");
        StudioTheme.Help(threshold, "100% requires every sampled direction on every ring through the 180° full-angle cap to be observed black. Lower percentages allow gaps and give approximate bands.");
        threshold.SelectionChanged += (_, _) => ShowFinding();
        var fullFrame = new Button { Content = "Use full-frame mask…", Margin = new Thickness(8, 0, 4, 0) };
        fullFrame.IsEnabled = threshold.IsEnabled = calibration.MaximumIncidentAngleDegrees < 90;
        StudioTheme.Help(fullFrame, "Choose an original binary PNG with observations through the lens rim, before any cropping or disk overlay. A previously exported mask may already contain an artificial black border. Black means blocked and white means open sky. This file is used only here.");
        fullFrame.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Choose an original full-frame binary mask", Filter = "Binary PNG mask|*.png" };
            if (picker.ShowDialog(this) == true) SelectMask(picker.FileName);
        };
        controls.Children.Add(fullFrame);
        useStudyMask.Click += (_, _) => SelectMask(null);
        controls.Children.Add(useStudyMask);
        showMask.Margin = new Thickness(10, 0, 0, 0);
        showMask.Checked += (_, _) => preview.Photo.ShowOverlay = true;
        showMask.Unchecked += (_, _) => preview.Photo.ShowOverlay = false;
        controls.Children.Add(showMask);

        angleControls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        angleControls.ColumnDefinitions.Add(new());
        angleControls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var angleLabel = new Label { Content = "Full FOV (°)", Target = angleText, VerticalAlignment = VerticalAlignment.Center };
        angleSlider.Margin = new(6, 0, 12, 0);
        Grid.SetColumn(angleSlider, 1); Grid.SetColumn(angleText, 2);
        angleControls.Children.Add(angleLabel); angleControls.Children.Add(angleSlider); angleControls.Children.Add(angleText);
        DockPanel.SetDock(angleControls, Dock.Top); root.Children.Add(angleControls);
        AutomationProperties.SetName(angleText, "Apparent full field of view in degrees");
        AutomationProperties.SetName(angleSlider, "Apparent full field of view in degrees");
        AutomationProperties.SetAutomationId(angleSlider, "ApparentFovSlider");
        AutomationProperties.SetAutomationId(angleText, "ApparentFullFovDegrees");
        StudioTheme.Help(manual, "Fit the pink circle to the apparent rim on the photo or mask. The chosen full angle limits the directions used for shading.");
        angleSlider.ValueChanged += (_, _) =>
        {
            if (syncingAngle) return;
            hasManualInput = true;
            double full = angleSlider.Value == angleSlider.Maximum ? angleSlider.Maximum : Math.Round(angleSlider.Value, 1);
            SetAngle(Math.Min(full, angleSlider.Maximum));
            ShowFinding();
        };
        angleText.TextChanged += (_, _) =>
        {
            if (syncingAngle) return;
            hasManualInput = true;
            if (ReadAngle() is double full)
            {
                syncingAngle = true; angleSlider.Value = full; syncingAngle = false;
            }
            ShowFinding();
        };
        automatic.Checked += (_, _) => ShowFinding();
        manual.Checked += (_, _) => ShowFinding();

        var summary = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(summary, Dock.Bottom); root.Children.Add(summary);
        summary.Children.Add(status); summary.Children.Add(outerSky); summary.Children.Add(explanation);
        AutomationProperties.SetAutomationId(status, "MaskEdgeResult");
        AutomationProperties.SetAutomationId(outerSky, "MaskEdgeOuterSky");
        status.Focusable = outerSky.Focusable = true;
        StudioTheme.Help(outerSky, "White means the mask marks open sky on a sampled ring. Gaps can remain even at 95% black. An obstruction covering the solar disk blocks its direct beam. Diffuse light can still arrive from other directions.");
        legend.Margin = new Thickness(0, 3, 0, 0); summary.Children.Add(legend);
        StudioTheme.Help(legend, "The scan extends at most 90° from the lens axis (180° full angle). A band of dark obstructions can mimic the lens rim.");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0) };
        AutomationProperties.SetAutomationId(apply, "ApplyMaskEdge");
        StudioTheme.Help(apply, "Use this field of view with the selected study mask on the next Update results.");
        apply.Click += (_, _) =>
        {
            if (!apply.IsEnabled) return;
            if (manual.IsChecked == true) applyCoverage(null, ReadAngle());
            else applyCoverage((int)threshold.SelectedItem, null);
            if (IsVisible) Close();
        };
        var close = new Button { Content = "Close", MinWidth = 70 };
        close.Click += (_, _) => Close(); actions.Children.Add(apply); actions.Children.Add(close); summary.Children.Add(actions);

        source.Margin = new Thickness(2, 5, 2, 7);
        DockPanel.SetDock(source, Dock.Top); root.Children.Add(source);
        var pane = new Grid { Background = StudioTheme.Navy, ClipToBounds = true };
        pane.Children.Add(preview); pane.Children.Add(new ImageZoomTools(preview, "MaskEdge", "edge estimate"));
        root.Children.Add(pane);
        preview.SetPhoto(photo, calibration.ImageWidth, calibration.ImageHeight, PortablePaths.Resolve(photoPath));
        preview.SetFieldOfView(fov, showUsableOutline: false);
        preview.Photo.ShowOverlay = false;
        Loaded += (_, _) => SelectMask(null);
        Closed += (_, _) => pending?.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { Close(); e.Handled = true; } };
    }

    internal bool Matches(string path, MaskAsset? mask, CalibrationResult? profile) =>
        string.Equals(photoPath, path, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(studyMask?.Png, mask?.Png) &&
        profile != null && calibration.MaximumIncidentAngleDegrees == profile.MaximumIncidentAngleDegrees &&
        calibration.PrincipalPoint.AsSpan().SequenceEqual(profile.PrincipalPoint) &&
        calibration.IncidentAngleToRadiusPolynomial.AsSpan().SequenceEqual(profile.IncidentAngleToRadiusPolynomial);

    internal Task SelectMask(string? fullFrameFile)
    {
        pending?.Cancel();
        Analysis = AnalyzeAsync(fullFrameFile);
        return Analysis;
    }

    internal void SetStudyEditable(bool value)
    {
        studyEditable = value;
        RefreshApplyButton();
    }

    private void RefreshApplyButton() => apply.IsEnabled = studyEditable && inspectingStudyPhoto &&
        (manual.IsChecked == true ? ReadAngle().HasValue : AutomaticFinding?.EstimatedLimitFullDegrees.HasValue == true);

    private MaskEdgeFinding? AutomaticFinding => scan?.Findings.FirstOrDefault(f => Equals(f.BlackPercent, threshold.SelectedItem));

    private double? ReadAngle() => double.TryParse(angleText.Text.Trim().Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value / 2 > 0 && value <= angleSlider.Maximum ? value : null;

    private void SetAngle(double value)
    {
        syncingAngle = true;
        angleText.Text = value.ToString("G", CultureInfo.CurrentCulture);
        angleSlider.Value = Math.Clamp(value, 0, angleSlider.Maximum);
        syncingAngle = false;
    }

    private async Task AnalyzeAsync(string? fullFrameFile)
    {
        scan = null;
        inspectingStudyPhoto = fullFrameFile == null;
        hasManualInput = inspectingStudyPhoto && savedManual.HasValue;
        if (hasManualInput && savedManual is double saved) SetAngle(saved);
        preview.Photo.ClearOverlay();
        showMask.IsEnabled = false;
        outerSky.Text = "";
        automaticStatus = "Scanning concentric rings…";
        automaticDetail = "";
        useStudyMask.Content = studyMask == null ? "Use study photo" : "Use study mask";
        useStudyMask.Visibility = fullFrameFile != null ? Visibility.Visible : Visibility.Collapsed;
        source.Text = fullFrameFile == null ? "Source · selected study mask" : "Full-frame mask · " + Path.GetFileName(fullFrameFile);
        if (fullFrameFile == null && studyMask == null)
        {
            source.Text = "No study mask is available.";
            automaticStatus = "Choose a full-frame mask to try the ring scan.";
            ShowFinding();
            return;
        }
        using var request = new CancellationTokenSource();
        pending = request;
        var ct = request.Token;
        ShowFinding();
        try
        {
            var result = await Task.Run(async () =>
            {
                byte[] png = fullFrameFile == null ? studyMask!.Png : await File.ReadAllBytesAsync(fullFrameFile, ct);
                bool sameMask = fullFrameFile == null || (studyMask != null &&
                    MaskEdgeProbe.SameBinaryPixels(png, studyMask.Png, calibration.ImageWidth, calibration.ImageHeight, ct));
                bool fromAi = sameMask && studyMask!.Variant?.IsImported != true;
                var computed = MaskEdgeProbe.AnalyzeTo180(png, calibration.ImageWidth, calibration.ImageHeight,
                    calibration.PrincipalPoint[0], calibration.PrincipalPoint[1], calibration.IncidentAngleToRadiusPolynomial,
                    calibration.MaximumIncidentAngleDegrees,
                    sameMask ? studyMask!.Disk : null, fromAi ? studyMask!.Result.Crop : null,
                    ct, fromAi ? studyMask!.Result.InputSize : 0);
                ct.ThrowIfCancellationRequested();
                return (Scan: computed, Bitmap: IrradianceWorkspace.Bitmap(png), SameMask: sameMask);
            }, ct);
            if (ct.IsCancellationRequested) return;
            scan = result.Scan;
            outerSky.Text = DescribeOuterSky(scan);
            if (fullFrameFile != null && result.SameMask)
                source.Text = $"Matches study mask · {Path.GetFileName(fullFrameFile)} · study coverage applied";
            preview.Photo.SetOverlay(result.Bitmap);
            showMask.IsEnabled = true;
            ShowFinding();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ct.IsCancellationRequested) return;
            automaticStatus = "Could not analyze this mask.";
            automaticDetail = ex.Message;
            ShowFinding();
        }
        finally { if (ReferenceEquals(pending, request)) pending = null; }
    }

    private void ShowFinding()
    {
        bool adjust = manual.IsChecked == true;
        angleControls.Visibility = adjust ? Visibility.Visible : Visibility.Collapsed;
        thresholdControls.Visibility = adjust ? Visibility.Collapsed : Visibility.Visible;
        outerSky.Visibility = adjust ? Visibility.Collapsed : Visibility.Visible;
        var finding = AutomaticFinding;
        double measured = 2 * calibration.MaximumIncidentAngleDegrees;
        if (adjust && !hasManualInput) SetAngle(finding?.EstimatedLimitFullDegrees ?? measured);
        RefreshApplyButton();
        StudioTheme.Help(status, "");
        legend.Text = CalibratedLegend;
        if (adjust)
        {
            double? value = ReadAngle();
            double? chosenRadius = value is double angle ? SkyPhotoFieldOfView.RadiusForApparentFov(calibration, angle) : null;
            preview.SetMaskScan(calibration.PrincipalPoint[0], calibration.PrincipalPoint[1], null, null, chosenRadius);
            status.Text = value.HasValue ? "" : $"Enter a full angle above 0° and up to {angleSlider.Maximum:G}°.";
            explanation.Text = "";
            if (value.HasValue) legend.Text += "     Pink · apparent FOV";
            return;
        }
        if (scan == null || finding == null || calibration.MaximumIncidentAngleDegrees >= 90)
        {
            preview.SetMaskScan(0, 0, null, null);
            status.Text = calibration.MaximumIncidentAngleDegrees >= 90 ? "Calibration already covers the 180° scan cap." : automaticStatus;
            explanation.Text = calibration.MaximumIncidentAngleDegrees >= 90 ? $"The calibrated full FOV is {measured:0.##}°." : automaticDetail;
            return;
        }
        int percent = finding.BlackPercent;
        double? radius = finding.RadiusPixels;
        preview.SetMaskScan(calibration.PrincipalPoint[0], calibration.PrincipalPoint[1], radius, scan.LimitRadiusPixels);
        legend.Text = CalibratedLegend + (scan.LimitRadiusPixels.HasValue ? "     Blue · 180° scan cap" : "") +
            (radius.HasValue ? "     Amber · dark band starts" : "");
        if (radius is double r && finding.EstimatedLimitFullDegrees is double full)
            status.Text = (percent == 100 ? "All-black band" : $"Approximate {percent}%-black band") +
                $" at {r:0.#} px radius · {full:0.#}° extrapolated span";
        else status.Text = finding.EstimatedLimitFullDegrees == 180 ? "180° scan cap · no smaller band confirmed." : "Angular scan unavailable.";
        explanation.Text = finding.Explanation;
        string help = "The extrapolated span is twice the incident angle to the mask transition, using the slope at the calibrated limit. An obstruction can also produce a dark ring.";
        if (scan.Samples.Count > 0)
        {
            var first = scan.Samples[0];
            double total = MaskEdgeProbe.SamplesPerRing;
            help += $" At the calibrated limit, {first.ObservedBlackSamples / total:P0} of the circle is observed black and {first.UnknownSamples / total:P0} is outside known mask coverage.";
            if (scan.Reached180)
            {
                var last = scan.Samples[^1];
                int open = MaskEdgeProbe.SamplesPerRing - last.ObservedBlackSamples - last.UnknownSamples;
                help += $" At 180° there are {open:N0} open, {last.ObservedBlackSamples:N0} blocked and {last.UnknownSamples:N0} unknown samples.";
            }
        }
        StudioTheme.Help(status, help);
    }

    private static string DescribeOuterSky(MaskEdgeScan result)
    {
        if (!result.LimitRadiusPixels.HasValue) return "";
        if (result.Samples.Count < 2) return "The photo ends before a complete outer ring can be sampled.";
        if (result.FarthestObservedSkyRadiusPixels is double r)
            return $"Open sky sampled beyond calibration · farthest sample at {r:0.#} px radius.";
        if (!result.HasObservedOuterSamples) return "No observed mask coverage beyond calibration.";
        return result.HasUnknownOuterSamples || !result.Reached180 ? "No open sky in sampled coverage. Other directions are unknown." :
            "All sampled directions beyond calibration are blocked.";
    }
}
