using System.Diagnostics;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Infrastructure.Mastering;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;

namespace Cambrian.Api.Tests;

[Trait("Category", "ReleaseReady")]
public sealed class FfmpegEngineMetadataTests
{
    private const string VerificationCommand =
        "dotnet test Cambrian.sln --configuration Release --no-build --filter \"FullyQualifiedName~FfmpegEngineMetadataTests\"";

    private readonly ITestOutputHelper _output;

    public FfmpegEngineMetadataTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MasterAsync_EmbedsMp3MetadataAndCover_WhenFfmpegIsAvailable()
    {
        if (!FfmpegAvailable())
        {
            var message = "ffmpeg verification skipped: ffmpeg was not found on PATH. "
                + "Install ffmpeg and rerun: " + VerificationCommand;
            _output.WriteLine(message);
            Console.WriteLine(message);
            return;
        }

        _output.WriteLine("ffmpeg verification ran. Command: " + VerificationCommand);
        Console.WriteLine("ffmpeg verification ran. Command: " + VerificationCommand);

        var engine = new FfmpegEngine(
            Options.Create(new MasteringOptions { JobTimeoutSeconds = 90 }),
            NullLogger<FfmpegEngine>.Instance);
        await using var audio = new MemoryStream(CreateSineWav(TimeSpan.FromSeconds(6)));
        await using var cover = new MemoryStream(CreateCoverPng(3000));

        var result = await engine.MasterAsync(new MasteringEngineRequest
        {
            Source = audio,
            SourceFileName = "source.wav",
            CoverArt = cover,
            CoverArtFileName = "cover.png",
            Metadata = new ReleaseMetadata
            {
                Title = "Release Title -map 1",
                Artist = "Release Artist",
                Album = "Release Album",
                Date = "2026",
                Genre = "Hip-Hop",
                Comment = "Release Ready Beta",
            },
        });

        result.Mp3.Should().NotBeNull();
        result.Wav.Should().NotBeNull();

        var mp3Path = Path.Combine(Path.GetTempPath(), $"rr-meta-{Guid.NewGuid():N}.mp3");
        var wavPath = Path.Combine(Path.GetTempPath(), $"rr-meta-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(mp3Path, result.Mp3!);
            await File.WriteAllBytesAsync(wavPath, result.Wav!);

            using var mp3 = TagLib.File.Create(mp3Path);
            mp3.Tag.Title.Should().Be("Release Title -map 1");
            mp3.Tag.FirstPerformer.Should().Be("Release Artist");
            mp3.Tag.Album.Should().Be("Release Album");
            mp3.Tag.Pictures.Should().NotBeEmpty("MP3 release exports must embed cover art");
            mp3.Properties.AudioBitrate.Should().BeInRange(300, 330, "MP3 release exports should be 320 kbps CBR");

            using var wav = TagLib.File.Create(wavPath);
            wav.Properties.AudioSampleRate.Should().Be(44100);
            wav.Properties.BitsPerSample.Should().Be(16);
            wav.Properties.AudioChannels.Should().Be(1);
        }
        finally
        {
            try { File.Delete(mp3Path); } catch { }
            try { File.Delete(wavPath); } catch { }
        }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(5_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] CreateSineWav(TimeSpan duration)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        var samples = Math.Max(1, (int)(duration.TotalSeconds * sampleRate));
        var dataSize = samples * channels * bitsPerSample / 8;
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + dataSize);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(dataSize);

            for (var i = 0; i < samples; i++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * 0.2);
                writer.Write(sample);
            }
        }
        return ms.ToArray();
    }

    private static byte[] CreateCoverPng(int edge)
    {
        using var image = new Image<Rgba32>(edge, edge, new Rgba32(24, 80, 120, 255));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
