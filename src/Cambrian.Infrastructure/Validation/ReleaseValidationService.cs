using System.Text.RegularExpressions;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;

namespace Cambrian.Infrastructure.Validation;

/// <summary>
/// Reads audio tags with TagLibSharp and cover art with ImageSharp. Honest
/// quality checks only — it flags problems and placeholder junk; it never edits
/// anything to defeat detection or provenance.
/// </summary>
public sealed class ReleaseValidationService : IReleaseValidationService
{
    private const int MinArtworkEdge = 3000;

    private static readonly Regex JunkTitle = new(
        @"^\s*(untitled|track\s*\d*|demo|export|final|new\s*recording|audio|master|mixdown|\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Distributors reject AI tool names as the artist credit (accuracy, not concealment).
    private static readonly string[] ToolNames =
    {
        "suno", "udio", "musicgen", "riffusion", "stable audio", "stableaudio",
        "ai", "a.i.", "artificial intelligence", "chatgpt", "openai",
    };

    private readonly MasteringOptions _options;
    private readonly ILogger<ReleaseValidationService> _logger;

    public ReleaseValidationService(IOptions<MasteringOptions> options, ILogger<ReleaseValidationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public MetadataValidationResult ValidateMetadata(Stream audio, string fileName)
    {
        var r = new MetadataValidationResult();
        try
        {
            if (audio.CanSeek) audio.Position = 0;
            var name = string.IsNullOrWhiteSpace(fileName) ? "audio.mp3" : fileName;
            using var tagFile = TagLib.File.Create(new StreamFileAbstraction(name, audio));
            var tag = tagFile.Tag;
            var duration = tagFile.Properties.Duration;

            if (duration > TimeSpan.Zero)
            {
                r.DecodableAudio = true;
                r.DurationSeconds = duration.TotalSeconds;

                var minSeconds = Math.Max(3, _options.MinDurationSeconds);
                var maxSeconds = Math.Max(minSeconds, _options.MaxDurationSeconds);
                if (duration.TotalSeconds < minSeconds)
                    r.Issues.Add($"Audio must be at least {minSeconds} seconds.");
                if (duration.TotalSeconds > maxSeconds)
                    r.Issues.Add($"Release Ready currently supports tracks up to {maxSeconds / 60} minutes.");
            }
            else
            {
                r.Issues.Add("Could not determine audio duration — re-export the file and try again.");
            }

            r.Title = Clean(tag.Title);
            r.Artist = Clean(tag.FirstPerformer);
            r.Album = Clean(tag.Album);

            if (string.IsNullOrWhiteSpace(r.Title))
                r.Issues.Add("Missing track title.");
            else if (JunkTitle.IsMatch(r.Title))
            {
                r.Issues.Add("Title looks like a placeholder — use a specific, descriptive title.");
                r.Stripped.Add("title");
            }

            if (string.IsNullOrWhiteSpace(r.Artist))
                r.Issues.Add("Missing artist name.");
            else if (IsToolName(r.Artist))
            {
                r.Issues.Add("Artist is an AI tool name — distributors reject this. Use your real artist "
                    + "name and disclose the tool in the AI-disclosure section.");
                r.Stripped.Add("artist");
            }

            if (string.IsNullOrWhiteSpace(r.Album))
                r.Issues.Add("Missing release or album name.");

            r.Passed = r.Issues.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EVENT: MetadataValidationError file:{File}", fileName);
            r.Passed = false;
            r.DecodableAudio = false;
            r.Issues.Add("Could not read audio metadata — re-export the file and try again.");
        }
        finally
        {
            if (audio.CanSeek) audio.Position = 0;
        }
        return r;
    }

    public ArtworkValidationResult ValidateArtwork(Stream? image, string? fileName)
    {
        var r = new ArtworkValidationResult();
        if (image is null)
        {
            r.Provided = false;
            r.Passed = false;
            r.Issues.Add("No artwork provided. Add a 3000×3000 RGB JPEG or PNG.");
            return r;
        }

        r.Provided = true;
        try
        {
            if (image.CanSeek) image.Position = 0;
            var info = Image.Identify(image);

            r.Width = info.Width;
            r.Height = info.Height;
            r.Format = info.Metadata.DecodedImageFormat?.Name;

            var fmt = r.Format ?? "";
            if (!fmt.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
                && !fmt.Equals("PNG", StringComparison.OrdinalIgnoreCase))
                r.Issues.Add($"Unsupported artwork format ({(string.IsNullOrEmpty(fmt) ? "unknown" : fmt)}). Use JPEG or PNG.");

            if (info.Width < MinArtworkEdge || info.Height < MinArtworkEdge)
                r.Issues.Add($"Artwork is {info.Width}×{info.Height}; it must be at least {MinArtworkEdge}×{MinArtworkEdge}.");

            if (info.Width != info.Height)
                r.Issues.Add("Artwork must be square (1:1).");

            r.Passed = r.Issues.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EVENT: ArtworkValidationError file:{File}", fileName);
            r.Passed = false;
            r.Issues.Add("Could not read the artwork — make sure it's a valid JPEG or PNG.");
        }
        return r;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool IsToolName(string artist)
    {
        var a = artist.Trim().ToLowerInvariant();
        return ToolNames.Any(t => a == t || a.Contains(t));
    }

    /// <summary>Lets TagLib read tags straight from an in-memory stream (TagLibSharp
    /// ships no stream abstraction). The caller owns the stream lifetime.</summary>
    private sealed class StreamFileAbstraction : TagLib.File.IFileAbstraction
    {
        private readonly Stream _stream;
        public StreamFileAbstraction(string name, Stream stream)
        {
            Name = name;
            _stream = stream;
        }

        public string Name { get; }
        public Stream ReadStream => _stream;
        public Stream WriteStream => _stream;
        public void CloseStream(Stream stream) { /* caller owns the stream */ }
    }
}
