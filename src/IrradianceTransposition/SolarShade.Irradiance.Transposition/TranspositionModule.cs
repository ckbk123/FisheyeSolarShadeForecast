namespace SolarShade.Irradiance.Transposition;

/// <summary>Horizontal interval means to unshaded panel means. Solar callback receives exact offset-aware
/// quadrature instants; do not substitute solar positions at the provider label for these instants.</summary>
public static partial class TranspositionModule
{
    /// <summary>Simple 0/1 export entry point. Cancellation returns 0; use ComputeToWorkbook for diagnostics.</summary>
    public static int TryExport(IReadOnlyList<IrradianceSample> samples, PanelOrientation panel,
        Func<DateTimeOffset, SunPosition> solarPosition, SamplingWindow window, string outputXlsx,
        DiffuseModel model = DiffuseModel.PerezDriesse, TranspositionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try { return ComputeToWorkbook(samples, panel, solarPosition, window, outputXlsx, model, options, cancellationToken).Status; }
        catch (OperationCanceledException) { return 0; }
    }

    /// <summary>Status 1 on complete success, 0 on failure; no partial rows are returned. Cancellation propagates.
    /// Assumes constant DNI during daylight and constant DHI over each interval, not measured sub-hour weather.</summary>
    public static TranspositionResult Compute(IReadOnlyList<IrradianceSample> samples, PanelOrientation panel,
        Func<DateTimeOffset, SunPosition> solarPosition, SamplingWindow window,
        DiffuseModel model = DiffuseModel.PerezDriesse, TranspositionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        try
        {
            ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(solarPosition);
            ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(options.TimeZone);
            panel.Validate(); window.Validate();
            Guard.Range(options.MinimumMeanCosine, 1e-12, 1, nameof(options.MinimumMeanCosine));
            Guard.Range(options.MaximumInferredDni, 1, 10000, nameof(options.MaximumInferredDni));
            if (options.MaxDegreeOfParallelism < 0) throw new ArgumentOutOfRangeException(nameof(options.MaxDegreeOfParallelism));
            if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
            if (samples.Count == 0) throw new ArgumentException("No irradiance samples supplied.");
            for (int i = 0; i < samples.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guard.Irradiance(samples[i].DirectHorizontal, nameof(IrradianceSample.DirectHorizontal));
                Guard.Irradiance(samples[i].DiffuseHorizontal, nameof(IrradianceSample.DiffuseHorizontal));
                if (i > 0 && samples[i].TimestampUtc <= samples[i - 1].TimestampUtc)
                    throw new ArgumentException("Timestamps must be unique and strictly increasing by UTC instant.");
            }
            var result = new TransposedSample[samples.Count];
            var kernel = new SkyTransposition.PanelKernel(panel);
            void Row(int i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { result[i] = ConvertRow(samples[i], panel, kernel, solarPosition, window, model, options, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { throw new InvalidDataException($"Row {i + 1} ({samples[i].TimestampUtc:O}): {ex.Message}", ex); }
            }
            // Scalar double math works on older CPUs. Parallelism is opt-in because callbacks may not be thread safe.
            int parallelism = options.MaxDegreeOfParallelism == 0
                ? Math.Min(8, HardwareCapabilities.Detect().RecommendedConcurrency)
                : options.MaxDegreeOfParallelism;
            if (parallelism == 1 || samples.Count < 128)
                for (int i = 0; i < samples.Count; i++) Row(i);
            else Parallel.For(0, samples.Count, new ParallelOptions
            { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, Row);
            int lowSun = result.Count(r => r.Flags.HasFlag(TranspositionFlags.LowSun));
            int nightDiffuse = result.Count(r => r.Flags.HasFlag(TranspositionFlags.NightDiffuseIsotropic));
            return new(1, $"Converted {result.Length} rows. Low-Sun rows: {lowSun}; rows using isotropic night/twilight diffuse: {nightDiffuse}.", Array.AsReadOnly(result));
        }
        catch (OperationCanceledException) { throw; }
        catch (AggregateException ex)
        {
            var errors = ex.Flatten().InnerExceptions;
            if (errors.Any(e => e is OperationCanceledException)) throw new OperationCanceledException(cancellationToken);
            return new(0, errors[0].Message, Array.Empty<TransposedSample>());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(0, ex.Message, Array.Empty<TransposedSample>()); }
    }

    /// <summary>Compute everything before atomic three-column XLSX export. Failed computation preserves any existing file.</summary>
    public static TranspositionResult ComputeToWorkbook(IReadOnlyList<IrradianceSample> samples, PanelOrientation panel,
        Func<DateTimeOffset, SunPosition> solarPosition, SamplingWindow window, string outputXlsx,
        DiffuseModel model = DiffuseModel.PerezDriesse, TranspositionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var result = Compute(samples, panel, solarPosition, window, model, options, cancellationToken);
        if (!result.Succeeded) return result;
        try
        {
            PlaneWorkbook.Write(outputXlsx, result.Samples, panel, window, model, options, cancellationToken);
            return result with { OutputPath = Path.GetFullPath(outputXlsx) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Xml.XmlException)
        { return new(0, $"Export failed: {ex.Message}", Array.Empty<TransposedSample>()); }
    }

    private static TransposedSample ConvertRow(IrradianceSample input, PanelOrientation panel,
        SkyTransposition.PanelKernel kernel, Func<DateTimeOffset, SunPosition> solarPosition,
        SamplingWindow window, DiffuseModel model, TranspositionOptions options, CancellationToken ct)
    {
        // Horizontal-to-horizontal is the supplied measurement itself, including low-Sun intervals.
        // No DNI inference or empirical diffuse regularization is necessary in this exact boundary case.
        if (panel.TiltDegrees == 0)
            return new(input.TimestampUtc, input.DirectHorizontal, input.DiffuseHorizontal,
                input.DirectHorizontal > 0 ? 1 : null, input.DiffuseHorizontal > 0 ? 1 : null,
                null, null, TranspositionFlags.HorizontalIdentity);
        if (input.DirectHorizontal == 0 && input.DiffuseHorizontal == 0)
            return new(input.TimestampUtc, 0, 0, null, null, 0, null, TranspositionFlags.None);
        // Rent one small buffer per active worker instead of allocating one object per quadrature point.
        var positions = System.Buffers.ArrayPool<SunPosition>.Shared.Rent(window.Samples);
        try
        {
            double cosineSum = 0;
            for (int j = 0; j < window.Samples; j++)
            {
                ct.ThrowIfCancellationRequested();
                var time = input.TimestampUtc.AddMinutes(window.StartOffsetMinutes + (j + .5) * window.DurationMinutes / window.Samples);
                var sun = solarPosition(time); sun.Validate(); positions[j] = sun;
                if (sun.ZenithDegrees < 90) cosineSum += Math.Cos(sun.ZenithDegrees * SkyTransposition.Radians);
            }
            double meanCosine = cosineSum / window.Samples;
            double dni = 0;
            var flags = TranspositionFlags.None;
            if (input.DirectHorizontal > 0)
            {
                flags |= TranspositionFlags.InferredDni;
                dni = InferDni(input.DirectHorizontal, meanCosine, options);
            }
            double direct = 0, diffuse = 0;
            for (int j = 0; j < window.Samples; j++)
            {
                ct.ThrowIfCancellationRequested();
                var time = input.TimestampUtc.AddMinutes(window.StartOffsetMinutes + (j + .5) * window.DurationMinutes / window.Samples);
                var sun = positions[j];
                var value = kernel.Evaluate(sun, sun.ZenithDegrees < 90 ? dni : 0, input.DiffuseHorizontal,
                    SkyTransposition.ExtraterrestrialDni(time), model);
                direct += value.Direct / window.Samples; diffuse += value.SkyDiffuse / window.Samples;
                flags |= value.Flags;
            }
            return new(input.TimestampUtc, direct, diffuse,
                input.DirectHorizontal > 0 ? direct / input.DirectHorizontal : null,
                input.DiffuseHorizontal > 0 ? diffuse / input.DiffuseHorizontal : null,
                dni, meanCosine, flags);
        }
        finally { System.Buffers.ArrayPool<SunPosition>.Shared.Return(positions); }
    }

    private static double InferDni(double directHorizontal, double meanCosine, TranspositionOptions options)
    {
        if (directHorizontal == 0) return 0;
        if (meanCosine < options.MinimumMeanCosine)
            throw new InvalidDataException("Positive direct-horizontal irradiance without sufficient daylight; check interval and solar timestamps.");
        double dni = directHorizontal / meanCosine;
        if (dni > options.MaximumInferredDni)
            throw new InvalidDataException(FormattableString.Invariant($"Inferred DNI {dni:F3} W/m² exceeds configured {options.MaximumInferredDni} W/m²; check source/interval consistency or supply native DNI to EvaluateDni."));
        return dni;
    }
}
