using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SkyPhotoMasking.Tests")]

namespace SolarShade.SkyPhotoMasking;

public enum SkyModel { EfficientNetB4, EfficientNetB5, EfficientNetB6, EfficientNetB7 }
public enum MaskAcceleration { Auto, Cpu, DirectML }
public enum DiskDetection { Auto, Centered }

/// <summary>Coordinates in the EXIF-oriented image, matching OpenCV's default image reader.</summary>
public sealed record LensDisk(double CenterX, double CenterY, double Radius, int ImageWidth, int ImageHeight);
public sealed record MaskCrop(int X, int Y, int Width, int Height);
public sealed record GpuAdapter(int DeviceId, string Name, ulong DedicatedMemoryBytes);
public sealed record MaskHardware(string Architecture, int LogicalProcessors, bool CpuVectorAcceleration,
    IReadOnlyList<GpuAdapter> DirectMLAdapters, string? ProbeMessage);

public sealed record SkyMaskOptions
{
    public string ModelsDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "SkyPhotoModels");
    /// <summary>1024 preserves the original default. 512 is the original fast mode.</summary>
    public int InputSize { get; init; } = 1024;
    public MaskAcceleration Acceleration { get; init; } = MaskAcceleration.Auto;
    public int? DirectMLDeviceId { get; init; }
    public DiskDetection DiskDetection { get; init; } = DiskDetection.Auto;
    /// <summary>Optional calibrated disk overrides automatic detection; image dimensions must match.</summary>
    public LensDisk? Disk { get; init; }
    /// <summary>0 lets ONNX Runtime choose physical cores and affinity; overrides must be positive.</summary>
    public int CpuThreads { get; init; }
    /// <summary>Development/validator data, not required by the app.</summary>
    public bool IncludeDiagnostics { get; init; }
}

public sealed record SkyMaskResult(
    [property: JsonIgnore] byte[] Png, SkyModel Model, int Width, int Height, int InputSize,
    LensDisk Disk, MaskCrop Crop, string DiskMethod, string ExecutionProvider, string? AccelerationFallback,
    double SessionLoadMilliseconds, double InferenceMilliseconds, double TotalMilliseconds,
    [property: JsonIgnore] float[]? Probabilities,
    [property: JsonIgnore] byte[]? InputRgbPng);

/// <summary>
/// Reusable Python-free sky segmentation. CreateMask(localPath, model) returns a lossless PNG:
/// white=sky, black=obstructions/outside lens. Reuse and dispose this object.
/// Calls are serialized; only the most recent model stays loaded to bound GPU/RAM use.
/// </summary>
public sealed class SkyPhotoMasker : IDisposable
{
    private readonly SkyMaskOptions options;
    private readonly object gate = new();
    private InferenceSession? session;
    private SkyModel? loadedModel;
    private string provider = "CPU";
    private string? fallback;
    private bool disposed;
    public MaskHardware Hardware { get; }

