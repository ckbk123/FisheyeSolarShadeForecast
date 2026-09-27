using OpenCvSharp;
using SolarShade.Core.Models;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SolarShade.Shading;

/// <summary>Default: phone face down, camera looks up, image top north/bottom south.
/// Positive tilt moves the optical axis toward ImageTopAzimuthDegrees. Roll rotates image axes about the optical axis.
/// All coordinates refer to the EXIF-oriented image, not raw JPEG storage.</summary>
public sealed record CameraPose(double ImageTopAzimuthDegrees = 0, double TiltDegrees = 0, double RollDegrees = 0)
{
    /// <summary>Converts the true-north bearing of the image bottom to the camera convention.</summary>
    public static CameraPose FromImageBottom(double bottomAzimuthDegrees, double tiltDegrees = 0, double rollDegrees = 0)
    {
        if (!double.IsFinite(bottomAzimuthDegrees)) throw new ArgumentOutOfRangeException(nameof(bottomAzimuthDegrees));
        var pose = new CameraPose(((bottomAzimuthDegrees + 180) % 360 + 360) % 360, tiltDegrees, rollDegrees);
        _ = pose.Axes();
        return pose;
    }
    /// <summary>Exact convention adapter for legacy Python psi/omega.</summary>
    public static CameraPose FromLegacy(double psiDegrees, double omegaDegrees) => new(-psiDegrees, -omegaDegrees);
    internal (Direction X, Direction Y, Direction Z) Axes()
    {
        if (!double.IsFinite(ImageTopAzimuthDegrees) || !double.IsFinite(TiltDegrees) || Math.Abs(TiltDegrees) > 180 || !double.IsFinite(RollDegrees))
            throw new ArgumentOutOfRangeException(nameof(CameraPose));
        var (s, c) = Math.SinCos(ImageTopAzimuthDegrees * Math.PI/180);
        var (st, ct) = Math.SinCos(TiltDegrees * Math.PI/180);
        var (sr, cr) = Math.SinCos(RollDegrees * Math.PI/180);
        var x = new Direction(-c, s, 0);
        var y = new Direction(-ct*s, -ct*c, st);
        return (cr*x+sr*y, -sr*x+cr*y, new(st*s, st*c, ct));
    }
}

/// <summary>Actual imaged lens disk; may differ from calibrated principal point.</summary>
public sealed record ImageDisk(double CenterX, double CenterY, double RadiusPixels);
public enum MissingCoveragePolicy { ReportUnknown, AssumeBlocked }
public sealed record ShadingOptions
{
    public double SolarAngularRadiusDegrees { get; init; } = 0.25;
    public int DiskSamples { get; init; } = 128;
    public int DiffuseSamples { get; init; } = 65536;
    public int MaxDegreeOfParallelism { get; init; } = 0;
    public MissingCoveragePolicy MissingCoverage { get; init; } = MissingCoveragePolicy.AssumeBlocked;
    public bool UseUniformRegionShortcut { get; init; } = true;
}

/// <summary>Loss bounds: lower assumes uncovered sky is open; upper assumes it is blocked.
/// Uncovered directions are blocked by default; ReportUnknown explicitly returns null for incomplete coverage.</summary>
public sealed record VisibilityEstimate(double? ShadingFactor, double ShadingLowerBound, double ShadingUpperBound, double Coverage);
public sealed record ShadingRow(DateTimeOffset Timestamp, SolarAngles? SolarPosition,
    VisibilityEstimate Direct, string Status);
public sealed record ShadingResult(IReadOnlyList<ShadingRow> Rows, VisibilityEstimate Diffuse,
    double SolarAngularRadiusDegrees, int DiskSamples, TimeSampling Sampling);

