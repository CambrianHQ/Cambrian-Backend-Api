using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cambrian.Api.Tests;

[Trait("Category", "ReleaseReady")]
public sealed class ReleaseReadyValidationTests
{
    [Fact]
    public async Task ValidateAndCreate_FakeMp3ImageBytes_RejectsBeforeStorageJobOrCharge()
    {
        var fixture = CreateFixture();
        await using var audio = new MemoryStream(CreateCoverPng(3000));
        await using var artwork = new MemoryStream(CreateCoverPng(3000));

        var act = () => fixture.Service.ValidateAndCreateAsync(Input(audio, "fake.mp3", artwork, "cover.png"));

        var ex = await act.Should().ThrowAsync<ReleaseReadyValidationException>();
        ex.Which.Validation.Metadata.Passed.Should().BeFalse();
        ex.Which.Validation.Metadata.DecodableAudio.Should().BeFalse();
        await AssertNoPersistenceAsync(fixture);
    }

    [Fact]
    public async Task ValidateAndCreate_TinyAudio_RejectsBeforeStorageJobOrCharge()
    {
        var fixture = CreateFixture();
        await using var audio = new MemoryStream(CreateTaggedWav(TimeSpan.FromSeconds(1)));
        await using var artwork = new MemoryStream(CreateCoverPng(3000));

        var act = () => fixture.Service.ValidateAndCreateAsync(Input(audio, "tiny.wav", artwork, "cover.png"));

        var ex = await act.Should().ThrowAsync<ReleaseReadyValidationException>();
        ex.Which.Message.Should().Contain("at least 5 seconds");
        await AssertNoPersistenceAsync(fixture);
    }

    [Fact]
    public async Task ValidateAndCreate_TooLongAudio_RejectsBeforeStorageJobChargeOrFfmpeg()
    {
        var fixture = CreateFixture();
        await using var audio = new MemoryStream(CreateTaggedWav(TimeSpan.FromSeconds(901)));
        await using var artwork = new MemoryStream(CreateCoverPng(3000));

        var act = () => fixture.Service.ValidateAndCreateAsync(Input(audio, "too-long.wav", artwork, "cover.png"));

        var ex = await act.Should().ThrowAsync<ReleaseReadyValidationException>();
        ex.Which.Message.Should().Contain("up to 15 minutes");
        await AssertNoPersistenceAsync(fixture);
    }

    [Fact]
    public async Task ValidateAndCreate_MissingMetadata_RejectsBeforeStorageJobOrCharge()
    {
        var fixture = CreateFixture();
        await using var audio = new MemoryStream(CreateTaggedWav(TimeSpan.FromSeconds(6), artist: null));
        await using var artwork = new MemoryStream(CreateCoverPng(3000));

        var act = () => fixture.Service.ValidateAndCreateAsync(Input(audio, "missing-artist.wav", artwork, "cover.png"));

        var ex = await act.Should().ThrowAsync<ReleaseReadyValidationException>();
        ex.Which.Message.Should().Contain("Missing artist name");
        await AssertNoPersistenceAsync(fixture);
    }

    [Fact]
    public async Task ValidateAndCreate_MissingCoverArt_RejectsBeforeStorageJobOrCharge()
    {
        var fixture = CreateFixture();
        await using var audio = new MemoryStream(CreateTaggedWav(TimeSpan.FromSeconds(6)));

        var act = () => fixture.Service.ValidateAndCreateAsync(Input(audio, "song.wav", artwork: null, artworkName: null));

        var ex = await act.Should().ThrowAsync<ReleaseReadyValidationException>();
        ex.Which.Message.Should().Contain("No artwork provided");
        await AssertNoPersistenceAsync(fixture);
    }

