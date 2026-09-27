using System.Text.Json;
using SolarShade.Calibration.Solver;
using Xunit;
using Xunit.Abstractions;

namespace SolarShade.Calibration.Tests;

public sealed class PortHardwarePolicyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(16, 0, 1)]
    [InlineData(16, -1, 1)]
    [InlineData(16, 512, 1)]
    [InlineData(16, 640, 1)]
    [InlineData(16, 768, 2)]
    [InlineData(16, 896, 3)]
    [InlineData(16, 2048, 12)]
    [InlineData(4, 8192, 4)]
    [InlineData(1, 8192, 1)]
    [InlineData(0, 8192, 1)]
    public void WorkerBudget_RespectsMemoryAndCpuCaps(int processors, long memoryMiB, int expected) =>
        Assert.Equal(expected, PortHardwarePolicy.RecommendWorkers(processors, memoryMiB * 1024 * 1024));

    [Fact]
    public void ResourceReport_UsesTheWorkerPolicy()
    {
        var hardware = PortHardwarePolicy.Detect();
        output.WriteLine(JsonSerializer.Serialize(hardware, new JsonSerializerOptions { WriteIndented = true }));
        Assert.InRange(hardware.RecommendedImageWorkers, 1, Math.Min(12, hardware.LogicalProcessorCount));
        Assert.Equal(PortHardwarePolicy.RecommendWorkers(hardware.LogicalProcessorCount,
            hardware.WorkerMemoryBudgetBytes), hardware.RecommendedImageWorkers);
    }
}
