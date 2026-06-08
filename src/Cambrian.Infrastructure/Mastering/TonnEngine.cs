using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Mastering;

/// <summary>
/// Primary mastering engine (RoEx Tonn API). Produces a preview the creator
/// approves before the final master is retrieved. URL-based: RoEx fetches the
/// source from a signed URL, so callers must supply
/// <see cref="MasteringEngineRequest.SourceUrl"/>.
///
/// <para>
/// The exact RoEx request/response field names are confirmed against their live
/// API once the key is provisioned. Until then this engine is config-gated off
/// (ffmpeg is the default) and refuses to run without <c>Mastering:Tonn:ApiKey</c>,
/// so the day never blocks on RoEx approval.
/// </para>
/// </summary>
public sealed class TonnEngine : IMasteringEngine
{
    private readonly HttpClient _http;
    private readonly MasteringOptions _opts;
    private readonly ILogger<TonnEngine> _logger;

    public TonnEngine(HttpClient http, IOptions<MasteringOptions> opts, ILogger<TonnEngine> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
    }

    public string Name => "tonn";
    public bool RequiresApproval => true;

    public async Task<MasteringEngineResult> MasterAsync(MasteringEngineRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(request.SourceUrl))
            throw new InvalidOperationException("TonnEngine requires a signed SourceUrl for RoEx to fetch.");

        var payload = new
        {
            masteringData = new
            {
                trackData = new[]
                {
                    new
                    {
                        trackURL = request.SourceUrl,
                        musicalStyle = _opts.Tonn.DefaultMusicalStyle,
                        desiredLoudness = "MEDIUM",
                        sampleRate = "44100",
                    },
                },
            },
        };

        var resp = await PostAsync("/masteringpreview", payload, ct);
        var taskId = ReadString(resp, "mastering_task_id")
            ?? throw new InvalidOperationException("RoEx mastering preview returned no task id.");

        var previewUrl = await PollAsync("/retrievepreview", taskId, ct);
        _logger.LogInformation("EVENT: TonnPreviewReady taskId:{TaskId}", taskId);

        return new MasteringEngineResult
        {
            EngineRef = taskId,
            PreviewUrl = previewUrl,
            AwaitingApproval = true,
        };
    }

    public async Task<MasteringEngineResult> FinalizeAsync(MasteringEngineRequest request, string engineRef, CancellationToken ct = default)
    {
        EnsureConfigured();
        var finalUrl = await PollAsync("/retrievefinalmaster", engineRef, ct);
        var bytes = await _http.GetByteArrayAsync(finalUrl, ct);
        _logger.LogInformation("EVENT: TonnFinalRetrieved taskId:{TaskId} bytes:{Bytes}", engineRef, bytes.Length);

        // RoEx delivers a single mastered file; expose it in the WAV slot.
        return new MasteringEngineResult
        {
            Wav = bytes,
            EngineRef = engineRef,
            OutputLufs = request.TargetLufs,
            OutputTruePeakDbtp = request.TargetTruePeakDbtp,
        };
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_opts.Tonn.ApiKey))
            throw new InvalidOperationException(
                "Tonn (RoEx) is not configured — set Mastering:Tonn:ApiKey or switch Mastering:Engine to 'ffmpeg'.");
    }

    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _opts.Tonn.BaseUrl.TrimEnd('/') + path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("x-api-key", _opts.Tonn.ApiKey);

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"RoEx {path} failed ({(int)resp.StatusCode}): {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // Poll a retrieve endpoint until it returns a download URL or times out.
    private async Task<string> PollAsync(string path, string taskId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_opts.Tonn.PollTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var el = await PostAsync(path, new { masteringData = new { masteringTaskId = taskId } }, ct);
            var url = ReadString(el, "download_url_mastered")
                ?? ReadString(el, "download_url")
                ?? ReadString(el, "preview_url");
            if (!string.IsNullOrWhiteSpace(url)) return url!;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"RoEx {path} did not complete within {_opts.Tonn.PollTimeoutSeconds}s.");
    }

    // RoEx nests result fields under various keys; do a shallow + one-level search.
    private static string? ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        if (el.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString();

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                prop.Value.TryGetProperty(name, out var nested) &&
                nested.ValueKind == JsonValueKind.String)
                return nested.GetString();
        }
        return null;
    }
}
