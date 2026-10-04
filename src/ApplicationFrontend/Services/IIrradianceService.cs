using SolarShade.Camera;
using SolarShade.Shading;

namespace SolarShade.Desktop;

/// <summary>The existing upstream pipeline, independent of the tab hosting its controls.</summary>
public interface IIrradianceService : IDisposable
{
    int CardinalGenerationCount { get; }
    UserSettings PrepareInputs(UserSettings settings);
    ArtifactGroup ObserveInputs(UserSettings settings, IEnumerable<string>? invalidFields = null,
        bool refreshSources = false, bool verifyArtifacts = false, bool refreshWeather = false);
    bool IsCurrent(Evaluation evaluation);
    Evaluation? TryRestoreAccepted(UserSettings settings) => null;
    DependentResultStore CreateDependentStore(string folder, string software, params string[] requiredFiles);
    Task<Evaluation> Evaluate(UserSettings settings, bool refresh, Action<string> progress, Action<MaskAsset>? onMask,
        CancellationToken ct, Action<SunPathOverlayResult?>? onSunPath = null, Action<CardinalDirectionOverlayResult?>? onCardinals = null);
    Task<(CalibrationProfile Profile, string Path, string Details)> Calibrate(UserSettings settings);
}