/// <summary>Module 2: immutable prepared mask/calibration, reusable and safe for concurrent Evaluate calls.
/// Uniform-radiance solar disk; isotropic diffuse sky on a horizontal receiving plane.</summary>
public sealed partial class SkyShadingModule
{
    private readonly byte[] pixels;
    private readonly int width, height;
    private readonly CalibratedSkyProjection projection;
    private readonly ShadingOptions options;
    private readonly Direction[] cap;
    private readonly byte[] tiles;
    private readonly int tileWidth;
    private readonly double footprintBound, fullDiskMinZ, horizonMargin;
    private readonly Direction capSum;
    public VisibilityEstimate Diffuse { get; }

    public SkyShadingModule(SkyMask mask, CalibrationResult calibration, CameraPose? pose = null,
        ShadingOptions? options = null, ImageDisk? imageDisk = null)
    {
        this.options = options ?? new();
        var o = this.options;
        if (!double.IsFinite(o.SolarAngularRadiusDegrees) || o.SolarAngularRadiusDegrees is < 0 or > 10 ||
            o.DiskSamples is < 1 or > 65536 || o.DiffuseSamples is < 64 or > 4000000 || o.MaxDegreeOfParallelism < 0 || !Enum.IsDefined(o.MissingCoverage))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (mask.Width < 2 || mask.Height < 2 || mask.Pixels.Length != (long)mask.Width*mask.Height ||
            mask.Width != calibration.ImageWidth || mask.Height != calibration.ImageHeight)
            throw new ArgumentException("Mask and calibration must have identical EXIF-oriented dimensions.");
        projection = new(calibration, pose, imageDisk);
        width = mask.Width; height = mask.Height;
        pixels = (byte[])mask.Pixels.Clone();
        tileWidth = (width+15)/16;
        tiles = new byte[tileWidth*((height+15)/16)];
        for(int ty=0;ty<(height+15)/16;ty++)
        for(int tx=0;tx<tileWidth;tx++)
        {
            if(Vector128.IsHardwareAccelerated && tx*16+16<=width)
            {
                var any=Vector128<byte>.Zero;var all=Vector128.Create((byte)255);
                for(int yy=ty*16;yy<Math.Min(height,ty*16+16);yy++)
                {
                    var value=Vector128.LoadUnsafe(ref pixels[yy*width+tx*16]);
                    var binary=Vector128.BitwiseOr(Vector128.Equals(value,Vector128<byte>.Zero),Vector128.Equals(value,Vector128.Create((byte)255)));
                    if(!Vector128.EqualsAll(binary,Vector128.Create((byte)255)))throw new ArgumentException("Mask must contain only 0 (blocked) and 255 (sky).");
                    any=Vector128.BitwiseOr(any,value);all=Vector128.BitwiseAnd(all,value);
                }
                tiles[ty*tileWidth+tx]=Vector128.EqualsAll(any,Vector128<byte>.Zero)?(byte)0:Vector128.EqualsAll(all,Vector128.Create((byte)255))?(byte)255:(byte)128;
                continue;
            }
            byte first=pixels[ty*16*width+tx*16]; bool same=true;
            for(int yy=ty*16;yy<Math.Min(height,ty*16+16);yy++)
            for(int xx=tx*16;xx<Math.Min(width,tx*16+16);xx++)
            {
                byte value=pixels[yy*width+xx];
                if(value!=0&&value!=255)throw new ArgumentException("Mask must contain only 0 (blocked) and 255 (sky).");
                same &= value==first;
            }
            tiles[ty*tileWidth+tx]=same?first:(byte)128;
        }
        footprintBound = projection.LipschitzBound*o.SolarAngularRadiusDegrees*Math.PI/180;
        fullDiskMinZ = Math.Cos(Math.Max(0,projection.MaximumIncidentAngleRadians-o.SolarAngularRadiusDegrees*Math.PI/180));
        horizonMargin = Math.Sin(o.SolarAngularRadiusDegrees*Math.PI/180);
        cap = CreateCap(o.SolarAngularRadiusDegrees, o.DiskSamples);
        foreach(var p in cap)capSum += p;
        Diffuse = ComputeDiffuse();
    }

