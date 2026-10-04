using SolarShade.PvBattery;

namespace SolarShade.Desktop;

internal static class PvAcceptedVault
{
    private const string Owner = "SolarShade.AcceptedPvResult.v1";
    private static readonly string[] Artifacts = ["battery-hourly.csv", "battery-result.json", "battery-hourly.xlsx"];
    private sealed record SavedPv(string Fingerprint, BatterySimulationRequest Input,
        IReadOnlyList<BatteryStep> Steps, IReadOnlyList<BatteryHour> Hours, BatterySummary Summary);
    private sealed record Manifest(string Owner, string SourceKey, string SettingsKey,
        string ResultSha256, Dictionary<string, string> Hashes);
    private static string Folder(IrradianceSnapshot source, string settingsKey) =>
        Path.Combine(AppData.PathFor("AcceptedResults"), AcceptedResultVault.Key(source.Settings), "Pv", settingsKey);

    internal static void Archive(IrradianceSnapshot source, string settingsKey,
        DependentResultStore store, BatterySimulationResult result)
    {
        if (!store.IsCurrent(source, settingsKey)) throw new InvalidOperationException("PV output is not current.");
        string target = Folder(source, settingsKey);
        if (Directory.Exists(target)) { Verify(target, source, settingsKey); return; }
        string parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            Directory.CreateDirectory(staging);
            var hashes = new Dictionary<string, string>();
            foreach (string file in Artifacts)
            {
                string origin = Path.Combine(store.DirectoryPath, file), destination = Path.Combine(staging, file);
                string hash = AppData.FileKey(origin); File.Copy(origin, destination);
                if (AppData.FileKey(destination) != hash) throw new IOException("PV archive copy failed verification.");
                hashes.Add(file, hash);
            }
            AppData.Write(Path.Combine(staging, "result.json"), new SavedPv(result.InputFingerprint, result.Input,
                result.Steps, result.Hours, result.Summary));
            AppData.Write(Path.Combine(staging, "vault.json"), new Manifest(Owner, AcceptedResultVault.Key(source.Settings),
                settingsKey, AppData.FileKey(Path.Combine(staging, "result.json")), hashes));
            Verify(staging, source, settingsKey);
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) DebugDataStore.DeleteTree(parent, staging); }
    }
    internal static BatterySimulationResult? Restore(IrradianceSnapshot source, string settingsKey, DependentResultStore store)
    {
        string folder = Folder(source, settingsKey);
        if (!Directory.Exists(folder)) return null;
        var saved = Verify(folder, source, settingsKey);
        store.Publish(source, settingsKey, destination =>
        {
            foreach (string file in Artifacts) File.Copy(Path.Combine(folder, file), Path.Combine(destination, file));
        });
        return BatterySimulationResult.Restore(saved.Fingerprint, saved.Input, saved.Steps, saved.Hours, saved.Summary);
    }
    private static SavedPv Verify(string folder, IrradianceSnapshot source, string settingsKey)
    {
        DebugDataStore.SafePath(folder);
        var manifest = AppData.Read<Manifest>(Path.Combine(folder, "vault.json"))
            ?? throw new InvalidDataException("Saved PV result metadata is missing.");
        if (manifest.Owner != Owner || manifest.SourceKey != AcceptedResultVault.Key(source.Settings) ||
            manifest.SettingsKey != settingsKey || manifest.ResultSha256 != AppData.FileKey(Path.Combine(folder, "result.json")) ||
            manifest.Hashes == null || !manifest.Hashes.Keys.Order().SequenceEqual(Artifacts.Order()))
            throw new InvalidDataException("Saved PV result metadata has changed.");
        foreach (var (name, hash) in manifest.Hashes)
            if (AppData.FileKey(Path.Combine(folder, name)) != hash)
                throw new InvalidDataException("A saved PV artifact has changed: " + name);
        return AppData.Read<SavedPv>(Path.Combine(folder, "result.json"))
            ?? throw new InvalidDataException("Saved PV result data is missing.");
    }
}
