using System.Text;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Provenance;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for the batched Merkle anchor job (<see cref="ProvenanceAnchorBatchProcessor"/>):
/// one batch → one root → one tx, per-leaf proofs persisted, and retry-safety on failure.
/// </summary>
public sealed class ProvenanceAnchorBatchTests
{
    private static string HexOf(string s) =>
        ContentHashing.ComputeSha256Hex(new MemoryStream(Encoding.ASCII.GetBytes(s)));

    private static ProvenanceAnchorBatchProcessor Processor(
        IProvenanceAnchorRepository repo, IProvenanceAnchor anchor) =>
        new(repo, anchor, NullLogger<ProvenanceAnchorBatchProcessor>.Instance);

    private static ProvenanceAnchor Pending(string hash) => new()
    {
        Id = Guid.NewGuid(),
        TrackId = Guid.NewGuid(),
        ContentHash = hash,
        Status = "pending",
    };

    [Fact]
    public async Task ProcessBatch_AnchorsAll_OneRoot_AndEveryProofVerifies()
    {
        var rows = new List<ProvenanceAnchor> { Pending(HexOf("a")), Pending(HexOf("b")), Pending(HexOf("c")) };
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        repo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        List<ProvenanceAnchor>? saved = null;
        repo.UpdateRangeAsync(Arg.Do<IEnumerable<ProvenanceAnchor>>(r => saved = r.ToList()), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Real NoOp anchor → real Merkle root + proofs (just a dev tx ref).
        var anchor = new NoOpProvenanceAnchor(NullLogger<NoOpProvenanceAnchor>.Instance);

        var count = await Processor(repo, anchor).ProcessBatchAsync(100);

        Assert.Equal(3, count);
        Assert.NotNull(saved);
        Assert.All(saved!, r => Assert.Equal("anchored", r.Status));
        Assert.Single(saved!.Select(r => r.BatchId).Distinct());      // one batch
        Assert.Single(saved!.Select(r => r.RootTxRef).Distinct());     // one tx
        Assert.All(saved!, r => Assert.True(
            MerkleTree.VerifyProof(r.ContentHash, r.MerkleProof!, r.MerkleRoot!),
            $"proof for {r.ContentHash} must verify against root"));
    }

    [Fact]
    public async Task ProcessBatch_NoPending_ReturnsZero_AndDoesNotAnchor()
    {
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        repo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<ProvenanceAnchor>());
        var anchor = Substitute.For<IProvenanceAnchor>();

        var count = await Processor(repo, anchor).ProcessBatchAsync(100);

        Assert.Equal(0, count);
        await anchor.DidNotReceive().AnchorBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatch_AnchorThrows_LeavesRowsPending()
    {
        var rows = new List<ProvenanceAnchor> { Pending(HexOf("a")) };
        var repo = Substitute.For<IProvenanceAnchorRepository>();
        repo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var anchor = Substitute.For<IProvenanceAnchor>();
        anchor.AnchorBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("rpc down"));

        var count = await Processor(repo, anchor).ProcessBatchAsync(100);

        Assert.Equal(0, count);
        Assert.Equal("pending", rows[0].Status); // untouched → rolls into next batch
        await repo.DidNotReceive().UpdateRangeAsync(Arg.Any<IEnumerable<ProvenanceAnchor>>(), Arg.Any<CancellationToken>());
    }
}
