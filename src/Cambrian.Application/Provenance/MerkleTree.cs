using System.Security.Cryptography;
using System.Text.Json;

namespace Cambrian.Application.Provenance;

/// <summary>
/// Minimal binary Merkle tree over content-hash leaves, used by the batched anchor: many leaves
/// reduce to one root, and each leaf gets a compact proof to that root. Leaves are the lowercase
/// hex content hashes; internal nodes are <c>SHA-256(left || right)</c> over the raw 32-byte
/// digests. An odd node at any level is promoted unchanged (duplicate-free).
///
/// <para>Free compute — shared by the dev anchor now and the real L2 anchor in batch 2, so a leaf's
/// proof produced here verifies against the same root regardless of which anchor wrote it on-chain.</para>
/// </summary>
public static class MerkleTree
{
    /// <summary>A single step in a Merkle proof: the sibling hash (hex) and which side it is on.</summary>
    public sealed record ProofStep(string Sibling, string Position); // Position: "left" | "right"

    /// <summary>Compute the Merkle root (hex) for the given leaf hashes (hex). Order matters.</summary>
    public static string ComputeRoot(IReadOnlyList<string> leafHashesHex)
    {
        var level = ToBytes(leafHashesHex);
        if (level.Count == 0)
            throw new ArgumentException("At least one leaf is required.", nameof(leafHashesHex));

        while (level.Count > 1)
            level = NextLevel(level);

        return Convert.ToHexString(level[0]).ToLowerInvariant();
    }

    /// <summary>
    /// Build the proof (sibling path) for the leaf at <paramref name="index"/>, as a JSON array of
    /// <see cref="ProofStep"/>. Verifiable with <see cref="VerifyProof"/>.
    /// </summary>
    public static string BuildProofJson(IReadOnlyList<string> leafHashesHex, int index)
    {
        if (index < 0 || index >= leafHashesHex.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var level = ToBytes(leafHashesHex);
        var steps = new List<ProofStep>();
        var pos = index;

        while (level.Count > 1)
        {
            var isRightNode = (pos % 2) == 1;
            var siblingIndex = isRightNode ? pos - 1 : pos + 1;

            if (siblingIndex < level.Count)
            {
                steps.Add(new ProofStep(
                    Convert.ToHexString(level[siblingIndex]).ToLowerInvariant(),
                    isRightNode ? "left" : "right"));
            }
            // else: odd node promoted; no sibling this level.

            pos /= 2;
            level = NextLevel(level);
        }

        return JsonSerializer.Serialize(steps);
    }

    /// <summary>
    /// Verify that <paramref name="leafHashHex"/> + <paramref name="proofJson"/> reproduce
    /// <paramref name="rootHex"/>. Returns false (never throws) on any malformed input, since this
    /// runs on a public endpoint.
    /// </summary>
    public static bool VerifyProof(string leafHashHex, string proofJson, string rootHex)
    {
        try
        {
            var steps = JsonSerializer.Deserialize<List<ProofStep>>(proofJson);
            if (steps is null)
                return false;

            var acc = Convert.FromHexString(leafHashHex);
            foreach (var step in steps)
            {
                var sibling = Convert.FromHexString(step.Sibling);
                acc = step.Position == "left"
                    ? HashPair(sibling, acc)
                    : HashPair(acc, sibling);
            }

            return Convert.ToHexString(acc).Equals(rootHex, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException)
        {
            return false;
        }
    }

    private static List<byte[]> NextLevel(List<byte[]> level)
    {
        var next = new List<byte[]>((level.Count + 1) / 2);
        for (var i = 0; i < level.Count; i += 2)
        {
            next.Add(i + 1 < level.Count
                ? HashPair(level[i], level[i + 1])
                : level[i]); // odd one out promoted unchanged
        }
        return next;
    }

    private static byte[] HashPair(byte[] left, byte[] right)
    {
        var buffer = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, buffer, 0, left.Length);
        Buffer.BlockCopy(right, 0, buffer, left.Length, right.Length);
        return SHA256.HashData(buffer);
    }

    private static List<byte[]> ToBytes(IReadOnlyList<string> hex) =>
        hex.Select(Convert.FromHexString).ToList();
}