    [Fact]
    public async Task ValidateAndCreate_ValidAudioAndArtwork_CreatesDraftJobWithoutCharging()
    {
        var fixture = CreateFixture();
        await using var audio = new MemoryStream(CreateTaggedWav(TimeSpan.FromSeconds(6)));
        await using var artwork = new MemoryStream(CreateCoverPng(3000));

        var result = await fixture.Service.ValidateAndCreateAsync(Input(audio, "song.wav", artwork, "cover.png"));

        result.JobId.Should().NotBeEmpty();
        result.Validation.Passed.Should().BeTrue();
        _ = fixture.Storage.Received(2).UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>());
        _ = fixture.Jobs.Received(1).AddAsync(
            Arg.Is<MasteringJob>(j =>
                j.Status == "validated"
                && j.ContentHash != null
                && j.ContentHash.Length == 64
                && j.CoverArtKey != null
                && j.ChargedAt == null),
            Arg.Any<CancellationToken>());
        _ = fixture.Credits.DidNotReceive().TryChargeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAndCreate_DuplicateClassicUpload_ReusesExistingJobWithoutSecondChargeableRow()
    {
        var existingId = Guid.NewGuid();
        var report = new ValidationReport
        {
            Metadata = new MetadataValidationResult { Passed = true, DecodableAudio = true, DurationSeconds = 6, Title = "Song", Artist = "Artist", Album = "Release" },
            Artwork = new ArtworkValidationResult { Passed = true, Provided = true, Width = 3000, Height = 3000, Format = "PNG" },
        };
        var fixture = CreateFixture(existing: new MasteringJob
        {
            Id = existingId,
            CreatorId = "user-1",
            Engine = "ffmpeg",
            Status = "queued",
            Kind = "mastering",
            SourceKey = "release-ready/source/existing.wav",
            ValidationReportJson = JsonSerializer.Serialize(report, JsonOpts),
        });
        await using var audio = new MemoryStream(CreateTaggedWav(TimeSpan.FromSeconds(6)));
        await using var artwork = new MemoryStream(CreateCoverPng(3000));

        var result = await fixture.Service.ValidateAndCreateAsync(Input(audio, "song.wav", artwork, "cover.png"));

        result.JobId.Should().Be(existingId);
        _ = fixture.Storage.DidNotReceiveWithAnyArgs().UploadAsync(default!, default!, default!);
        _ = fixture.Jobs.DidNotReceive().AddAsync(Arg.Any<MasteringJob>(), Arg.Any<CancellationToken>());
        _ = fixture.Credits.DidNotReceive().TryChargeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static ReleaseReadyUploadInput Input(Stream audio, string audioName, Stream? artwork, string? artworkName) => new()
    {
        UserId = "user-1",
        Audio = audio,
        AudioFileName = audioName,
        Artwork = artwork,
        ArtworkFileName = artworkName,
    };

    private static TestFixture CreateFixture(MasteringJob? existing = null)
    {
        var validation = new ReleaseValidationService(
            Options.Create(new MasteringOptions { MinDurationSeconds = 5, MaxDurationSeconds = 900 }),
            NullLogger<ReleaseValidationService>.Instance);
        var credits = Substitute.For<IReleaseCreditService>();
        var jobs = Substitute.For<IMasteringJobRepository>();
        var engine = Substitute.For<IMasteringEngine>();
        var storage = Substitute.For<IObjectStorage>();
        var tracks = Substitute.For<ITrackRepository>();
        var pipeline = Substitute.For<ITrackReleasePipelineService>();
        var readiness = Substitute.For<ITrackReadinessCache>();

        engine.Name.Returns("ffmpeg");
        engine.RequiresApproval.Returns(false);
        jobs.GetActiveClassicByCreatorAndHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MasteringJob?>(existing));
        storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => Task.FromResult((string)call[1]!));

        var service = new ReleaseReadyService(
            validation,
            credits,
            jobs,
            engine,
            storage,
            tracks,
            pipeline,
            readiness,
            NullLogger<ReleaseReadyService>.Instance);

        return new TestFixture(service, credits, jobs, engine, storage);
    }

    private static Task AssertNoPersistenceAsync(TestFixture fixture)
    {
        _ = fixture.Storage.DidNotReceiveWithAnyArgs().UploadAsync(default!, default!, default!);
        _ = fixture.Jobs.DidNotReceive().AddAsync(Arg.Any<MasteringJob>(), Arg.Any<CancellationToken>());
        _ = fixture.Credits.DidNotReceive().TryChargeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = fixture.Engine.DidNotReceiveWithAnyArgs().MasterAsync(default!, default);
        return Task.CompletedTask;
    }

    private static byte[] CreateTaggedWav(
        TimeSpan duration,
        string? title = "Release Song",
        string? artist = "Release Artist",
        string? album = "Release Album")
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-test-{Guid.NewGuid():N}.wav");
        try
        {
            WriteSineWav(path, duration);
            using (var tag = TagLib.File.Create(path))
            {
                tag.Tag.Title = title;
                tag.Tag.Performers = artist is null ? Array.Empty<string>() : new[] { artist };
                tag.Tag.Album = album;
                tag.Save();
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void WriteSineWav(string path, TimeSpan duration)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var samples = Math.Max(1, (int)(duration.TotalSeconds * sampleRate));
        var dataSize = samples * channels * bitsPerSample / 8;

        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs, Encoding.ASCII);
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

    private static byte[] CreateCoverPng(int edge)
    {
        using var image = new Image<Rgba32>(edge, edge, new Rgba32(24, 80, 120, 255));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private sealed record TestFixture(
        ReleaseReadyService Service,
        IReleaseCreditService Credits,
        IMasteringJobRepository Jobs,
        IMasteringEngine Engine,
        IObjectStorage Storage);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

[Trait("Category", "ReleaseReady")]
public sealed class ReleaseReadyValidationApiTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public ReleaseReadyValidationApiTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ValidateEndpoint_InvalidAudioAndMissingCover_ReturnsStructuredErrorCodes()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync($"rr-errors-{Guid.NewGuid():N}@test.com");
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(CreatePngBytes(128));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(audio, "audio", "fake.mp3");

        var response = await client.PostAsync("/release-ready/validate", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be(ReleaseReadyErrorCodes.ValidationFailed);
        body.GetProperty("error").GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().Contain(e => e.GetProperty("code").GetString() == ReleaseReadyErrorCodes.InvalidAudio);
        errors.Should().Contain(e => e.GetProperty("code").GetString() == ReleaseReadyErrorCodes.MissingCoverArt);
        errors.Should().AllSatisfy(e =>
        {
            e.TryGetProperty("message", out var message).Should().BeTrue();
            message.GetString().Should().NotBeNullOrWhiteSpace();
        });
    }

    private static byte[] CreatePngBytes(int edge)
    {
        using var image = new Image<Rgba32>(edge, edge, new Rgba32(120, 24, 80, 255));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
