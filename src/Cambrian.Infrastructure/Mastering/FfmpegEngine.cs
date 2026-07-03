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
        var coverPath = Path.Combine(work, "cover" + SafeExt(request.CoverArtFileName, ".jpg"));
        var wavPath = Path.Combine(work, "master.wav");
        var mp3Path = Path.Combine(work, "master.mp3");

        try
        {
            await using (var fs = File.Create(inputPath))
                await request.Source.CopyToAsync(fs, ct);
            if (request.CoverArt is not null)
            {
                if (request.CoverArt.CanSeek)
                    request.CoverArt.Position = 0;
                await using var coverFs = File.Create(coverPath);
                await request.CoverArt.CopyToAsync(coverFs, ct);
            }

            double i = request.TargetLufs, tp = request.TargetTruePeakDbtp;
            const double lra = 11.0;

            // Pass 1 — analyze.
            var stderr1 = await RunAsync(
                new[]
                {
                    "-hide_banner", "-nostats",
                    "-i", inputPath,
                    "-af", $"loudnorm=I={F(i)}:TP={F(tp)}:LRA={F(lra)}:print_format=json",
                    "-f", "null",
                    "-",
                },
                ct);
            var m = ParseLoudnorm(stderr1)
                ?? throw new InvalidOperationException("ffmpeg loudnorm pass 1 produced no measurements.");

            // Pass 2 — apply (linear where achievable) → 44.1k / 16-bit WAV.
            var wavArgs = new List<string>
            {
                "-hide_banner", "-nostats", "-y",
                "-i", inputPath,
                "-af",
                $"loudnorm=I={F(i)}:TP={F(tp)}:LRA={F(lra)}:" +
                $"measured_I={m.InputI}:measured_TP={m.InputTp}:measured_LRA={m.InputLra}:" +
                $"measured_thresh={m.InputThresh}:offset={m.TargetOffset}:linear=true:print_format=json",
                "-ar", "44100",
                "-sample_fmt", "s16",
            };
            AddMetadataArgs(wavArgs, request.Metadata);
            wavArgs.Add(wavPath);

            var stderr2 = await RunAsync(
                wavArgs,
                ct);
            var applied = ParseLoudnorm(stderr2);

            // Encode the mastered WAV → 320 kbps MP3 with explicit tags and cover art.
            var mp3Args = new List<string>
            {
                "-hide_banner", "-nostats", "-y",
                "-i", wavPath,
            };
            var hasCover = File.Exists(coverPath) && new FileInfo(coverPath).Length > 0;
            if (hasCover)
                mp3Args.AddRange(new[] { "-i", coverPath });

            mp3Args.AddRange(new[]
            {
                "-map", "0:a:0",
            });
            if (hasCover)
                mp3Args.AddRange(new[] { "-map", "1:v:0" });

            mp3Args.AddRange(new[]
            {
                "-codec:a", "libmp3lame",
                "-ar", "44100",
                "-b:a", "320k",
                "-minrate", "320k",
                "-maxrate", "320k",
                "-bufsize", "320k",
                "-write_id3v2", "1",
                "-id3v2_version", "3",
            });
            if (hasCover)
            {
                mp3Args.AddRange(new[]
                {
                    "-codec:v", "copy",
                    "-disposition:v", "attached_pic",
                    "-metadata:s:v", "title=Album cover",
                    "-metadata:s:v", "comment=Cover (front)",
                });
            }
            AddMetadataArgs(mp3Args, request.Metadata);
            mp3Args.Add(mp3Path);

            await RunAsync(mp3Args, ct);

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
    private async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _opts.Ffmpeg.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

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

    private static void AddMetadataArgs(List<string> args, ReleaseMetadata? metadata)
    {
        if (metadata is null)
            return;

        AddMetadata(args, "title", metadata.Title);
        AddMetadata(args, "artist", metadata.Artist);
        AddMetadata(args, "album", metadata.Album);
        AddMetadata(args, "date", metadata.Date);
        AddMetadata(args, "genre", metadata.Genre);
        AddMetadata(args, "comment", metadata.Comment);
    }

    private static void AddMetadata(List<string> args, string key, string? value)
    {
        var cleaned = CleanMetadataValue(value);
        if (cleaned is null)
            return;

        args.Add("-metadata");
        args.Add($"{key}={cleaned}");
    }

    private static string? CleanMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var chars = value
            .Normalize()
            .Where(c => !char.IsControl(c) || c is '\t' or '\r' or '\n')
            .Select(c => c is '\r' or '\n' ? ' ' : c)
            .ToArray();
        var cleaned = new string(chars).Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length <= 250 ? cleaned : cleaned[..250];
    }

    private static string SafeExt(string? name, string fallback = ".audio")
    {
        var ext = Path.GetExtension(name ?? "");
        return string.IsNullOrWhiteSpace(ext) || ext.Length > 6 ? fallback : ext;
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