    public static SkyMask DecodeMask(byte[] encodedPng)
    {
        var fast=MaskPng.TryDecode(encodedPng);
        if(fast!=null)return fast;
        using var mat = Cv2.ImDecode(encodedPng, ImreadModes.Grayscale);
        if (mat.Empty()) throw new InvalidDataException("Cannot decode mask PNG.");
        int w=mat.Width,h=mat.Height;
        var bytes = new byte[checked(w*h)];
        if(mat.IsContinuous()) Marshal.Copy(mat.Data,bytes,0,bytes.Length);
        else for (int y = 0; y < h; y++) Marshal.Copy(mat.Ptr(y), bytes, y*w, w);
        return new(w, h, bytes);
    }

    /// <summary>Projects a unit world ray without perspective division. Horizon eligibility is handled separately.</summary>
    public bool TryProject(Direction ray, out double x, out double y)
        => projection.TryProject(ray, out x, out y);

    private double Sky(double x, double y)
    {
        // Subpixel area proxy; avoids truncation and makes tiny disks stable across pixel boundaries.
        int ix = Math.Min((int)x, width-2), iy = Math.Min((int)y, height-2), p = iy*width+ix;
        double fx = x-ix, fy = y-iy;
        return ((1-fy)*((1-fx)*pixels[p]+fx*pixels[p+1]) + fy*((1-fx)*pixels[p+width]+fx*pixels[p+width+1]))/255;
    }

    private static Direction[] CreateCap(double radius, int count)
    {
        var result = new Direction[count];
        double extent = 1-Math.Cos(radius*Math.PI/180), golden = Math.PI*(3-Math.Sqrt(5));
        for (int i = 0; i < count; i++)
        {
            // Uniform solid angle: cos(theta) is uniform. Precompute once, not for every hour.
            var z = 1-extent*(i+0.5)/count;
            var r = Math.Sqrt(Math.Max(0, 1-z*z));
            var (s, c) = Math.SinCos(i*golden);
            result[i] = new(r*c, r*s, z);
        }
        return result;
    }

    private static (Direction U, Direction V) Basis(Direction s)
    {
        var u = s.Cross(Math.Abs(s.Up) < 0.9 ? new(0,0,1) : new(1,0,0)).Unit();
        return (u, s.Cross(u));
    }

    /// <summary>Exact spherical boundary, for debug overlays. Invalid vertices are null; never bridge missing coverage.</summary>
    public IReadOnlyList<Point2d?> ProjectBoundary(SolarAngles sun, int vertices = 128)
    {
        if (vertices < 3) throw new ArgumentOutOfRangeException(nameof(vertices));
        var s = sun.Direction; var (u,v) = Basis(s);
        var (sa,ca) = Math.SinCos(options.SolarAngularRadiusDegrees*Math.PI/180);
        var points = new Point2d?[vertices];
        for (int i = 0; i < vertices; i++)
        {
            var (st,ct) = Math.SinCos(2*Math.PI*i/vertices);
            var p = ca*s + (sa*ct)*u + (sa*st)*v;
            if (p.Up > 0 && TryProject(p, out var x, out var y)) points[i] = new(x,y);
        }
        return points;
    }

    private (double Visible, double Known, double Total) Disk(Direction s)
    {
        // Entire disk below the geometric horizon: no horizontal direct flux to attenuate.
        if (s.Up <= -horizonMargin) return (0,0,0);
        var (u,v) = Basis(s);
        if(options.UseUniformRegionShortcut && s.Up>horizonMargin && s.Dot(projection.OpticalAxis)>=fullDiskMinZ &&
            TryProject(s,out var px,out var py) && UniformFootprint(px,py,out var sky))
        {
            var flux=capSum.Up*s.Up+capSum.East*u.Up+capSum.North*v.Up;
            return(sky*flux,flux,flux);
        }
        double visible = 0, known = 0, total = 0;
        foreach (var p in cap)
        {
            var ray = p.Up*s+p.East*u+p.North*v;
            var weight = Math.Max(0, ray.Up); // horizontal irradiance projection and horizon clipping
            total += weight;
            if (weight > 0 && TryProject(ray, out var x, out var y)) { known += weight; visible += weight*Sky(x,y); }
        }
        return (visible,known,total);
    }

