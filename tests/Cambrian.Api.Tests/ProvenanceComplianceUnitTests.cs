using System.Text;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Provenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for the §9 provenance/compliance building blocks: content hashing, the ECDSA
/// signed timestamp, the Merkle tree, the deterministic compliance score, and the (pending)
/// anchor service.
/// </summary>
public sealed class ProvenanceComplianceUnitTests
{
    private static string HexOf(string s) =>
        ContentHashing.ComputeSha256Hex(new MemoryStream(Encoding.ASCII.GetBytes(s)));

    // ── ContentHashing ──

    [Fact]
    public void ComputeSha256Hex_MatchesKnownVector()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ContentHashing.ComputeSha256Hex(stream));
    }

    [Fact]
    public void ComputeSha256Hex_ResetsSeekableStreamToStart()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        ContentHashing.ComputeSha256Hex(stream);
        Assert.Equal(0, stream.Position);
    }

    // ── EcdsaProvenanceSigner ──

    private static EcdsaProvenanceSigner NewSigner() =>
        new(new ConfigurationBuilder().Build(), NullLogger<EcdsaProvenanceSigner>.Instance);

    [Fact]
    public void Signer_SignThenVerify_Roundtrips()
    {
        var signer = NewSigner();
        var hash = HexOf("audio-bytes");

        var stamp = signer.Sign(hash, DateTime.UtcNow);

        Assert.True(signer.Verify(hash, stamp.SignedAt, stamp.Signature));
        Assert.Equal("ECDSA-P256-SHA256", stamp.Algorithm);
        Assert.Equal(16, stamp.KeyId.Length);
    }

    [Fact]
    public void Signer_RejectsTamperedHashTimestampAndSignature()
    {
        var signer = NewSigner();
        var hash = HexOf("audio-bytes");
        var stamp = signer.Sign(hash, DateTime.UtcNow);

        Assert.False(signer.Verify(HexOf("other"), stamp.SignedAt, stamp.Signature)); // wrong hash
        Assert.False(signer.Verify(hash, stamp.SignedAt.AddSeconds(1), stamp.Signature)); // wrong time
        Assert.False(signer.Verify(hash, stamp.SignedAt, "not-base64!!"));               // malformed sig
    }

    [Fact]
    public void Signer_SignedAt_IsTruncatedToWholeSeconds()
    {
        var stamp = NewSigner().Sign(HexOf("x"), new DateTime(2026, 6, 4, 12, 0, 0, 123, DateTimeKind.Utc));
        Assert.Equal(0, stamp.SignedAt.Millisecond);
        Assert.Equal(DateTimeKind.Utc, stamp.SignedAt.Kind);
    }

    [Fact]
    public void Signer_PublicKey_IsExportedPem()
    {
        var signer = NewSigner();
        Assert.Contains("BEGIN PUBLIC KEY", signer.GetPublicKeyPem());
    }

    // ── MerkleTree ──

    [Fact]
    public void Merkle_EveryLeafProofVerifiesToRoot()
    {
        var leaves = new[] { HexOf("a"), HexOf("b"), HexOf("c"), HexOf("d"), HexOf("e") }; // odd-ish
        var root = MerkleTree.ComputeRoot(leaves);

        for (var i = 0; i < leaves.Length; i++)
        {
            var proof = MerkleTree.BuildProofJson(leaves, i);
            Assert.True(MerkleTree.VerifyProof(leaves[i], proof, root), $"leaf {i} should verify");
        }
    }

    [Fact]
    public void Merkle_TamperedLeafFailsVerification()
    {
        var leaves = new[] { HexOf("a"), HexOf("b"), HexOf("c") };
        var root = MerkleTree.ComputeRoot(leaves);
        var proof0 = MerkleTree.BuildProofJson(leaves, 0);

        Assert.False(MerkleTree.VerifyProof(HexOf("tampered"), proof0, root));
    }

    [Fact]
    public void Merkle_SingleLeaf_RootIsLeafAndEmptyProofVerifies()
    {
        var leaf = HexOf("only");
        var root = MerkleTree.ComputeRoot(new[] { leaf });
        Assert.Equal(leaf, root);
        Assert.True(MerkleTree.VerifyProof(leaf, MerkleTree.BuildProofJson(new[] { leaf }, 0), root));
    }

    // ── ComplianceScoreService ──

    private static ComplianceScoreService CreateComplianceService(ProvenanceAnchor? anchor, TrackAuthorship? authorship)
    {
        var anchors = Substitute.For<IProvenanceAnchorRepository>();
        anchors.GetByTrackIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(anchor);
        var auth = Substitute.For<ITrackAuthorshipRepository>();
        auth.GetByTrackIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(authorship);
        return new ComplianceScoreService(anchors, auth);
    }

    [Fact]
    public async Task ComplianceScore_BareUnsignedTrack_ScoresZeroAndAllFail()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };
        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        Assert.Equal(0, result.Score);
        Assert.All(result.Checks, c => Assert.Equal("fail", c.Status));
    }

    [Fact]
    public async Task ComplianceScore_FullyCompliant_ScoresHundred()
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Beat",
            PrimaryGenre = "Electronic", Description = "A track", Mood = "chill", Tempo = "90",
            CoverArtUrl = "covers/x.jpg", CommercialRightsVerified = true, Signature = "sig",
        };
        var anchor = new ProvenanceAnchor { TrackId = track.Id, Status = "anchored", Chain = "base" };
        var authorship = new TrackAuthorship { TrackId = track.Id, Edits = "Mastered", AiDisclosure = "No AI used" };

        var result = await CreateComplianceService(anchor, authorship).ComputeAsync(track);

        Assert.Equal(100, result.Score);
        Assert.All(result.Checks, c => Assert.Equal("pass", c.Status));
    }

    [Fact]
    public async Task ComplianceScore_StampedButNotAnchored_IsWarn()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat", Signature = "sig" }; // signed, no anchor
        var result = await CreateComplianceService(anchor: null, authorship: null).ComputeAsync(track);

        var check = result.Checks.Single(c => c.Name == "provenanceAnchored");
        Assert.Equal("warn", check.Status);
    }

    [Fact]
    public async Task ComplianceScore_AnchoredScoresHigherThanStamped()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat", Signature = "sig" };

        var stamped = await CreateComplianceService(null, null).ComputeAsync(track); // warn (10)
        var anchored = await CreateComplianceService(
            new ProvenanceAnchor { TrackId = track.Id, Status = "anchored" }, null).ComputeAsync(track); // pass (20)

        Assert.Equal(stamped.Score + 10, anchored.Score);
    }

    // ── ProvenanceService (pending anchor record; no chain write) ──

    [Fact]
    public async Task EnsureAnchorPending_CreatesPendingRow()
    {
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        repo.GetByTrackIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ProvenanceAnchor?)null);
        var svc = new ProvenanceService(repo, NullLogger<ProvenanceService>.Instance);

        await svc.EnsureAnchorPendingAsync(Guid.NewGuid(), "deadbeef");

        await repo.Received(1).AddAsync(
            Arg.Is<ProvenanceAnchor>(a => a.Status == "pending" && a.ContentHash == "deadbeef"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureAnchorPending_IsIdempotent()
    {
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        var trackId = Guid.NewGuid();
        repo.GetByTrackIdAsync(trackId, Arg.Any<CancellationToken>())
            .Returns(new ProvenanceAnchor { TrackId = trackId, Status = "pending" });
        var svc = new ProvenanceService(repo, NullLogger<ProvenanceService>.Instance);

        await svc.EnsureAnchorPendingAsync(trackId, "deadbeef");

        await repo.DidNotReceive().AddAsync(Arg.Any<ProvenanceAnchor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAnchorState_NoRow_ReportsPending()
    {
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        repo.GetByTrackIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ProvenanceAnchor?)null);
        var svc = new ProvenanceService(repo, NullLogger<ProvenanceService>.Instance);

        var state = await svc.GetAnchorStateAsync(Guid.NewGuid());

        Assert.Equal("pending", state.Status);
        Assert.Null(state.RootTxRef);
    }

    [Fact]
    public async Task GetAnchorState_Anchored_ReturnsMerkleFields()
    {
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        var trackId = Guid.NewGuid();
        repo.GetByTrackIdAsync(trackId, Arg.Any<CancellationToken>()).Returns(new ProvenanceAnchor
        {
            TrackId = trackId, Status = "anchored", Chain = "base",
            MerkleRoot = "abcd", RootTxRef = "0xtx", MerkleProof = "[]",
            AnchoredAt = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc),
        });
        var svc = new ProvenanceService(repo, NullLogger<ProvenanceService>.Instance);

        var state = await svc.GetAnchorStateAsync(trackId);

        Assert.Equal("anchored", state.Status);
        Assert.Equal("base", state.Chain);
        Assert.Equal("0xtx", state.RootTxRef);
        Assert.Equal("abcd", state.MerkleRoot);
    }
}
