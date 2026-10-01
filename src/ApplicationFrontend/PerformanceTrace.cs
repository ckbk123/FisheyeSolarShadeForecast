using System.Diagnostics;
using System.Text.Json;

namespace SolarShade.Desktop;

/// <summary>Opt-in local tracing. Logging must never prevent completion.</summary>
internal sealed class PerformanceTrace : IDisposable
{
    private static readonly AsyncLocal<string?> operation = new();
    private static readonly object gate = new();
    private readonly string name, detail;
    private readonly string? previous;
    private readonly bool root;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly long allocated = GC.GetTotalAllocatedBytes();
    private readonly string? path = Environment.GetEnvironmentVariable("SOLARSHADE_PERFORMANCE_LOG");
    public string? Outcome { get; set; }
    private PerformanceTrace(string name, string detail, bool root)
    { this.name = name; this.detail = detail; this.root = root; previous = operation.Value; if (root) operation.Value = Guid.NewGuid().ToString("N"); }
    public static PerformanceTrace Operation(string name) => new(name, "", true);
    public static PerformanceTrace Phase(string name, string detail = "") => new(name, detail, false);
    public void Dispose()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var row = new { Operation = operation.Value, Phase = name, Detail = detail, Outcome,
                    Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated, Utc = DateTimeOffset.UtcNow };
                lock (gate) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.AppendAllText(path, JsonSerializer.Serialize(row) + Environment.NewLine); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        finally { if (root) operation.Value = previous; }
    }
}
