using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using SolarShade.SkyPhotoMasking;

return SkyMaskValidator.MainEntry(args);

public static class SkyMaskValidator
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static string SourceDirectory([CallerFilePath] string path = "") => Directory.Exists(Path.GetDirectoryName(path))
        ? Path.GetDirectoryName(path)! : AppContext.BaseDirectory;

    public static int MainEntry(string[] args)
    {
        try
        {
            string? Arg(string name) { var i = Array.IndexOf(args, name); return i < 0 ? null : i+1 < args.Length ? args[i+1] : throw new ArgumentException($"Missing value for {name}"); }
            var folder = SourceDirectory();
            var image = Arg("--image") ?? Path.Combine(folder, "example_image.jpg");
            var output = Arg("--output") ?? folder;
            var options = new SkyMaskOptions
            {
                InputSize = int.Parse(Arg("--size") ?? "1024"),
                Acceleration = Enum.Parse<MaskAcceleration>(Arg("--acceleration") ?? "Auto", true),
                IncludeDiagnostics = args.Contains("--diagnostics"),
                ModelsDirectory = Arg("--models") ?? Path.Combine(AppContext.BaseDirectory, "SkyPhotoModels"),
                DiskDetection = args.Contains("--centered") ? DiskDetection.Centered : DiskDetection.Auto
            };
            var models = Arg("--model") is string m ? new[] { Enum.Parse<SkyModel>(m, true) } : Enum.GetValues<SkyModel>();
            return Validate(image, output, options, models);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    /// <summary>Writes a pure mask and separately labeled comparison image per model beside this validator by default.</summary>
    public static int Validate(string imagePath, string? outputDirectory = null, SkyMaskOptions? options = null, SkyModel[]? models = null)
    {
        var output = Path.GetFullPath(outputDirectory ?? SourceDirectory()); Directory.CreateDirectory(output);
        using var masker = new SkyPhotoMasker(options);
        Console.WriteLine(JsonSerializer.Serialize(masker.Hardware, Json));
        var records = new List<object>(); var previews = new List<string>(); var failures = 0;
        foreach (var model in models ?? Enum.GetValues<SkyModel>())
        {
            try
            {
                Console.WriteLine($"Running {model}... loading/compiling on first call.");
                var cold = masker.CreateMaskDetailed(imagePath, model);
                var warm = masker.CreateMaskDetailed(imagePath, model);
                var stem = $"{Path.GetFileNameWithoutExtension(imagePath)}-{Path.GetFileNameWithoutExtension(SkyPhotoMasker.ModelFilename(model))}";
                var maskPath = Path.Combine(output, stem + ".png");
                File.WriteAllBytes(maskPath, warm.Png);
                using var mask = Cv2.ImDecode(warm.Png, ImreadModes.Grayscale);
                using var invalid = new Mat();
                Cv2.InRange(mask, new Scalar(1), new Scalar(254), invalid);
                if (Cv2.CountNonZero(invalid) != 0 || mask.Width != warm.Width || mask.Height != warm.Height)
                    throw new InvalidDataException("Output must contain only 0/255 and retain source dimensions.");
                if (!cold.Png.SequenceEqual(warm.Png)) throw new InvalidDataException("Repeated inference changed the output PNG.");
                var preview = Path.Combine(output, stem + "-labeled.png");
                WriteLabeled(mask, warm, preview); previews.Add(preview);
                if (warm.Probabilities is not null)
                {
                    var diagnostic = Path.Combine(output, "Diagnostics"); Directory.CreateDirectory(diagnostic);
                    File.WriteAllBytes(Path.Combine(diagnostic, stem + "-input.png"), warm.InputRgbPng!);
                    using var writer = new BinaryWriter(File.Create(Path.Combine(diagnostic, stem + ".f32")));
                    foreach (var value in warm.Probabilities) writer.Write(value);
                }
                records.Add(new { model, maskPath, cold, warm });
                Console.WriteLine($"{model}: {warm.ExecutionProvider}; cold {cold.TotalMilliseconds:F0} ms; warm {warm.TotalMilliseconds:F0} ms; inference {warm.InferenceMilliseconds:F0} ms; {maskPath}");
            }
            catch (Exception ex)
            {
                failures++; records.Add(new { model, error = ex.ToString() }); Console.Error.WriteLine($"{model}: {ex}");
            }
        }
        File.WriteAllText(Path.Combine(output, "validation-report.json"), JsonSerializer.Serialize(new
        { utc = DateTimeOffset.UtcNow, imagePath = Path.GetFullPath(imagePath), hardware = masker.Hardware, failures, records }, Json));
        if (previews.Count == 4)
        {
            using var comparison = new Mat(1000,900,MatType.CV_8UC1,Scalar.Black);
            for (var i = 0; i < previews.Count; i++)
            {
                using var preview = Cv2.ImRead(previews[i],ImreadModes.Grayscale);
                using var small = new Mat(); Cv2.Resize(preview,small,new Size(450,500),interpolation:InterpolationFlags.Nearest);
                using var roi = new Mat(comparison,new Rect(i%2*450,i/2*500,450,500)); small.CopyTo(roi);
            }
            Cv2.ImWrite(Path.Combine(output,"four-model-comparison.png"),comparison);
        }
        return failures == 0 ? 0 : 1;
    }

    private static void WriteLabeled(Mat mask, SkyMaskResult result, string path)
    {
        using var crop = new Mat(mask,new Rect(result.Crop.X,result.Crop.Y,result.Crop.Width,result.Crop.Height));
        using var small = new Mat(); Cv2.Resize(crop, small, new Size(900,900), interpolation: InterpolationFlags.Nearest);
        // Label occupies a separate border; the pure mask remains suitable for pixel-coordinate shading.
        using var labeled = new Mat(small.Height+100, Math.Max(700, small.Width), MatType.CV_8UC1, Scalar.Black);
        using (var roi = new Mat(labeled, new Rect(0, 100, small.Width, small.Height))) small.CopyTo(roi);
        Cv2.PutText(labeled, $"{result.Model} | {result.InputSize}px | white = sky", new Point(12, 32), HersheyFonts.HersheySimplex, 1.0, Scalar.White, 2, LineTypes.Link8);
        Cv2.PutText(labeled, $"Warm: {result.TotalMilliseconds:F0} ms | {result.ExecutionProvider.Split(':')[0]}", new Point(12, 72), HersheyFonts.HersheySimplex, .9, Scalar.White, 2, LineTypes.Link8);
        Cv2.ImWrite(path, labeled);
    }
}
