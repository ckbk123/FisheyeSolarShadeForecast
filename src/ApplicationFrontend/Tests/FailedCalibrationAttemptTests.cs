using System.IO;
using System.Windows.Interop;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class FailedCalibrationAttemptTests
{
    [Fact]
    public void MissingBoardFolderDoesNotLabelAnExistingProfileAsAFailedFit() => Sta(async () =>
    {
        using var fixture = new Fixture();
        AppData.SaveSettings(fixture.Settings with { CalibrationFolder = Path.Combine(fixture.Root, "missing-folder") });
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            await window.Irradiance.Calibrate();
            Assert.Equal(UpdateState.Failed, window.UpdateStateForTest);
            Assert.StartsWith("Calibrated FOV", window.Irradiance.FieldOfViewTextForTest);
            Assert.Null(AppData.ReadSettings()!.LastCalibrationFailure);
        }
        finally { await window.InputsSettled; window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFitSupersedesOldProfileAcrossRestartAndCanRecover(bool retry) => Sta(async () =>
    {
        using var fixture = new Fixture();
        string boards = Path.Combine(fixture.Root, "boards");
        Directory.CreateDirectory(boards);
        File.Copy(fixture.Settings.SkyImage, Path.Combine(boards, "blank.png"));
        AppData.SaveSettings(fixture.Settings with { CalibrationFolder = boards });

        using (var service = new AppServices(fixture.Debug))
        {
            var first = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
            try
            {
                _ = new WindowInteropHelper(first).EnsureHandle();
                first.Calculate();
                await Until(() => !first.UpdatingForTest);
                Assert.True(first.ExportEnabledForTest);

                await first.Irradiance.Calibrate();
                await first.RecheckSourcesAsync();
                Assert.Equal("Calibration failed", first.Irradiance.FieldOfViewTextForTest);
                Assert.NotNull(first.Irradiance.CalibrationReviewForTest?.Report.Failure);
                Assert.False(first.ExportEnabledForTest);
                Assert.Null(first.Irradiance.CardinalsForTest);
                Assert.False(first.Irradiance.SunPathVisibleForTest);
                Assert.Empty(AppData.ReadSettings()!.ProfilePath);
                Assert.True(File.Exists(fixture.Settings.ProfilePath));
            }
            finally { await first.InputsSettled; first.Close(); }
        }

        using var reopenedService = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: reopenedService, loadExampleOnFirstRun: false);
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            Assert.Equal("Calibration failed", window.Irradiance.FieldOfViewTextForTest);
            Assert.NotNull(window.Irradiance.CalibrationReviewForTest?.Report.Failure);
            if (retry)
            {
                var example = PortablePaths.Example();
                foreach (string board in Directory.GetFiles(PortablePaths.Resolve(example.CalibrationFolder), "*.jpg"))
                    File.Copy(board, Path.Combine(boards, Path.GetFileName(board)));
                window.Irradiance.SelectSkyPhotoForTest(PortablePaths.Resolve(example.SkyImage));
                await window.InputsSettled;
                Assert.Equal("Calibration failed", window.Irradiance.FieldOfViewTextForTest);
                await window.Irradiance.Calibrate();
                Assert.Null(window.Irradiance.CalibrationReviewForTest!.Report.Failure);
            }
            else window.SetProfileForTest(fixture.Settings.ProfilePath);
            await window.InputsSettled;
            Assert.Null(AppData.ReadSettings()!.LastCalibrationFailure);
            Assert.StartsWith("Calibrated FOV", window.Irradiance.FieldOfViewTextForTest);
        }
        finally { await window.InputsSettled; window.Close(); }
    });
}
