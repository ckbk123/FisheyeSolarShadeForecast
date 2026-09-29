namespace SolarShade.Desktop;

/// <summary>Owns the irradiance editing session and accepted-source availability, separately from the shell.</summary>
public sealed class IrradianceWorkspaceController : IDisposable
{
    public IIrradianceService Service { get; }
    public UserSettings Settings { get; internal set; } = new();
    public Evaluation? Completed { get; internal set; }
    public UpdateState State { get; internal set; }
    public long Revision { get; internal set; }
    internal CancellationTokenSource? Pending { get; set; }
    internal bool Updating { get; set; }
    internal bool Calibrating { get; set; }
    public bool IsBusy => Updating || Calibrating;
    public SourceReadiness Source { get; private set; } = new(null, "Complete and update Solar Irradiance first.");
    public event EventHandler? SourceChanged;
    private bool disposed;
    public IrradianceWorkspaceController(IIrradianceService service) => Service = service;
    public void RefreshSource()
    {
        if (disposed) return;
        SourceReadiness next;
        if (IsBusy || State != UpdateState.UpToDate)
            next = new(null, IsBusy ? "Solar Irradiance is updating." : "Complete and update Solar Irradiance first.");
        else if (Source.Snapshot is { } prior && ReferenceEquals(prior.Evaluation, Completed) &&
            Service.IsCurrent(prior.Evaluation) && prior.Evaluation.ManagedDebugRun?.HasCompleteDataset == true)
            return;
        else next = IrradianceReadiness.Inspect(Service, Completed);
        if (Source.Snapshot?.Id == next.Snapshot?.Id && Source.Reason == next.Reason) return;
        Source = next; SourceChanged?.Invoke(this, EventArgs.Empty);
    }
    public bool IsCurrent(IrradianceSnapshot snapshot) => !disposed && !IsBusy && State == UpdateState.UpToDate &&
        Source.Snapshot?.Id == snapshot.Id && Service.IsCurrent(snapshot.Evaluation) &&
        snapshot.Evaluation.ManagedDebugRun?.HasCompleteDataset == true;
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Pending?.Cancel(); Pending?.Dispose(); Service.Dispose(); SourceChanged = null;
    }
}