    public SkyPhotoMasker(SkyMaskOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.InputSize is not (512 or 1024))
            throw new ArgumentOutOfRangeException(nameof(options), "InputSize must be 512 or 1024.");
        if (this.options.CpuThreads < 0 || this.options.DirectMLDeviceId < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(this.options.Acceleration) || !Enum.IsDefined(this.options.DiskDetection))
            throw new ArgumentOutOfRangeException(nameof(options));
        Hardware = ProbeHardware();
    }

    /// <summary>The two-input API. Returns encoded grayscale PNG bytes at original oriented dimensions.</summary>
    public byte[] CreateMask(string imagePath, SkyModel model) => CreateMaskDetailed(imagePath, model).Png;

    public SkyMaskStage CreateMaskToDirectory(string imagePath, SkyModel model, string debugDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = CreateMaskDetailed(imagePath, model);
        cancellationToken.ThrowIfCancellationRequested();
        return new(result, SkyMaskExporter.Export(result, debugDirectory, "Computed", cancellationToken));
    }

    public SkyMaskResult CreateMaskDetailed(string imagePath, SkyModel model)
    {
        var total = Stopwatch.StartNew();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var filename = ModelFilename(model); // validate enum before any work
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Sky image not found.", imagePath);
            using var source = Cv2.ImRead(Path.GetFullPath(imagePath), ImreadModes.Color);
            if (source.Empty()) throw new InvalidDataException("Image could not be decoded.");
            if (Math.Min(source.Width, source.Height) < 64) throw new InvalidDataException("Image is too small (minimum 64 pixels per side).");
            var diskMethod = options.Disk is not null ? "Camera profile" : options.DiskDetection.ToString();
            var disk = options.Disk ?? FindDisk(source, options.DiskDetection);
            ValidateDisk(disk, source.Width, source.Height);
            var crop = CropForDisk(disk);
            using var cropped = new Mat(source, new Rect(crop.X, crop.Y, crop.Width, crop.Height));
            using var masked = cropped.Clone();
            BlackOutsideCircle(masked);
            using var resized = new Mat();
            Cv2.Resize(masked, resized, new Size(options.InputSize, options.InputSize), interpolation: InterpolationFlags.Linear);
            var tensor = ToTensor(resized);
            var load = Stopwatch.StartNew();
            EnsureSession(model, filename);
            var loadMs = load.Elapsed.TotalMilliseconds;
            var inference = Stopwatch.StartNew();
            float[] probabilities;
            try { probabilities = Run(tensor); }
            catch (OnnxRuntimeException ex) when (options.Acceleration == MaskAcceleration.Auto && provider.StartsWith("DirectML"))
            {
                // Same model and resolution. No heuristic substitute or hidden quality downgrade.
                fallback = $"GPU inference failed; retried on CPU: {ex.Message}";
                session?.Dispose(); session = null; loadedModel = null;
                CreateSession(model, filename, null);
                probabilities = Run(tensor);
            }
            var inferenceMs = inference.Elapsed.TotalMilliseconds;
            using var smallMask = Threshold(probabilities, options.InputSize, model);
            using var restored = new Mat();
            Cv2.Resize(smallMask, restored, new Size(crop.Width, crop.Height), interpolation: InterpolationFlags.Nearest);
            using var full = new Mat(source.Height, source.Width, MatType.CV_8UC1, Scalar.Black);
            using (var roi = new Mat(full, new Rect(crop.X, crop.Y, crop.Width, crop.Height))) restored.CopyTo(roi);
            Cv2.ImEncode(".png", full, out var png);
            byte[]? inputPng = null;
            if (options.IncludeDiagnostics) Cv2.ImEncode(".png", resized, out inputPng);
            return new(png, model, source.Width, source.Height, options.InputSize, disk, crop, diskMethod,
                provider, fallback, loadMs, inferenceMs, total.Elapsed.TotalMilliseconds,
                options.IncludeDiagnostics ? probabilities : null, inputPng);
        }
    }

    public static string ModelFilename(SkyModel model) => model switch
    {
        SkyModel.EfficientNetB4 => "efficientnet-b4.onnx", SkyModel.EfficientNetB5 => "efficientnet-b5.onnx",
        SkyModel.EfficientNetB6 => "efficientnet-b6.onnx", SkyModel.EfficientNetB7 => "efficientnet-b7.onnx",
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    public static float ModelThreshold(SkyModel model) => model switch
    {
        SkyModel.EfficientNetB4 => 155 / 255f, SkyModel.EfficientNetB5 => 158 / 255f,
        SkyModel.EfficientNetB6 => 148 / 255f, SkyModel.EfficientNetB7 => 161 / 255f,
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    private void EnsureSession(SkyModel model, string filename)
    {
        if (session is not null && loadedModel == model) return;
        session?.Dispose(); session = null; loadedModel = null; fallback = null;
        if (!File.Exists(Path.Combine(options.ModelsDirectory, filename)))
            throw new FileNotFoundException("Pretrained ONNX model missing. Deploy SkyPhotoModels with the application.", filename);
        GpuAdapter? gpu = options.DirectMLDeviceId is int id
            ? Hardware.DirectMLAdapters.FirstOrDefault(g => g.DeviceId == id)
            : Hardware.DirectMLAdapters.OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();
        if (options.Acceleration != MaskAcceleration.Cpu && gpu is not null)
        {
            try { CreateSession(model, filename, gpu); return; }
            catch (OnnxRuntimeException ex) when (options.Acceleration == MaskAcceleration.Auto)
            { fallback = $"DirectML unavailable for this model; CPU selected: {ex.Message}"; }
        }
        else if (options.Acceleration == MaskAcceleration.DirectML)
            throw new NotSupportedException("No compatible Direct3D 12 hardware adapter was found for the requested device.");
        else if (options.Acceleration == MaskAcceleration.Auto)
            fallback = Hardware.ProbeMessage ?? "No compatible Direct3D 12 adapter found; CPU selected.";
        CreateSession(model, filename, null);
    }

    private void CreateSession(SkyModel model, string filename, GpuAdapter? gpu)
    {
        using var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = gpu is null,
            IntraOpNumThreads = options.CpuThreads
        };
        so.AddFreeDimensionOverrideByName("height", options.InputSize);
        so.AddFreeDimensionOverrideByName("width", options.InputSize);
        // Idle desktop apps should not spin worker threads indefinitely between photos.
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        if (gpu is not null) so.AppendExecutionProvider_DML(gpu.DeviceId);
        var candidate = new InferenceSession(Path.Combine(options.ModelsDirectory, filename), so);
        try
        {
            var input = candidate.InputMetadata;
            var output = candidate.OutputMetadata;
            if (input.Count != 1 || !input.TryGetValue("image", out var info) || info.ElementType != typeof(float)
                || info.Dimensions.Length != 4 || info.Dimensions[0] != 1 || info.Dimensions[1] != 3
                || info.Dimensions[2] != options.InputSize || info.Dimensions[3] != options.InputSize
                || output.Count != 1 || !output.ContainsKey("obstacle_probability"))
                throw new InvalidDataException("Model must use the supplied RGB NCHW /255 and sigmoid-output export contract.");
            if (!candidate.ModelMetadata.CustomMetadataMap.TryGetValue("encoder", out var encoder)
                || encoder != Path.GetFileNameWithoutExtension(filename))
                throw new InvalidDataException("Model metadata does not match the selected EfficientNet variant.");
            session = candidate; loadedModel = model;
            provider = gpu is null ? "CPU" : $"DirectML: {gpu.Name} (adapter {gpu.DeviceId})";
        }
        catch { candidate.Dispose(); throw; }
    }

    private float[] Run(DenseTensor<float> tensor)
    {
        using var outputs = session!.Run(new[] { NamedOnnxValue.CreateFromTensor("image", tensor) });
        var output = outputs.Single().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (!dims.SequenceEqual(new[] { 1, 1, options.InputSize, options.InputSize }))
            throw new InvalidDataException("Unexpected model output dimensions.");
        return output.ToArray();
    }

    internal static unsafe DenseTensor<float> ToTensor(Mat bgr)
    {
        var n = bgr.Width; var plane = n * n;
        var data = new float[3 * plane];
        // Planar RGB, no ImageNet/AdvProp normalization: this matches inference.py.
        Parallel.For(0, n, y =>
        {
            var row = (byte*)bgr.Ptr(y);
            for (var x = 0; x < n; x++)
            {
                var i = y * n + x;
                data[i] = row[3 * x + 2] / 255f;
                data[plane + i] = row[3 * x + 1] / 255f;
                data[2 * plane + i] = row[3 * x] / 255f;
            }
        });
        return new DenseTensor<float>(data, new[] { 1, 3, n, n });
    }

    internal static unsafe Mat Threshold(float[] probabilities, int n, SkyModel model)
    {
        var threshold = ModelThreshold(model);
        var mask = new Mat(n, n, MatType.CV_8UC1);
        var radius = (n - 1) / 2.0;
        try
        {
            for (var y = 0; y < n; y++)
            {
                var row = (byte*)mask.Ptr(y);
                for (var x = 0; x < n; x++)
                {
                    var p = probabilities[y * n + x];
                    if (!float.IsFinite(p) || p < -0.00001f || p > 1.00001f)
                        throw new InvalidDataException("Model returned invalid probabilities.");
                    row[x] = p <= threshold && (x-radius)*(x-radius)+(y-radius)*(y-radius) <= radius*radius
                        ? (byte)255 : (byte)0;
                }
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    private static void ValidateDisk(LensDisk disk, int width, int height)
    {
        if (disk.ImageWidth != width || disk.ImageHeight != height)
            throw new InvalidDataException("Camera disk dimensions do not match the EXIF-oriented image.");
        if (!double.IsFinite(disk.CenterX) || !double.IsFinite(disk.CenterY) || !double.IsFinite(disk.Radius)
            || disk.Radius < 16 || disk.Radius > Math.Max(width, height)
            || disk.CenterX < 0 || disk.CenterX >= width || disk.CenterY < 0 || disk.CenterY >= height)
            throw new InvalidDataException("Invalid camera disk coordinates.");
    }

    internal static MaskCrop CropForDisk(LensDisk disk)
    {
        // Preserve Python inclusive endpoints and square crop semantics.
        var cx = (int)Math.Round(disk.CenterX); var cy = (int)Math.Round(disk.CenterY);
        var r = (int)Math.Round(disk.Radius);
        var x0 = Math.Max(cx-r, 0); var x1 = Math.Min(cx+r, disk.ImageWidth-1);
        var y0 = Math.Max(cy-r, 0); var y1 = Math.Min(cy+r, disk.ImageHeight-1);
        var size = Math.Min(x1-x0+1, y1-y0+1);
        x0 = Math.Max(cx-size/2, 0); y0 = Math.Max(cy-size/2, 0);
        x1 = Math.Min(x0+size-1, disk.ImageWidth-1); y1 = Math.Min(y0+size-1, disk.ImageHeight-1);
        if (x1-x0 != y1-y0 || x1-x0 < 32) throw new InvalidDataException("Lens disk cannot produce a valid square crop.");
        return new(x0, y0, x1-x0+1, y1-y0+1);
    }

    private static unsafe void BlackOutsideCircle(Mat image)
    {
        var width = image.Width; var height = image.Height;
        var center = (width-1)/2; // Python crop mask uses integer center
        for (var y = 0; y < height; y++)
        {
            var row = (byte*)image.Ptr(y);
            for (var x = 0; x < width; x++)
                if ((long)(x-center)*(x-center)+(long)(y-center)*(y-center) > (long)center*center)
                    row[3*x] = row[3*x+1] = row[3*x+2] = 0;
        }
    }

    internal static LensDisk FindDisk(Mat source, DiskDetection mode)
    {
        if (mode == DiskDetection.Centered)
            return new((source.Width-1)/2.0, (source.Height-1)/2.0, (Math.Min(source.Width, source.Height)-1)/2.0, source.Width, source.Height);
        var scale = Math.Min(1.0, 768.0 / Math.Max(source.Width, source.Height));
        using var small = new Mat(); using var gray = new Mat();
        Cv2.Resize(source, small, new Size(), scale, scale, InterpolationFlags.Area);
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(5, 5), 1.2);
        var minSide = Math.Min(gray.Width, gray.Height);
        var circles = Cv2.HoughCircles(gray, HoughModes.Gradient, 1.2, minSide * 0.06,
            param1: 100, param2: 45, minRadius: (int)(minSide*0.16), maxRadius: (int)(minSide*0.50));
        CircleSegment? best = null; double bestScore = 12;
        foreach (var circle in circles)
        {
            var cx = circle.Center.X; var cy = circle.Center.Y; var r = circle.Radius;
            if (cx-r < -2 || cy-r < -2 || cx+r > gray.Width+1 || cy+r > gray.Height+1) continue;
            // The sky disk is brighter just inside its rim than the surrounding lens housing.
            // This rejects the larger, dimmer decorative rings often present in phone adapters.
            double contrast = 0; int support = 0;
            for (var a = 0; a < 180; a++)
            {
                var theta = a * Math.PI/90; var dx = Math.Cos(theta); var dy = Math.Sin(theta);
                double Sample(double rr) => gray.At<byte>(Math.Clamp((int)Math.Round(cy+dy*r*rr), 0, gray.Height-1),
                    Math.Clamp((int)Math.Round(cx+dx*r*rr), 0, gray.Width-1));
                var diff = (Sample(0.90)+Sample(0.96))/2 - (Sample(1.04)+Sample(1.09))/2;
                contrast += diff; if (diff > 15) support++;
            }
            var score = contrast/180;
            if (support >= 63 && score > bestScore) { best = circle; bestScore = score; }
        }
        if (best is not CircleSegment found)
            throw new InvalidDataException("Could not confidently locate a complete fisheye disk. Supply SkyMaskOptions.Disk from the camera profile, or select Centered for a centered full-disk photo.");
        return new(found.Center.X/scale, found.Center.Y/scale, found.Radius/scale, source.Width, source.Height);
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; session?.Dispose(); session = null; }
    }

    /// <summary>Enumerates actual DXGI adapter IDs and probes D3D12 support; excludes software adapters.</summary>
    public static MaskHardware ProbeHardware()
    {
        var adapters = new List<GpuAdapter>(); string? message = null;
        if (OperatingSystem.IsWindows())
        {
            IntPtr factory = IntPtr.Zero;
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out factory));
                var enumerate = ComMethod<EnumAdapters>(factory, 12);
                for (uint i = 0; ; i++)
                {
                    var hr = enumerate(factory, i, out var adapter);
                    if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        Marshal.ThrowExceptionForHR(ComMethod<GetDescription>(adapter, 10)(adapter, out var desc));
                        if ((desc.Flags & 2) != 0) continue; // software adapter
                        var deviceIid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
                        if (D3D12CreateDevice(adapter, 0xb000, ref deviceIid, out var device) >= 0)
                        {
                            Marshal.Release(device);
                            adapters.Add(new((int)i, desc.Description, (ulong)desc.DedicatedVideoMemory));
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
            { message = $"GPU probe: {ex.Message}"; }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        }
        else message = "This build targets Windows x64.";
        return new(RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            System.Numerics.Vector.IsHardwareAccelerated, adapters.AsReadOnly(), message);
    }

    private static T ComMethod<T>(IntPtr pointer, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot*IntPtr.Size));
    [DllImport("dxgi.dll", ExactSpelling=true)] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d12.dll", ExactSpelling=true)] private static extern int D3D12CreateDevice(IntPtr adapter, int level, ref Guid iid, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self, out AdapterDescription description);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
}
