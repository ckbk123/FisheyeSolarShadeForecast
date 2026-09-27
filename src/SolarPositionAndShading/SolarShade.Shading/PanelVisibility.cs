using System.Collections.Concurrent;
using SolarShade.Core.Models;

namespace SolarShade.Shading;

/// <summary>Receiver-weighted visible and observed fractions. Unknown sky is blocked in Visible.</summary>
public readonly record struct FluxVisibility(double Visible, double Known);
public readonly record struct DiskMoments(Direction Visible, Direction Known, Direction Total);

public sealed partial class SkyShadingModule
{
    internal (double Sky, bool Known) SampleDirection(Direction ray) =>
        TryProject(ray, out var x, out var y) ? (Sky(x, y), true) : (0, false);

    internal DiskMoments PanelMoments(Direction sun)
    {
        var (u, v) = Basis(sun);
        Direction visible = default, known = default, all = default;
        foreach (var c in cap)
        {
            var ray = c.Up * sun + c.East * u + c.North * v;
            if (ray.Up <= 0) continue;
            all += ray;
            var p = SampleDirection(ray);
            if (p.Known) { known += ray; visible += p.Sky * ray; }
        }
        return new(visible, known, all);
    }

    internal FluxVisibility PanelDisk(Direction sun, Direction normal, DiskMoments moments)
    {
        if (normal.Dot(sun) > horizonMargin)
        {
            double total = normal.Dot(moments.Total);
            return total > 0 ? new(Math.Clamp(normal.Dot(moments.Visible) / total, 0, 1),
                Math.Clamp(normal.Dot(moments.Known) / total, 0, 1)) : new(0, 0);
        }
        // Near a receiver's grazing angle, clip each ray against both sky and receiver horizons.
        var (u, v) = Basis(sun);
        double all = 0, known = 0, visible = 0;
        foreach (var c in cap)
        {
            var ray = c.Up * sun + c.East * u + c.North * v;
            if (ray.Up <= 0) continue;
            double weight = Math.Max(0, normal.Dot(ray)); all += weight;
            var p = SampleDirection(ray);
            if (p.Known) { known += weight; visible += weight * p.Sky; }
        }
        return all > 0 ? new(visible / all, known / all) : new(0, 0);
    }
}

/// <summary>Immutable calibrated sky with reusable disk moments. Panel edits only reweight cached rays.</summary>
public sealed class PanelSkyScene
{
    private readonly SkyShadingModule projector;
    private readonly (Direction Ray, double Sky, bool Known)[] dome;
    private readonly ConcurrentDictionary<Direction, DiskMoments> moments = new();
    public int CachedSolarDirections => moments.Count;
    public PanelSkyScene(SkyMask mask, CalibrationResult calibration, CameraPose pose, ImageDisk disk,
        int diskSamples = 64, int domeSamples = 32768)
    {
        if (domeSamples is < 64 or > 4000000) throw new ArgumentOutOfRangeException(nameof(domeSamples));
        projector = new(mask, calibration, pose, new ShadingOptions
            { SolarAngularRadiusDegrees = .25, DiskSamples = diskSamples, DiffuseSamples = 64 }, disk);
        dome = new (Direction, double, bool)[domeSamples];
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < dome.Length; i++)
        {
            double z = (i + .5) / dome.Length, r = Math.Sqrt(1 - z * z), a = i * golden;
            var ray = new Direction(r * Math.Cos(a), r * Math.Sin(a), z);
            var sample = projector.SampleDirection(ray);
            dome[i] = (ray, sample.Sky, sample.Known);
        }
    }
    public FluxVisibility Dome(Direction normal)
    {
        ValidateDirection(normal);
        double all = 0, known = 0, visible = 0;
        foreach (var p in dome)
        {
            double weight = Math.Max(0, normal.Dot(p.Ray)); all += weight;
            if (p.Known) { known += weight; visible += weight * p.Sky; }
        }
        return all > 0 ? new(visible / all, known / all) : new(0, 0);
    }
    public DiskMoments Moments(Direction sun)
    { ValidateDirection(sun); return moments.GetOrAdd(sun, projector.PanelMoments); }
    public FluxVisibility Disk(Direction sun, Direction normal)
    { ValidateDirection(normal); return projector.PanelDisk(sun, normal, Moments(sun)); }
    private static void ValidateDirection(Direction ray)
    {
        double norm = ray.Dot(ray);
        if (!double.IsFinite(norm) || Math.Abs(norm - 1) > 1e-8)
            throw new ArgumentException("A finite unit direction is required.");
    }
}
