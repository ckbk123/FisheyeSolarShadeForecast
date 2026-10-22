using System.IO;
using System.Text.Json;
using OpenCvSharp;
using SolarShade.Calibration.Solver;
using SolarShade.Calibration.Validation;
using SolarShade.Core.Models;
using SolarShade.Camera;
using Window = System.Windows.Window;
using WindowStartupLocation = System.Windows.WindowStartupLocation;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class CalibrationReviewTests
{
    private static string BoundReview(Fixture fixture)
    {
        string directory = Path.Combine(fixture.Root, "bound-review"); Directory.CreateDirectory(directory);
        CameraProfileFiles.Export(CameraProfileFiles.Load(fixture.Settings.ProfilePath), directory);
        using (var preview = new Mat(50, 50, MatType.CV_8UC1, Scalar.White))
            Cv2.ImWrite(Path.Combine(directory, "preview.png"), preview);
        var detections = new CalibrationDetectionReport(1, 6, 9,
            [new("board.png", "board.png", true, 54, "Detected 54 inner corners.", "preview.png")]);
        AppData.Write(Path.Combine(directory, "checkerboard-detections.json"), detections);
        CalibrationEvidenceFiles.Write(directory);
        return Path.Combine(directory, "camera-profile.json");
    }

    [Fact]
    public void ReviewWindowFitsItsOwnerBeforeEitherWindowIsShown() => Sta(() =>
    {
        using var fixture = new Fixture();
        string profile = BoundReview(fixture);
        var review = CalibrationReview.ForProfile(profile)!;
        var owner = new Window { Width = 760, Height = 540 };
        // WPF requires an owner HWND, but creating the handle does not show or activate the window.
        _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var window = new CalibrationReviewWindow(review, owner);
        try
        {
            Assert.Same(owner, window.Owner); Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.True(window.Width <= owner.Width); Assert.True(window.Height <= owner.Height);
            Assert.False(window.IsVisible); Assert.False(owner.IsVisible);
        }
        finally { window.Close(); owner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void SamePathProfileReplacementClearsItsPreviousReviewIncludingAfterSourceRecheck() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string profilePath = BoundReview(fixture); byte[] original = File.ReadAllBytes(profilePath);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.SetProfileForTest(profilePath); await window.InputsSettled;
            Assert.NotNull(window.Irradiance.CalibrationReviewForTest);
            var profile = CameraProfileFiles.Load(profilePath);
            AppData.Write(profilePath, profile with { Calibration = profile.Calibration with { PrincipalPoint = [105, 151] } });
            await window.RecheckSourcesAsync();
            Assert.Null(window.Irradiance.CalibrationReviewForTest);
            Assert.Null(CalibrationReview.ForProfile(profilePath)); // A fresh reader cannot attach the old evidence either.
            File.WriteAllBytes(profilePath, original); await window.RecheckSourcesAsync();
            Assert.NotNull(window.Irradiance.CalibrationReviewForTest);
            AppData.Write(profilePath, profile with { Calibration = profile.Calibration with { PrincipalPoint = [110, 151] } });
            window.SetProfileForTest(profilePath); await window.InputsSettled;
            Assert.Null(window.Irradiance.CalibrationReviewForTest); // Reloading the unchanged filename must verify content.
        }
        finally { await window.InputsSettled; window.Close(); }
    });

    [Fact]
    public void AnotherProfileInTheSameDirectoryCannotInheritItsSiblingReview() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string original = BoundReview(fixture), other = Path.Combine(Path.GetDirectoryName(original)!, "another-camera.json");
        var profile = CameraProfileFiles.Load(original);
        AppData.Write(other, profile with { Calibration = profile.Calibration with { PrincipalPoint = [110, 151] } });
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.SetProfileForTest(original); await window.InputsSettled;
            Assert.NotNull(window.Irradiance.CalibrationReviewForTest);
            window.SetProfileForTest(other); await window.InputsSettled;
            Assert.Null(window.Irradiance.CalibrationReviewForTest);
            Assert.NotNull(CalibrationReview.ForProfile(Path.Combine(Path.GetDirectoryName(original)!, "calibration.yml")));
        }
        finally { await window.InputsSettled; window.Close(); }
    });

    [Fact]
    public void HistoricalEvidenceIsAssociatedOnlyWithItsSavedFittedProjection()
    {
        using var fixture = new Fixture(); string profilePath = BoundReview(fixture);
        string directory = Path.GetDirectoryName(profilePath)!;
        File.Delete(Path.Combine(directory, CalibrationEvidenceFiles.FileName));
        var profile = CameraProfileFiles.Load(profilePath); var c = profile.Calibration;
        var reference = new CalibrationReferenceSolution(1, "Historical fixture", [c.ImageWidth, c.ImageHeight], 22,
            [6, 9], c.PrincipalPoint, c.IncidentAngleToRadiusPolynomial, [0, 1], []);
        AppData.Write(Path.Combine(directory, "result.json"), reference);
        Assert.NotNull(CalibrationReview.ForProfile(profilePath));
        AppData.Write(profilePath, profile with { Calibration = c with { PrincipalPoint = [110, 151] } });
        Assert.Null(CalibrationReview.ForProfile(profilePath));
    }

    [Fact]
    public void ModifiedEvidenceClearsTheReviewEvenWhenTheProfileDidNotChange() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string profile = BoundReview(fixture);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.SetProfileForTest(profile); await window.InputsSettled;
            Assert.NotNull(window.Irradiance.CalibrationReviewForTest);
            using (var changed = new Mat(50, 50, MatType.CV_8UC1, Scalar.Black))
                Cv2.ImWrite(Path.Combine(Path.GetDirectoryName(profile)!, "preview.png"), changed);
            await window.RecheckSourcesAsync();
            Assert.Null(window.Irradiance.CalibrationReviewForTest);
        }
        finally { await window.InputsSettled; window.Close(); }
    });

    private static string Photos(Fixture fixture, bool board = false)
    {
        string directory = Path.Combine(fixture.Root, "checkerboards"); Directory.CreateDirectory(directory);
        using (var blank = new Mat(520, 400, MatType.CV_8UC1, Scalar.White))
            Cv2.ImWrite(Path.Combine(directory, "blank.png"), blank);
        File.WriteAllText(Path.Combine(directory, "unreadable.jpg"), "not an image");
        if (board)
        {
            using var image = new Mat(520, 400, MatType.CV_8UC1, Scalar.White);
            for (int row = 0; row < 10; row++)
                for (int col = 0; col < 7; col++)
                    if ((row + col) % 2 == 0) Cv2.Rectangle(image, new Rect(60 + col * 40, 60 + row * 40, 40, 40), Scalar.Black, -1);
            Cv2.ImWrite(Path.Combine(directory, "board.png"), image);
        }
        return directory;
    }

    [Fact]
    public void FitFailureStillRetainsDetectedCornersAndRejectedPhotoDetails() => Sta(() =>
    {
        using var fixture = new Fixture();
        string photos = Photos(fixture, true), output = Path.Combine(fixture.Root, "review");
        Assert.Throws<ArgumentOutOfRangeException>(() => new OmniCalibCSharpPort().Calibrate(photos, output,
            new(DownsampleFactor: 2), new(PolynomialDegree: 1)));
        var review = CalibrationReview.TryLoad(output)!;
        Assert.Equal(3, review.Report.Images.Count); Assert.NotNull(review.Report.Failure);
        var board = Assert.Single(review.Report.Images, p => p.Detected);
        Assert.Equal(54, board.CornerCount);
        using (var marked = Cv2.ImDecode(review.Preview(board)!, ImreadModes.Color))
        {
            var channels = Cv2.Split(marked);
            try { using var difference = new Mat(); Cv2.Absdiff(channels[0], channels[1], difference); Assert.True(Cv2.CountNonZero(difference) > 0); }
            finally { foreach (var channel in channels) channel.Dispose(); }
        }
        Assert.Contains(review.Report.Images, p => p.Name == "blank.png" && p.Message.Contains("No complete"));
        Assert.Contains(review.Report.Images, p => p.Name == "unreadable.jpg" && p.Message.Contains("decoded") && p.PreviewFile == null);
        var window = new CalibrationReviewWindow(review);
        try
        {
            window.SelectPhoto(review.Report.Images.ToList().IndexOf(board));
            Assert.Same(board, window.SelectedPhoto); Assert.True(window.HasPreview);
            window.SelectPhoto(review.Report.Images.ToList().FindIndex(p => p.Name == "unreadable.jpg"));
            Assert.False(window.HasPreview);
        }
        finally { window.Close(); }
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".calibration-*", SearchOption.AllDirectories));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task FailedCalibrationReviewSurvivesTheNextUpdateWithoutPublishingAProfile()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var failure = await Assert.ThrowsAsync<CalibrationReviewException>(() => service.Calibrate(
            fixture.Settings with { CalibrationFolder = Photos(fixture) }));
        Assert.StartsWith(Path.Combine(AppData.Root, "CalibrationReviews"), failure.DiagnosticsDirectory);
        var review = CalibrationReview.TryLoad(failure.DiagnosticsDirectory)!;
        Assert.Equal(2, review.Report.Images.Count); Assert.All(review.Report.Images, p => Assert.False(p.Detected));
        Assert.False(File.Exists(Path.Combine(failure.DiagnosticsDirectory, "camera-profile.json")));
        await service.Evaluate(fixture.Settings, false, _ => { }, null, default);
        Assert.NotNull(CalibrationReview.TryLoad(failure.DiagnosticsDirectory));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".calibration-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SuccessfulCalibrationKeepsFeedbackBesideItsReusableProfile()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string photos = Photos(fixture);
        string[] sourcePhotos = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Example", "Calib Images"), "*.jpg");
        foreach (string path in sourcePhotos)
            File.Copy(path, Path.Combine(photos, Path.GetFileName(path)));
        var calibrated = await service.Calibrate(fixture.Settings with { CalibrationFolder = photos });
        var review = CalibrationReview.TryLoad(Path.GetDirectoryName(calibrated.Path)!)!;
        Assert.Null(review.Report.Failure); Assert.Equal(sourcePhotos.Length + 2, review.Report.Images.Count);
        Assert.Equal(calibrated.Profile.Calibration.UsedImageCount, review.Report.Images.Count(p => p.Detected));
        Assert.Equal(2, review.Report.Images.Count(p => !p.Detected));
        Assert.All(review.Report.Images.Where(p => p.Detected), p => Assert.NotNull(review.Preview(p)));
        Assert.True(File.Exists(Path.Combine(review.Directory, "calibration.yml")));
        Assert.Contains("RMSE", review.FitSummary);
        Sta(async () =>
        {
            var window = new MainWindow(applicationServices: service);
            try
            {
                window.SetProfileForTest(calibrated.Path);
                Assert.Equal(review.Directory, window.Irradiance.CalibrationReviewForTest!.Directory);
                string relocated = Path.Combine(fixture.Root, "relocated-profile.json"); File.Copy(calibrated.Path, relocated);
                window.SetProfileForTest(relocated);
                Assert.Equal(review.Directory, window.Irradiance.CalibrationReviewForTest!.Directory);
                window.SetProfileForTest(fixture.Settings.ProfilePath);
                Assert.Null(window.Irradiance.CalibrationReviewForTest);
            }
            finally { await window.InputsSettled; window.Close(); }
        });
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".calibration-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExistingSavedProfilesRetainHistoricalOverlaysAndPickerExplainsCameraFormats()
    {
        using var fixture = new Fixture();
        string diagnostics = Path.Combine(fixture.Root, "older-profile"); Directory.CreateDirectory(Path.Combine(diagnostics, "debug"));
        var observations = new CalibrationObservationDocument(1, "xy_zero_based_after_exif_orientation", [400, 520], [6, 9], 22,
            new("fixture", 2, .01, "fixture"), [new("board.png", "board.png", "fixture", [[40, 40]])], [[0, 0, 0]]);
        File.WriteAllText(Path.Combine(diagnostics, "observations.json"), JsonSerializer.Serialize(observations));
        using (var image = new Mat(520, 400, MatType.CV_8UC1, Scalar.White)) Cv2.ImWrite(Path.Combine(diagnostics, "debug", "000-board.jpg"), image);
        var review = CalibrationReview.TryLoad(diagnostics)!;
        Assert.NotNull(review.Note); Assert.Contains("unavailable", review.Summary);
        Assert.NotNull(review.Preview(Assert.Single(review.Report.Images)));
        var picker = IrradianceWorkspace.CameraProfilePicker();
        Assert.Contains("camera parameters", picker.Title); Assert.Contains("*.json;*.yml;*.yaml", picker.Filter);
    }
}
