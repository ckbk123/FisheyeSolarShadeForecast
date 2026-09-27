using System.Diagnostics;
using System.Text.Json;
using SolarShade.Shading;
using SolarShade.Irradiance;
using SolarShade.Core.Models;

internal static class Convergence
{
    private sealed record ErrorSummary(double MaxLossBoundError,double MeanLossBoundError,double P95LossBoundError);
    public static void Run(string output,IrradianceWorkbook input,SolarSite site,SkyMask mask,CalibrationResult calibration,CameraPose pose,ImageDisk disk)
    {
        var solar=new SolarPositionModule(site);
        var referenceSequence=solar.Prepare(input.Samples,TimeSampling.PrecedingHour(240));
        var reference=new SkyShadingModule(mask,calibration,pose,new(){DiskSamples=2048,DiffuseSamples=1048576},disk).Evaluate(referenceSequence);
        var dense=new SkyShadingModule(mask,calibration,pose,new(){DiskSamples=4096,DiffuseSamples=2097152},disk).Evaluate(solar.Prepare(input.Samples,TimeSampling.PrecedingHour(480)));
        static ErrorSummary Error(ShadingResult result,ShadingResult expected)
        {
            var differences=result.Rows.Zip(expected.Rows).Where(pair=>pair.First.Status!="SkippedZeroDirect"&&pair.Second.Status!="PositiveDirectBelowHorizon")
                .Select(pair=>Math.Max(Math.Abs(pair.First.Direct.ShadingLowerBound-pair.Second.Direct.ShadingLowerBound),Math.Abs(pair.First.Direct.ShadingUpperBound-pair.Second.Direct.ShadingUpperBound))).ToArray();
            return new(differences.Max(),differences.Average(),differences.Order().ElementAt((int)(0.95*(differences.Length-1))));
        }
        var rows=new List<object>();
        foreach(int timeSamples in new[]{30,60,120})
        {
            var sequence=solar.Prepare(input.Samples,TimeSampling.PrecedingHour(timeSamples));
            foreach(int diskSamples in new[]{64,128,256})
            {
                var shader=new SkyShadingModule(mask,calibration,pose,new(){DiskSamples=diskSamples},disk);
                var value=shader.Evaluate(sequence);
                rows.Add(new{TimeSamples=timeSamples,DiskSamples=diskSamples,Error=Error(value,dense)});
            }
        }
        var standard=new SkyShadingModule(mask,calibration,pose,new(),disk);
        var exact=new SkyShadingModule(mask,calibration,pose,new(){UseUniformRegionShortcut=false},disk);
        var seq=solar.Prepare(input.Samples,TimeSampling.PrecedingHour());
        var shortcutError=Error(standard.Evaluate(seq),exact.Evaluate(seq));
        if(shortcutError.MaxLossBoundError>1e-10||Error(standard.Evaluate(seq),dense).MaxLossBoundError>0.01||Error(reference,dense).MaxLossBoundError>0.001)
            throw new InvalidDataException("Shading numerical validation failed.");
        File.WriteAllText(Path.Combine(output,"convergence.json"),JsonSerializer.Serialize(new
        {
            NumericalAcceptance="Maximum absolute error in either direct-loss bound <= 0.01 (one percentage point), compared with 480 temporal x 4096 disk samples per hour. This is numerical convergence, not physical calibration accuracy.",
            ReferenceStability=Error(reference,dense),ShortcutError=shortcutError,
            DiffuseStandard=standard.Diffuse,DiffuseDense=dense.Diffuse,Configurations=rows
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Convergence written.");
    }
}
