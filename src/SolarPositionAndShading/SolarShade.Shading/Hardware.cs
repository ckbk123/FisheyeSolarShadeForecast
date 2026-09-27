using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
namespace SolarShade.Shading;
public sealed record GpuAdapter(int DeviceId, string Name, ulong DedicatedMemoryBytes);
public sealed record MaskHardware(string Architecture, int LogicalProcessors, bool CpuVectorAcceleration, IReadOnlyList<GpuAdapter> DirectMLAdapters, string? ProbeMessage);
public sealed record ShadingHardware(MaskHardware Devices, bool Avx2, bool Avx512, int RecommendedWorkers, string SelectedBackend);
// Device probe adapted from the sibling SkyPhotoMasking module; no ONNX/model dependency.
public static class ShadingHardwareDetector
{
    private static readonly Lazy<ShadingHardware> Cached = new(() => new(ProbeHardware(), Avx2.IsSupported, Avx512F.IsSupported, Math.Max(1, Environment.ProcessorCount / 2), "CPU double-precision spherical quadrature; GPU capabilities reported separately, no GPU compute backend selected."));
    public static ShadingHardware Detect() => Cached.Value;
    /// <summary>Enumerates actual DXGI adapter IDs and probes D3D12 support; excludes software adapters.</summary>
    private static MaskHardware ProbeHardware()
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
                        // NULL output probes feature support without creating a device or initializing queues.
                        if (D3D12CreateDevice(adapter, 0xb000, ref deviceIid, IntPtr.Zero) >= 0)
                        {
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
    [DllImport("d3d12.dll", ExactSpelling=true)] private static extern int D3D12CreateDevice(IntPtr adapter, int level, ref Guid iid, IntPtr device);
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