    private bool UniformFootprint(double x,double y,out double sky)
    {
        sky=0;
        var disk = projection.CoverageDisk;
        double r=footprintBound+1;
        if(x-r<0||y-r<0||x+r>=width||y+r>=height||
            Math.Sqrt((x-disk.CenterX)*(x-disk.CenterX)+(y-disk.CenterY)*(y-disk.CenterY))+r>disk.RadiusPixels)return false;
        int left=(int)(x-r)/16,right=(int)(x+r)/16,top=(int)(y-r)/16,bottom=(int)(y+r)/16;
        var value=tiles[top*tileWidth+left];
        if(value==128)return false;
        for(int ty=top;ty<=bottom;ty++)for(int tx=left;tx<=right;tx++)if(tiles[ty*tileWidth+tx]!=value)return false;
        sky=value/255.0;return true;
    }

    private VisibilityEstimate Estimate(double visible, double known, double total)
    {
        if (total <= 0) return new(null,0,1,0);
        double coverage = Math.Clamp(known/total,0,1);
        double upper = Math.Clamp(1-visible/total,0,1);
        double lower = Math.Clamp(upper-(1-coverage),0,upper); // preserve ordered bounds even at floating-point endpoints
        return new(coverage >= 1-1e-10 || options.MissingCoverage == MissingCoveragePolicy.AssumeBlocked ? upper : null, lower, upper, coverage);
    }

    public ShadingResult Evaluate(SolarSequence sequence, CancellationToken cancellationToken = default)
    {
        var results = new ShadingRow[sequence.Rows.Count];
        Parallel.For(0, results.Length, new ParallelOptions { CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism == 0 ? Math.Max(1, Environment.ProcessorCount/2) : options.MaxDegreeOfParallelism }, i =>
        {
            var row = sequence.Rows[i];
            if(!double.IsFinite(row.DirectHorizontalWm2)||row.DirectHorizontalWm2<0)throw new ArgumentException("Invalid direct irradiance.");
            if (row.DirectHorizontalWm2 == 0)
            {
                // Neutral multiplier: zero input remains zero. Coverage is N/A, encoded 0 with explicit status.
                results[i] = new(row.Timestamp,null,new(0,0,0,0),"SkippedZeroDirect"); return;
            }
            if (row.IntegrationDirections.Count == 0) throw new ArgumentException("Positive direct irradiance requires solar directions.");
            double visible = 0, known = 0, total = 0;
            foreach (var s in row.IntegrationDirections)
            {
                var norm=s.Dot(s);
                if(!double.IsFinite(norm)||Math.Abs(norm-1)>1e-8)throw new ArgumentException("Integration directions must be finite unit vectors.");
                var d = Disk(s); visible += d.Visible; known += d.Known; total += d.Total;
            }
            var e = Estimate(visible,known,total);
            results[i] = new(row.Timestamp,row.AtLabel,e,total <= 0 ? "PositiveDirectBelowHorizon" : e.Coverage < 1-1e-10 ? "IncompleteCoverage" : "Computed");
        });
        return new(Array.AsReadOnly(results),Diffuse,options.SolarAngularRadiusDegrees,options.DiskSamples,sequence.Sampling);
    }

    private VisibilityEstimate ComputeDiffuse()
    {
        double visible = 0, known = 0;
        // Cosine-weighted hemisphere quadrature: equal weights already include cos(zenith) dOmega.
        for (int i = 0; i < options.DiffuseSamples; i++)
        {
            double u = (i+0.5)/options.DiffuseSamples, r = Math.Sqrt(u), z = Math.Sqrt(1-u);
            var (s,c) = Math.SinCos(i*Math.PI*(3-Math.Sqrt(5)));
            if (TryProject(new(r*s,r*c,z),out var x,out var y)) { known++; visible += Sky(x,y); }
        }
        return Estimate(visible,known,options.DiffuseSamples);
    }
}
