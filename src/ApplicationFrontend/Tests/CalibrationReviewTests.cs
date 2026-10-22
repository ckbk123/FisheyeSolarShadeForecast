using System.IO;
using System.Text.Json;
using OpenCvSharp;
using SolarShade.Calibration.Solver;
using SolarShade.Calibration.Validation;
using SolarShade.Core.Models;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class CalibrationReviewTests
{
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
