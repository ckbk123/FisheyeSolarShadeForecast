using SolarShade.PvBattery;

namespace SolarShade.Desktop;

public sealed class PvWorkspaceController : IDisposable
{
    private readonly IrradianceWorkspaceController source;
    private readonly PvEvaluationService service;
    private readonly string settingsPath;
    private PvSettingsDraft draft;
    private CancellationTokenSource? pending;
    private long revision;
    private Task cleanup = Task.CompletedTask;
    public Task CleanupSettled => cleanup;
    private bool disposed;
    private IrradianceSnapshot? acceptedSource;
    private string? acceptedKey;
    public PvSettingsDraft Draft => draft.Copy();
    public BatterySimulationResult? Result { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsCurrent { get; private set; }
    public bool CanEvaluate => !disposed && !IsBusy && source.Source.IsReady && !source.IsBusy && Problem == null;
    public bool CanExport => IsCurrent && !IsBusy && !source.IsBusy && source.Source.IsReady;
    public string Status { get; private set; } = "Enter your system settings, then evaluate.";
    public string? Problem { get { try { draft.Parse(); return null; } catch (ArgumentException ex) { return ex.Message; } } }
    public event EventHandler? Changed;
    public PvWorkspaceController(IrradianceWorkspaceController source, PvEvaluationService service, string? settingsPath = null)
    {
        this.source = source; this.service = service; this.settingsPath = settingsPath ?? AppData.PathFor("pv-settings.json");
        draft = PvSettingsDraft.Restore(this.settingsPath); source.SourceChanged += SourceChanged;
    }
    public void SetDraft(PvSettingsDraft value)
    {
        draft = value.Copy(); Invalidate("Settings changed. Evaluate system to update results.");
        try { AppData.Write(settingsPath, draft); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "Settings could not be saved: " + ex.Message; }
        Notify(); _ = TryRestoreAsync();
    }
    private void SourceChanged(object? sender, EventArgs e)
    {
        Invalidate(source.Source.IsReady ? "Irradiance is ready. Evaluate system." : source.Source.Reason);
        Notify(); _ = TryRestoreAsync();
    }
    private async Task TryRestoreAsync()
    {
        long version = revision;
        try
        {
            await cleanup;
            if (disposed || version != revision || !source.Source.IsReady || source.IsBusy || Problem != null) return;
            var snapshot = source.Source.Snapshot!;
            string key = AppData.Key(draft.Parse());
            var restored = await Task.Run(() => PvAcceptedVault.Restore(snapshot, key, service.Store));
            if (restored == null || disposed || version != revision || !source.IsCurrent(snapshot)) return;
            Result = restored; acceptedSource = snapshot; acceptedKey = key; IsCurrent = true;
            Status = "Saved PV result restored for this study."; Notify();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { if (!disposed && version == revision && source.Source.IsReady) { Status = "Saved PV result could not be restored: " + ex.Message; Notify(); } }
    }
    private void Invalidate(string message)
    {
        revision++; pending?.Cancel(); IsCurrent = false; Status = message;
        var previous = cleanup; long version = revision;
        cleanup = ClearAsync();
        async Task ClearAsync()
        {
            await previous;
            try { await Task.Run(service.Store.Invalidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            { if (!disposed && version == revision) Status += " Saved PV output could not be cleared: " + ex.Message; }
        }
    }
    public async Task EvaluateAsync()
    {
        if (!CanEvaluate) return;
        Invalidate("Evaluating system…");
        var snapshot = source.Source.Snapshot!; var settings = draft.Parse(); long current = ++revision;
        pending?.Dispose(); pending = new(); var ct = pending.Token;
        IsBusy = true; IsCurrent = false; source.DependentBusy = true; Status = "Evaluating system…"; Notify();
        try
        {
            await cleanup; ct.ThrowIfCancellationRequested();
            var result = await service.Evaluate(snapshot, settings, ct);
            if (disposed || current != revision || !source.IsCurrent(snapshot)) return;
            Result = result; acceptedSource = snapshot; acceptedKey = AppData.Key(settings); IsCurrent = true;
            Status = result.Summary.UnmetLoadWh > 0 ? "Unmet load detected during this study." : "No unmet load during this study.";
            try { await Task.Run(() => PvAcceptedVault.Archive(snapshot, acceptedKey, service.Store, result)); }
            catch (Exception ex) { Status += " The saved result cannot yet be restored later: " + ex.Message; }
        }
        catch (OperationCanceledException) { if (current == revision) Status = "Stopped. Evaluate system to retry."; }
        catch (Exception ex) { if (!disposed && current == revision) Status = "Evaluation failed: " + ex.Message; }
        finally { pending?.Dispose(); pending = null; IsBusy = false; source.DependentBusy = false; if (!disposed) Notify(); }
    }
    public void Stop() { if (IsBusy) { Invalidate("Stopped. Evaluate system to retry."); Notify(); } }
    public void VerifyPublication()
    {
        if (source.IsBusy) return;
        if (IsCurrent && (acceptedSource == null || !source.IsCurrent(acceptedSource) || !service.Store.IsCurrent(acceptedSource, acceptedKey!)))
        { Invalidate("Source or PV output changed. Update the source if needed, then evaluate system again."); Notify(); }
    }
    public async Task VerifyPublicationAsync()
    {
        if (!IsCurrent || source.IsBusy) return;
        long version = revision; var snapshot = acceptedSource; var key = acceptedKey;
        bool valid = snapshot != null && source.IsCurrent(snapshot) && await Task.Run(() => service.Store.IsCurrent(snapshot, key!));
        if (!disposed && version == revision && !valid)
        { Invalidate("Source or PV output changed. Update the source if needed, then evaluate system again."); Notify(); }
    }
    public async Task<string?> ExportAsync(string parent)
    {
        await VerifyPublicationAsync(); if (!CanExport) return null;
        var snapshot = acceptedSource!; string key = acceptedKey!; long current = revision;
        pending?.Dispose(); pending = new(); var ct = pending.Token;
        IsBusy = true; source.DependentBusy = true; Notify();
        try
        {
            var path = await Task.Run(() => service.Store.Export(snapshot, key, parent, ct), ct);
            if (!disposed && current == revision) Status = "Exported PV results: " + path;
            return path;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { if (!disposed && current == revision) Status = "Export failed: " + ex.Message; return null; }
        finally { pending?.Dispose(); pending = null; IsBusy = false; source.DependentBusy = false; if (!disposed) Notify(); }
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        if (disposed) return; disposed = true; revision++; pending?.Cancel();
        source.SourceChanged -= SourceChanged; Changed = null;
    }
}
