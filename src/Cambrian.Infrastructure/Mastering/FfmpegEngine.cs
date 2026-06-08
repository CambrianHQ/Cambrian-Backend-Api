using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Mastering;

/// <summary>
/// Fallback mastering engine: ffmpeg two-pass EBU R128 loudnorm to −14 LUFS /
/// −1 dBTP true peak, producing a 44.1k/16-bit WAV and a 320 kbps MP3. Fully
/// functional with no external service — this is the engine that runs until RoEx
/// is live. Loudness/peak normalization + transparent limiting only; it never
/// alters anything for the purpose of defeating detection/provenance.
/// </summary>
public sealed class FfmpegEngine : IMasteringEngine
{
    private readonly MasteringOptions _opts;
    private readonly ILogger<FfmpegEngine> _logger;

    public FfmpegEngine(IOptions<MasteringOptions> opts, ILogger<FfmpegEngine> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public string Name => "ffmpeg";
    public bool RequiresApproval => false;

    public async Task<MasteringEngineResult> MasterAsync(MasteringEngineRequest request, CancellationToken ct = default)
    {
        if (request.Source is null)
            throw new InvalidOperationException("FfmpegEngine requires a source stream.");

        var work = Path.Combine(Path.GetTempPath(), "rr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var inputPath = Path.Combine(work, "in" + SafeExt(request.SourceFileName));
        var wavPath = Path.Combine(work, "master.wav");
        var mp3Path = Path.Combine(work, "master.mp3");

        try
        {
            await using (var fs = File.Create(inputPath))
                await request.Source.CopyToAsync(fs, ct);

            double i = request.TargetLufs, tp = request.TargetTruePeakDbtp;
            const double lra = 11.0;

            // Pass 1 — analyze.
            var stderr1 = await RunAsync(
                $"-hide_banner -nostats -i \"{inputPath}\" -af loudnorm=I={F(i)}:TP={F(tp)}:LRA={F(lra)}:print_format=json -f null -",
                ct);
            var m = ParseLoudnorm(stderr1)
                ?? throw new InvalidOperationException("ffmpeg loudnorm pass 1 produced no measurements.");

            // Pass 2 — apply (linear where achievable) → 44.1k / 16-bit WAV.
            var stderr2 = await RunAsync(
                $"-hide_banner -nostats -y -i \"{inputPath}\" -af " +
                $"loudnorm=I={F(i)}:TP={F(tp)}:LRA={F(lra)}:" +
                $"measured_I={m.InputI}:measured_TP={m.InputTp}:measured_LRA={m.InputLra}:" +
                $"measured_thresh={m.InputThresh}:offset={m.TargetOffset}:linear=true:print_format=json " +
                $"-ar 44100 -sample_fmt s16 \"{wavPath}\"",
                ct);
            var applied = ParseLoudnorm(stderr2);

            // Encode the mastered WAV → 320 kbps MP3.
            await RunAsync($"-hide_banner -nostats -y -i \"{wavPath}\" -ar 44100 -b:a 320k \"{mp3Path}\"", ct);

            var wav = await File.ReadAllBytesAsync(wavPath, ct);
            var mp3 = await File.ReadAllBytesAsync(mp3Path, ct);

            _logger.LogInformation(
                "EVENT: FfmpegMasterDone inputLufs:{In} outputLufs:{Out} outputTp:{Tp}",
                m.InputI, applied?.OutputI, applied?.OutputTp);

            return new MasteringEngineResult
            {
                Wav = wav,
                Mp3 = mp3,
                InputLufs = TryDouble(m.InputI),
                OutputLufs = TryDouble(applied?.OutputI) ?? i,
                OutputTruePeakDbtp = TryDouble(applied?.OutputTp) ?? tp,
            };
        }
        finally
        {
            TryCleanup(work);
        }
    }

    public Task<MasteringEngineResult> FinalizeAsync(MasteringEngineRequest request, string engineRef, CancellationToken ct = default)
        => throw new NotSupportedException("FfmpegEngine masters in one shot; there is no approval/finalize step.");

    // ── ffmpeg process ──
    private async Task<string> RunAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _opts.Ffmpeg.FfmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start ffmpeg ('{_opts.Ffmpeg.FfmpegPath}'). Is ffmpeg installed and on PATH?", ex);
        }

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_opts.JobTimeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"ffmpeg exceeded {_opts.JobTimeoutSeconds}s and was killed.");
        }

        var stderr = await stderrTask;
        await stdoutTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {Tail(stderr)}");

        return stderr;
    }

    // loudnorm prints a JSON object to stderr; grab the last {...} block.
    private static LoudnormJson? ParseLoudnorm(string stderr)
    {
        var start = stderr.LastIndexOf('{');
        var end = stderr.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(stderr.Substring(start, end - start + 1));
            var r = doc.RootElement;
            return new LoudnormJson
            {
                InputI = Str(r, "input_i"),
                InputTp = Str(r, "input_tp"),
                InputLra = Str(r, "input_lra"),
                InputThresh = Str(r, "input_thresh"),
                TargetOffset = Str(r, "target_offset"),
                OutputI = Str(r, "output_i"),
                OutputTp = Str(r, "output_tp"),
            };
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static double? TryDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string F(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);

    private static string SafeExt(string? name)
    {
        var ext = Path.GetExtension(name ?? "");
        return string.IsNullOrWhiteSpace(ext) || ext.Length > 6 ? ".audio" : ext;
    }

    private static string Tail(string s) => s.Length <= 500 ? s : s[^500..];

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class LoudnormJson
    {
        public string? InputI, InputTp, InputLra, InputThresh, TargetOffset, OutputI, OutputTp;
    }
}
