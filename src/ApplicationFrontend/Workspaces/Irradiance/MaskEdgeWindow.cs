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
    private readonly Button useStudyMask = new() { Content = "Use study mask", Visibility = Visibility.Collapsed };
    private readonly Button apply = new() { Content = "Use for calculation", IsEnabled = false, Margin = new Thickness(0, 0, 6, 0) };
    private bool usingStudyMask, studyEditable;
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
        int? currentThreshold, Action<int> applyThreshold)
    {
        this.photoPath = photoPath;
        this.studyMask = studyMask;
        calibration = fov.EffectiveCalibration ?? throw new ArgumentException("A usable camera profile is required.", nameof(fov));
        threshold.SelectedItem = currentThreshold is 80 or 90 or 95 or 100 ? currentThreshold : 100;
        Title = "Estimate edge from mask";
        Width = 780; Height = 650;
        WindowWorkArea.FitToOwner(this, owner);
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var controls = new WrapPanel();
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        var label = new Label { Content = "Black pixels (%)", Target = threshold, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(label);
        controls.Children.Add(threshold);
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
        StudioTheme.Help(apply, "Use this threshold with the selected study mask on the next Update results. To use a different file, first import it as the study mask.");
        apply.Click += (_, _) =>
        {
            if (apply.IsEnabled && threshold.SelectedItem is int percent) { applyThreshold(percent); if (IsVisible) Close(); }
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

    private void RefreshApplyButton() => apply.IsEnabled = studyEditable && usingStudyMask && threshold.SelectedItem is int percent &&
        scan?.Findings.Any(f => f.BlackPercent == percent && f.EstimatedLimitFullDegrees.HasValue) == true;

    private async Task AnalyzeAsync(string? fullFrameFile)
    {
        scan = null;
        usingStudyMask = fullFrameFile == null && studyMask != null;
        RefreshApplyButton();
        preview.SetMaskScan(0, 0, null, null);
        preview.Photo.ClearOverlay();
        showMask.IsEnabled = false;
        explanation.Text = "";
        outerSky.Text = "";
        legend.Text = CalibratedLegend;
        StudioTheme.Help(status, "");
        useStudyMask.Visibility = fullFrameFile != null && studyMask != null ? Visibility.Visible : Visibility.Collapsed;
        source.Text = fullFrameFile == null ? "Source · selected study mask" : "Full-frame mask · " + Path.GetFileName(fullFrameFile);
        if (calibration.MaximumIncidentAngleDegrees >= 90)
        {
            source.Text = "Source · camera calibration";
            status.Text = "Calibration already covers the 180° scan cap.";
            explanation.Text = $"The calibrated full FOV is {2 * calibration.MaximumIncidentAngleDegrees:0.##}°.";
            return;
        }
        if (fullFrameFile == null && studyMask == null)
        {
            source.Text = "No study mask is available.";
            status.Text = "Choose a full-frame mask to try the ring scan.";
            return;
        }
        using var request = new CancellationTokenSource();
        pending = request;
        var ct = request.Token;
        status.Text = "Scanning concentric rings…";
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
            status.Text = "Could not analyze this mask.";
            explanation.Text = ex.Message;
        }
        finally { if (ReferenceEquals(pending, request)) pending = null; }
    }

    private void ShowFinding()
    {
        if (scan == null || threshold.SelectedItem is not int percent) return;
        var finding = scan.Findings.Single(f => f.BlackPercent == percent);
        RefreshApplyButton();
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
                help += $" At 180°, {last.ObservedBlackSamples / total:P0} is observed black, " +
                    $"{(total - last.ObservedBlackSamples - last.UnknownSamples) / total:P0} is open sky and {last.UnknownSamples / total:P0} is unknown.";
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
