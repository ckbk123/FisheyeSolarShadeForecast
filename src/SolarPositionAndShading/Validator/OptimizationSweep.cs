using System.Text.Json;
using SolarShade.Shading;

internal static class OptimizationSweep
{
    public static void Run(string output,string inputPath,string maskPath,string calibrationPath,SolarSite site,CameraPose pose,ImageDisk disk)
    {
        var reportPath=Path.Combine(output,"optimization-sweep.json");
        var trials=File.Exists(reportPath)?JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(reportPath))!.Select(e=>(object)e).ToList():new List<object>();int trial=5;
        int completed=trials.Count;
        // Together with the five recorded implementation trials: 50 distinct optimization trials.
        // Every configuration rewrites BOTH workbooks, rereads XLSX/PNG/YAML, and rebuilds the mask context.
        foreach(int time in new[]{30,60,120})foreach(int count in new[]{64,128,256})foreach(int workers in new[]{1,2,4,8,16})
        {
            trial++;
            if(trial<=5+completed)continue;
            var options=new ShadingOptions{DiskSamples=count,MaxDegreeOfParallelism=workers};
            var elapsed=new List<double>();
            for(int repeat=0;repeat<3;repeat++)
            {
                var r=ShadingFilePipeline.Compute(inputPath,maskPath,calibrationPath,66.43,site,
                    Path.Combine(output,"sweep",$"trial-{trial}-run-{repeat}.xlsx"),TimeSampling.PrecedingHour(time),pose,options,disk);
                elapsed.Add(r.TotalMilliseconds);
            }
            var record=new{Trial=trial,TimeSamples=time,DiskSamples=count,Workers=workers,Milliseconds=elapsed,
                MedianMilliseconds=elapsed.Order().ElementAt(1),Meets100ms=elapsed.All(t=>t<100)};
            trials.Add(record);Console.WriteLine(JsonSerializer.Serialize(record));
            File.WriteAllText(Path.Combine(output,"optimization-sweep.json"),JsonSerializer.Serialize(trials,new JsonSerializerOptions{WriteIndented=true}));
        }
    }
}
