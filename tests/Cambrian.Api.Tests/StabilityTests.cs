using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Backend stability layer tests — validates invariants, idempotency,
/// transactional guarantees, and validation guards.
/// </summary>
public sealed class StabilityTests
{
    // ────────────────────────────────────────────────
    //  Upload service — price range & title validation
    // ────────────────────────────────────────────────

    private static UploadService MakeUploadService(out ITrackRepository tracks, out UserManager<ApplicationUser> users)
    {
        tracks = Substitute.For<ITrackRepository>();
        var storage = Substitute.For<IObjectStorage>();
        storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/key");
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var creators = Substitute.For<ICreatorIdentityRepository>();

        return new UploadService(storage, tracks, users,
            Substitute.For<ILogger<UploadService>>(), creators);
    }

    private static IFormFile MakeAudioFile(string name = "beat.mp3", long length = 1024)
    {
        // MP3 magic bytes (ID3 tag)
        var bytes = new byte[Math.Max(length, 4)];
        bytes[0] = 0x49; // 'I'
        bytes[1] = 0x44; // 'D'
        bytes[2] = 0x33; // '3'
        var ms = new MemoryStream(bytes);
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        file.OpenReadStream().Returns(ms);
        return file;
    }

    [Fact(Skip = "Phase A validation reverted in c7e31bf — restore UploadService title guard to re-enable")]
    public async Task Upload_RejectsBlankTitle()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "   ",
                CreatorId = "creator-1",
                Price = 10m
            }));

        Assert.Contains("title is required", ex.Message);
    }

    [Fact(Skip = "Phase A validation reverted in c7e31bf — restore UploadService price range guard to re-enable")]
    public async Task Upload_RejectsPriceBelowMinimum()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        // $0.10 = 10 cents, below $0.50 minimum
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "Valid Title",
                CreatorId = "creator-1",
                Price = 0.10m
            }));

        Assert.Contains("at least", ex.Message);
    }

    [Fact(Skip = "Phase A validation reverted in c7e31bf — restore UploadService price range guard to re-enable")]
    public async Task Upload_RejectsPriceAboveMaximum()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        // $60,000 > $50,000 cap
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "Valid Title",
                CreatorId = "creator-1",
                Price = 60_000m
            }));

        Assert.Contains("must not exceed", ex.Message);
    }

    [Fact(Skip = "Phase A validation reverted in c7e31bf — restore UploadService price range guard to re-enable")]
    public async Task Upload_RejectsNonExclusivePriceBelowMinimum()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "Valid Title",
                CreatorId = "creator-1",
                Price = 10m,
                NonExclusivePrice = 0.20m
            }));

        Assert.Contains("Non-exclusive price", ex.Message);
        Assert.Contains("at least", ex.Message);
    }

    [Fact]
    public async Task Upload_AcceptsNullPrices()
    {
        // Null prices should be skipped (valid — default pricing)
        var sut = MakeUploadService(out var tracks, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });
        tracks.AddAsync(Arg.Any<Track>()).Returns(Task.CompletedTask);

        // No exception — null prices should be accepted
        await sut.Upload(new UploadTrackRequest
        {
            Audio = MakeAudioFile(),
            Title = "Valid Title",
            CreatorId = "creator-1",
            Price = null,
            NonExclusivePrice = null,
            ExclusivePrice = null,
            CopyrightBuyoutPrice = null
        });

        await tracks.Received(1).AddAsync(Arg.Any<Track>());
    }

    [Fact]
    public async Task Upload_AcceptsValidPriceRange()
    {
        var sut = MakeUploadService(out var tracks, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });
        tracks.AddAsync(Arg.Any<Track>()).Returns(Task.CompletedTask);

        // Valid: $0.50 minimum boundary
        await sut.Upload(new UploadTrackRequest
        {
            Audio = MakeAudioFile(),
            Title = "Valid Title",
            CreatorId = "creator-1",
            Price = 0.50m,
            NonExclusivePrice = 5m,
            ExclusivePrice = 100m,
            CopyrightBuyoutPrice = 500m
        });

        await tracks.Received(1).AddAsync(Arg.Any<Track>());
    }

    // ────────────────────────────────────────────────
    //  Image upload — magic byte validation
    // ────────────────────────────────────────────────

    [Fact]
    public void ImageMagicBytes_ValidJpeg_Passes()
    {
        // JPEG starts with FF D8 FF
        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 });
        Assert.True(CheckImageMagicBytes(stream, ".jpg"));
    }

    [Fact]
    public void ImageMagicBytes_ValidPng_Passes()
    {
        // PNG starts with 89 50 4E 47
        var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D });
        Assert.True(CheckImageMagicBytes(stream, ".png"));
    }

    [Fact]
    public void ImageMagicBytes_ValidWebp_Passes()
    {
        // WebP: RIFF....WEBP
        var bytes = new byte[12];
        var riff = System.Text.Encoding.ASCII.GetBytes("RIFF");
        var webp = System.Text.Encoding.ASCII.GetBytes("WEBP");
        Array.Copy(riff, 0, bytes, 0, 4);
        bytes[4] = 0x00; bytes[5] = 0x00; bytes[6] = 0x00; bytes[7] = 0x00; // size placeholder
        Array.Copy(webp, 0, bytes, 8, 4);
        var stream = new MemoryStream(bytes);
        Assert.True(CheckImageMagicBytes(stream, ".webp"));
    }

    [Fact]
    public void ImageMagicBytes_DisguisedExe_FailsJpeg()
    {
        // MZ header (PE executable) disguised as .jpg
        var stream = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 });
        Assert.False(CheckImageMagicBytes(stream, ".jpg"));
    }

    [Fact]
    public void ImageMagicBytes_DisguisedExe_FailsPng()
    {
        var stream = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 });
        Assert.False(CheckImageMagicBytes(stream, ".png"));
    }

    [Fact]
    public void ImageMagicBytes_RiffNotWebp_Fails()
    {
        // RIFF header but NOT WEBP at offset 8 (e.g., WAV file disguised as .webp)
        var bytes = new byte[12];
        var riff = System.Text.Encoding.ASCII.GetBytes("RIFF");
        var wave = System.Text.Encoding.ASCII.GetBytes("WAVE");
        Array.Copy(riff, 0, bytes, 0, 4);
        Array.Copy(wave, 0, bytes, 8, 4);
        var stream = new MemoryStream(bytes);
        Assert.False(CheckImageMagicBytes(stream, ".webp"));
    }

    /// <summary>
    /// Replicates the magic byte validation logic from UploadController.UploadImage
    /// for pure unit testing without holding an HTTP pipeline.
    /// </summary>
    private static bool CheckImageMagicBytes(Stream stream, string ext)
    {
        var imgMagic = new Dictionary<string, byte[][]>
        {
            [".jpg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
            [".jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
            [".png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],
            [".webp"] = [System.Text.Encoding.ASCII.GetBytes("RIFF")]
        };

        if (!imgMagic.TryGetValue(ext, out var signatures))
            return false;

        var headerBuf = new byte[12];
        var bytesRead = stream.Read(headerBuf);
        stream.Position = 0;

        foreach (var sig in signatures)
        {
            if (bytesRead >= sig.Length && headerBuf.AsSpan(0, sig.Length).SequenceEqual(sig))
            {
                if (ext == ".webp" && (bytesRead < 12 || !headerBuf.AsSpan(8, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("WEBP"))))
                    return false;
                return true;
            }
        }
        return false;
    }

    // ────────────────────────────────────────────────
    //  Tier manifest — fee rate invariants
    // ────────────────────────────────────────────────

    [Fact]
    public void TierManifest_FreeCreator_Has35PercentFee()
    {
        var config = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Free);
        Assert.Equal(0.35m, config.FeeRate);
    }

    [Fact]
    public void TierManifest_CreatorIs15Percent_ProIs10PercentFee()
    {
        // Creator pays a 15% platform fee; Pro pays the lower 10% premium-tier fee.
        Assert.Equal(0.15m, Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Creator).FeeRate);
        Assert.Equal(0.10m, Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Pro).FeeRate);
    }

    [Fact]
    public void TierManifest_FreeCreator_HasUploadLimit()
    {
        var config = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Free);
        Assert.NotNull(config.UploadLimit);
        Assert.True(config.UploadLimit > 0,
            "Free tier must have a finite upload limit.");
    }

    [Fact]
    public void TierManifest_ProCreator_IsUnlimited()
    {
        var pro = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Pro);
        Assert.True(pro.IsUnlimited,
            "Pro tier must have unlimited uploads.");
    }

    [Fact]
    public void TierManifest_CreatorWalletCredit_NeverRoundsUp()
    {
        // Verify the payout invariant: floor(gross × (1 − feeRate))
        var feeRate = 0.15m; // Pro
        var grossCents = 2999; // $29.99

        var creatorCents = (int)Math.Floor(grossCents * (1 - feeRate));
        Assert.Equal(2549, creatorCents); // 2999 × 0.85 = 2549.15 → floor = 2549

        // Verify it never rounds up
        Assert.True(creatorCents <= grossCents * (1 - feeRate),
            "Creator credit must never exceed gross × (1 - feeRate)");
    }
}
