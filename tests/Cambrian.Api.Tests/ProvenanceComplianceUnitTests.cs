using System.Text;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Provenance;
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

    private static ComplianceScoreService CreateComplianceService(
        ProvenanceAnchor? anchor,
        TrackAuthorship? authorship,
        TrackLyricsDto? lyrics = null,
        BehindTheTrackDto? creationProcess = null,
        AuthorshipRecord? authorshipRecord = null,
        bool hasCreatorProfile = false)
    {
        var anchors = Substitute.For<IProvenanceAnchorRepository>();
        anchors.GetByTrackIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(anchor);
        var auth = Substitute.For<ITrackAuthorshipRepository>();
        auth.GetByTrackIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(authorship);
        var details = Substitute.For<ITrackDetailsRepository>();
        details.GetLyricsAsync(Arg.Any<Guid>()).Returns(lyrics);
        details.GetCreationProcessAsync(Arg.Any<Guid>()).Returns(creationProcess);
        var records = Substitute.For<IAuthorshipRecordRepository>();
        records.GetLatestForTrackAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(authorshipRecord);
        var profiles = Substitute.For<ICreatorProfileRepository>();
        profiles.HasUsableProfileAsync(Arg.Any<string>()).Returns(hasCreatorProfile);
        return new ComplianceScoreService(anchors, auth, details, records, profiles);
    }

    private static ComplianceChecklistItemDto ChecklistItem(ComplianceScoreResponse response, string key) =>
        response.ChecklistItems.Single(i => i.Key == key);

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

    [Fact]
    public async Task ComplianceChecklist_AiDisclosureFromDdex_IsComplete()
    {
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "Beat",
            AiDisclosureDdex = """{"aiGenerated":true,"tools":["Suno"]}""",
        };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        var item = ChecklistItem(result, "ai_disclosure");
        Assert.Equal("complete", item.Status);
        Assert.Contains("satisfies the AI disclosure checklist item", item.Explanation);
    }

    [Fact]
    public async Task ComplianceChecklist_MissingAiDisclosure_IsIncomplete()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        var item = ChecklistItem(result, "ai_disclosure");
        Assert.Equal("incomplete", item.Status);
    }

    [Fact]
    public async Task ComplianceChecklist_AuthorshipRecord_IsOptionalPaidVerification_WhenMissing()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        var item = ChecklistItem(result, "authorship_record");
        Assert.Contains("optional paid verification", item.Label.ToLowerInvariant());
        Assert.Equal("optional_paid_verification", item.Status);
        Assert.False(item.IsPaidRequirement);
        Assert.Contains("not required", item.Explanation.ToLowerInvariant());
    }

    [Fact]
    public async Task ComplianceChecklist_FreeMaxScore_RemainsHundredWithoutPaidAuthorshipRecord()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        Assert.Equal(100, result.FreeMaxScore);
    }

    [Fact]
    public async Task ComplianceChecklist_RightsAttestation_MissingIsIncompleteWithoutPaidLanguage()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat", CommercialRightsVerified = false };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        var item = ChecklistItem(result, "rights");
        Assert.Equal("incomplete", item.Status);
        Assert.Contains("free creator attestation", item.Explanation.ToLowerInvariant());
        Assert.Contains("not a paid verification", item.Explanation.ToLowerInvariant());
    }

    // ── Release-readiness contradiction regression (observed on a live prod track) ──
    // A creator filled Behind-the-Track prompt notes (which the UI used to claim
    // satisfied AI disclosure) and could never complete ai_disclosure/rights.
    // These pin the EXACT source fields each requirement reads.

    [Theory]
    [InlineData(null, false)]                                  // no authorship row semantics via empty text
    [InlineData("", false)]                                    // saved but empty
    [InlineData("   ", false)]                                 // whitespace only
    [InlineData("No generative AI was used.", true)]           // explicit "no AI" counts
    [InlineData("Suno v5 for stems; vocals + mix are mine.", true)]
    public async Task ComplianceChecklist_AiDisclosure_EvaluatesAuthorshipText(string? disclosure, bool expectedComplete)
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };
        var authorship = new TrackAuthorship { TrackId = track.Id, AiDisclosure = disclosure };

        var result = await CreateComplianceService(null, authorship).ComputeAsync(track);

        Assert.Equal(expectedComplete ? "complete" : "incomplete", ChecklistItem(result, "ai_disclosure").Status);
    }

    [Fact]
    public async Task ComplianceChecklist_BttPromptNotesAlone_DoNotSatisfyAiDisclosure()
    {
        // The prod contradiction: prompt notes count for daw_tools but must not
        // silently satisfy ai_disclosure — disclosure has to be explicit.
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };
        var btt = new BehindTheTrackDto { TrackId = track.Id.ToString(), PromptNotes = "942 chars of prompts…" };

        var result = await CreateComplianceService(null, authorship: null, creationProcess: btt).ComputeAsync(track);

        Assert.Equal("incomplete", ChecklistItem(result, "ai_disclosure").Status);
        Assert.Equal("complete", ChecklistItem(result, "daw_tools").Status);
    }

    [Theory]
    [InlineData(false, "incomplete")]
    [InlineData(true, "complete")]
    public async Task ComplianceChecklist_Rights_EvaluatesTrackCommercialRightsFlag(bool attested, string expected)
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat", CommercialRightsVerified = attested };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        Assert.Equal(expected, ChecklistItem(result, "rights").Status);
    }

    [Fact]
    public async Task ComplianceScore_BackwardCompatibility_KeepsExistingScoreAndChecks()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat" };

        var result = await CreateComplianceService(null, null).ComputeAsync(track);

        Assert.Equal(0, result.Score);
        Assert.Equal(new[]
        {
            "commercialRightsVerified",
            "authorshipDocumented",
            "aiDisclosurePresent",
            "provenanceAnchored",
            "metadataComplete",
        }, result.Checks.Select(c => c.Name));
    }

    [Fact]
    public async Task ComplianceChecklist_DoesNotExposePrivateNarrativeContent()
    {
        const string privateText = "PRIVATE_NARRATIVE_SHOULD_NOT_LEAK";
        var track = new Track { Id = Guid.NewGuid(), Title = "Beat", Visibility = "hidden" };
        var authorship = new TrackAuthorship
        {
            TrackId = track.Id,
            Edits = privateText,
            ProcessNotes = privateText,
            AiDisclosure = privateText,
        };
        var creationProcess = new BehindTheTrackDto
        {
            TrackId = track.Id.ToString(),
            Story = privateText,
            DAW = privateText,
            HumanContributionNotes = privateText,
            ToolsUsed = new[] { privateText },
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await CreateComplianceService(null, authorship, creationProcess: creationProcess)
            .ComputeAsync(track);

        var explanations = string.Join(" ", result.ChecklistItems.Select(i => i.Explanation));
        Assert.DoesNotContain(privateText, explanations);
        Assert.All(result.ChecklistItems, i => Assert.False(string.IsNullOrWhiteSpace(i.TargetSection)));
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
