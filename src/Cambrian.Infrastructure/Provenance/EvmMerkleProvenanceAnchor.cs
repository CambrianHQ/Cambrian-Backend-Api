using System.Numerics;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace Cambrian.Infrastructure.Provenance;

/// <summary>
/// Production batched anchor on an EVM L2 (default Base). Builds the Merkle root + per-leaf proofs
/// (free compute via <see cref="MerkleTree"/>) and writes the root on-chain in <b>one</b> transaction:
/// a 0-value self-send carrying the 32-byte root in <c>data</c> (calldata). No contract to deploy —
/// the root is permanently readable from the tx input and the tx hash is the anchor reference.
///
/// <para>Selected when <c>Provenance:Anchor:Provider=evm</c>. Called serially by the single batch
/// worker, so nonces don't race. Requires a funded hot wallet (<c>PrivateKey</c>) + <c>RpcUrl</c>.</para>
/// </summary>
public sealed class EvmMerkleProvenanceAnchor : IProvenanceAnchor
{
    private static readonly TimeSpan ReceiptPollInterval = TimeSpan.FromSeconds(3);
    private const int MaxReceiptPolls = 60; // ~3 min ceiling, then fail → batch retries

    private readonly ProvenanceAnchorOptions _options;
    private readonly ILogger<EvmMerkleProvenanceAnchor> _logger;

    public EvmMerkleProvenanceAnchor(IOptions<ProvenanceAnchorOptions> options, ILogger<EvmMerkleProvenanceAnchor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BatchAnchorResult> AnchorBatchAsync(IReadOnlyList<string> leafHashes, CancellationToken ct = default)
    {
        if (leafHashes is null || leafHashes.Count == 0)
            throw new ArgumentException("At least one leaf hash is required.", nameof(leafHashes));
        if (string.IsNullOrWhiteSpace(_options.RpcUrl) || string.IsNullOrWhiteSpace(_options.PrivateKey))
            throw new InvalidOperationException("EVM anchor requires Provenance:Anchor:RpcUrl and PrivateKey.");

        var root = MerkleTree.ComputeRoot(leafHashes);
        var proofs = leafHashes
            .Select((leaf, i) => new LeafProof(leaf, MerkleTree.BuildProofJson(leafHashes, i)))
            .ToList();

        var account = new Account(_options.PrivateKey, new BigInteger(_options.ChainId));
        var web3 = new Web3(account, _options.RpcUrl);

        // One tx: self-send, value 0, root in calldata.
        var input = new TransactionInput
        {
            From = account.Address,
            To = account.Address,
            Value = new HexBigInteger(0),
            Data = "0x" + root,
        };

        var txHash = await web3.Eth.TransactionManager.SendTransactionAsync(input);

        TransactionReceipt? receipt = null;
        for (var i = 0; i < MaxReceiptPolls && receipt is null; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(ReceiptPollInterval, ct);
            receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        }

        if (receipt is null)
            throw new TimeoutException($"Anchor tx {txHash} not confirmed within the poll window.");
        if (receipt.Status is null || receipt.Status.Value == 0)
            throw new InvalidOperationException($"Anchor tx {txHash} reverted.");

        _logger.LogInformation(
            "EVENT: EvmProvenanceAnchored leaves:{Count} chain:{Chain} root:{Root} tx:{Tx} block:{Block}",
            leafHashes.Count, _options.Chain, root, txHash, receipt.BlockNumber?.Value);

        return new BatchAnchorResult(_options.Chain, root, txHash, DateTime.UtcNow, proofs);
    }
}
