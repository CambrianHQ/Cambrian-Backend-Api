using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

/// <summary>
/// Deterministic compliance score. Five equally-weighted checks (20 pts each;
/// pass = full, warn = half, fail = 0) sum to a 0–100 score. Every rule is defined
/// here so the surface is easy to extend or re-weight.
/// </summary>
public sealed class ComplianceScoreService : IComplianceScoreService
{
    private const int Pass = 20;
    private const int Warn = 10;
    private const int Fail = 0;
    private const int FreeChecklistMaxScore = 100;

    private const string Complete = "complete";
    private const string Incomplete = "incomplete";
    private const string Optional = "optional";
    private const string OptionalPaidVerification = "optional_paid_verification";

    private readonly IProvenanceAnchorRepository _anchors;
    private readonly ITrackAuthorshipRepository _authorship;
    private readonly ITrackDetailsRepository _details;
    private readonly IAuthorshipRecordRepository _authorshipRecords;
    private readonly ICreatorProfileRepository _profiles;

    public ComplianceScoreService(
        IProvenanceAnchorRepository anchors,
        ITrackAuthorshipRepository authorship,
        ITrackDetailsRepository details,
        IAuthorshipRecordRepository authorshipRecords,
        ICreatorProfileRepository profiles)
    {
        _anchors = anchors;
        _authorship = authorship;
        _details = details;
        _authorshipRecords = authorshipRecords;
        _profiles = profiles;
    }

    public async Task<ComplianceScoreResponse> ComputeAsync(Track track, CancellationToken ct = default)
    {
        var anchor = await _anchors.GetByTrackIdAsync(track.Id, ct);
        var authorship = await _authorship.GetByTrackIdAsync(track.Id, ct);
        var lyrics = await _details.GetLyricsAsync(track.Id);
        var creationProcess = await _details.GetCreationProcessAsync(track.Id);
        var authorshipRecord = await _authorshipRecords.GetLatestForTrackAsync(track.Id, ct);
        var hasCreatorProfile = await _profiles.HasUsableProfileAsync(track.CreatorId);

        var checks = new List<ComplianceCheck>
        {
            CommercialRights(track),
            AuthorshipDocumented(authorship, creationProcess),
            AiDisclosure(authorship, track),
            ProvenanceAnchored(anchor, stamped: !string.IsNullOrWhiteSpace(track.Signature)),
            MetadataComplete(track),
        };

        var score = checks.Sum(c => Points(c.Status));

        return new ComplianceScoreResponse
        {
            Score = score,
            Checks = checks,
            ChecklistItems = BuildChecklist(
                track,
                authorship,
                anchor,
                lyrics,
                creationProcess,
                authorshipRecord,
                hasCreatorProfile),
            FreeMaxScore = FreeChecklistMaxScore,
        };
    }

    private static int Points(string status) => status switch
    {
        "pass" => Pass,
        "warn" => Warn,
        _ => Fail,
    };

    private static ComplianceCheck CommercialRights(Track track) =>
        track.CommercialRightsVerified
            ? Check("commercialRightsVerified", "pass", "Commercial rights have been attested for this track.")
            : Check("commercialRightsVerified", "fail", "Commercial rights have not been verified yet.");

    private static ComplianceCheck AuthorshipDocumented(TrackAuthorship? a, BehindTheTrackDto? creation)
    {
        // Mirrors the checklist's hasHumanContribution: the free Behind-the-Track
        // human-contribution note documents authorship just as the plan-gated
        // TrackAuthorship narrative does. Without this, a free creator's completed
        // "human contribution" checklist item never moved the score.
        var hasNarrative = (a is not null && (
            !string.IsNullOrWhiteSpace(a.Edits) ||
            !string.IsNullOrWhiteSpace(a.ArrangementNotes) ||
            !string.IsNullOrWhiteSpace(a.ProcessNotes) ||
            a.LyricsAuthored))
            || !string.IsNullOrWhiteSpace(creation?.HumanContributionNotes);

        if (hasNarrative)
            return Check("authorshipDocumented", "pass", "Authorship details have been documented.");

        return a is null
            ? Check("authorshipDocumented", "fail", "No authorship documentation has been provided.")
            : Check("authorshipDocumented", "warn", "Authorship record exists but has no details.");
    }

    // Mirrors the checklist's hasAiDisclosure: an explicit disclosure — the free
    // AiDisclosureDdex or the TrackAuthorship disclosure — satisfies this. Prompt /
    // process notes deliberately do NOT: an AI-use disclosure has to be explicit.
    private static ComplianceCheck AiDisclosure(TrackAuthorship? a, Track track) =>
        !string.IsNullOrWhiteSpace(track.AiDisclosureDdex)
        || (a is not null && !string.IsNullOrWhiteSpace(a.AiDisclosure))
            ? Check("aiDisclosurePresent", "pass", "An AI-use disclosure has been provided.")
            : Check("aiDisclosurePresent", "fail", "No AI-use disclosure has been provided.");

    // Progressive: anchored (pass) > signed stamp but anchor pending (warn) > nothing (fail).
    private static ComplianceCheck ProvenanceAnchored(ProvenanceAnchor? anchor, bool stamped)
    {
        if (anchor?.Status == "anchored")
            return Check("provenanceAnchored", "pass", "The content hash is anchored on-chain.");
        if (anchor?.Status == "failed")
            return Check("provenanceAnchored", "fail", "Provenance anchoring failed and needs a retry.");
        if (stamped)
            return Check("provenanceAnchored", "warn", "Provenance is signed; on-chain anchoring is pending.");
        return Check("provenanceAnchored", "fail", "This track has not been stamped or anchored.");
    }

    private static ComplianceCheck MetadataComplete(Track track)
    {
        // Concrete required set beyond the always-present title.
        var present = 0;
        var total = 5;
        if (!string.IsNullOrWhiteSpace(track.PrimaryGenre) ||
            !string.IsNullOrWhiteSpace(track.Genre) ||
            !string.IsNullOrWhiteSpace(track.Subgenre)) present++;
        if (!string.IsNullOrWhiteSpace(track.Description)) present++;
        if (!string.IsNullOrWhiteSpace(track.Mood)) present++;
        if (!string.IsNullOrWhiteSpace(track.Tempo)) present++;
        if (!string.IsNullOrWhiteSpace(track.CoverArtUrl)) present++;

        if (present == total)
            return Check("metadataComplete", "pass", "All recommended metadata fields are filled in.");
        if (present * 2 >= total)
            return Check("metadataComplete", "warn", $"Metadata is partially complete ({present}/{total} fields).");
        return Check("metadataComplete", "fail", $"Most metadata fields are missing ({present}/{total} fields).");
    }

    private static ComplianceCheck Check(string name, string status, string detail) =>
        new() { Name = name, Status = status, Detail = detail };

    private static List<ComplianceChecklistItemDto> BuildChecklist(
        Track track,
        TrackAuthorship? authorship,
        ProvenanceAnchor? anchor,
        TrackLyricsDto? lyrics,
        BehindTheTrackDto? creationProcess,
        AuthorshipRecord? authorshipRecord,
        bool hasCreatorProfile)
    {
        var hasAiDisclosure = HasText(track.AiDisclosureDdex) || HasText(authorship?.AiDisclosure);
        var hasHumanContribution = HasNarrativeAuthorship(authorship)
            || HasText(creationProcess?.HumanContributionNotes);
        var hasBehindTheTrackStory = HasText(creationProcess?.Story);
        var hasDawOrTools = HasText(creationProcess?.DAW)
            || HasText(creationProcess?.PromptNotes)
            || HasText(creationProcess?.ProductionNotes)
            || HasAny(creationProcess?.ToolsUsed);
        var hasLyrics = HasText(lyrics?.Lyrics) || authorship?.LyricsAuthored == true;
        var hasStampedProvenance = HasText(track.ContentHash) && HasText(track.Signature);
        var provenanceCompleteAt = anchor?.Status == "anchored"
            ? anchor.AnchoredAt
            : track.SignedAt;

        return new List<ComplianceChecklistItemDto>
        {
            Item(
                "track_title",
                "Track title",
                HasText(track.Title) ? Complete : Incomplete,
                HasText(track.Title)
                    ? "Track title is present."
                    : "Add a track title before publishing.",
                completedAt: HasText(track.Title) ? track.CreatedAt : null),

            Item(
                "creator_profile",
                "Creator profile",
                hasCreatorProfile ? Complete : Incomplete,
                hasCreatorProfile
                    ? "A creator profile exists for this release."
                    : "Create or complete a creator profile so listeners can identify the artist behind the release."),

            Item(
                "audio",
                "Audio file",
                HasText(track.AudioUrl) || HasText(track.ContentHash) ? Complete : Incomplete,
                HasText(track.AudioUrl) || HasText(track.ContentHash)
                    ? "Audio has been uploaded for this release."
                    : "Upload the final audio file for this release.",
                completedAt: HasText(track.AudioUrl) || HasText(track.ContentHash) ? track.CreatedAt : null),

            Item(
                "cover_art",
                "Cover art",
                HasText(track.CoverArtUrl) ? Complete : Incomplete,
                HasText(track.CoverArtUrl)
                    ? "Cover art is present."
                    : "Add cover art before publishing."),

            Item(
                "ai_disclosure",
                "AI disclosure",
                hasAiDisclosure ? Complete : Incomplete,
                hasAiDisclosure
                    ? "AI disclosure is present and satisfies the AI disclosure checklist item."
                    : "Add an AI-use disclosure for this release, including when no generative AI was used.",
                completedAt: HasText(authorship?.AiDisclosure) ? authorship?.UpdatedAt : null),

            Item(
                "lyrics",
                "Lyrics",
                track.Instrumental ? Optional : (hasLyrics ? Complete : Optional),
                track.Instrumental
                    ? "Instrumental releases do not need lyrics."
                    : hasLyrics
                        ? "Lyrics or lyric authorship have been documented."
                        : "Lyrics are optional; add them for vocal releases when you want them visible.",
                completedAt: hasLyrics ? lyrics?.UpdatedAt ?? authorship?.UpdatedAt : null),

            Item(
                "btt_story",
                "Behind the Track story",
                hasBehindTheTrackStory ? Complete : Optional,
                hasBehindTheTrackStory
                    ? "Behind the Track story has been added."
                    : "Behind the Track story is optional and can be added to give listeners more release context.",
                completedAt: hasBehindTheTrackStory ? creationProcess?.UpdatedAt : null),

            Item(
                "human_contribution",
                "Human contribution",
                hasHumanContribution ? Complete : Incomplete,
                hasHumanContribution
                    ? "Human contribution details have been documented."
                    : "Document the human creative work behind this release, such as editing, arrangement, lyrics, or production notes.",
                completedAt: hasHumanContribution ? authorship?.UpdatedAt ?? creationProcess?.UpdatedAt : null),

            Item(
                "daw_tools",
                "DAW and tools",
                hasDawOrTools ? Complete : Optional,
                hasDawOrTools
                    ? "DAW, tools, prompts, or production notes have been documented."
                    : "DAW and tool details are optional, but they help buyers understand how the release was made.",
                completedAt: hasDawOrTools ? creationProcess?.UpdatedAt : null),

            Item(
                "rights",
                "Rights / ownership attestation",
                track.CommercialRightsVerified ? Complete : Incomplete,
                track.CommercialRightsVerified
                    ? "You attested that you control the commercial rights needed to publish this track."
                    : "Confirm that you control the rights needed to publish this track. This is a free creator attestation, not a paid verification."),

            AuthorshipRecordItem(authorshipRecord),

            Item(
                "provenance",
                "Provenance stamp",
                hasStampedProvenance ? Complete : Incomplete,
                ProvenanceExplanation(anchor, hasStampedProvenance),
                completedAt: hasStampedProvenance ? provenanceCompleteAt : null),
        };
    }

    private static ComplianceChecklistItemDto AuthorshipRecordItem(AuthorshipRecord? record)
    {
        if (record?.Status == "issued")
        {
            return Item(
                "authorship_record",
                "Authorship Record (optional paid verification)",
                Complete,
                "Optional paid Authorship Record has been issued for this release.",
                isPaidRequirement: false,
                completedAt: record.IssuedAt);
        }

        var explanation = record?.Status switch
        {
            "pending_payment" => "Optional paid Authorship Record checkout is pending. Publishing and compliance do not require this paid record.",
            "failed" => "Optional paid Authorship Record was not issued. You can retry if you want shareable paid verification; publishing and compliance do not require it.",
            _ => "Optional paid verification: an Authorship Record can add shareable timestamped documentation, but it is not required to publish or satisfy this checklist.",
        };

        return Item(
            "authorship_record",
            "Authorship Record (optional paid verification)",
            OptionalPaidVerification,
            explanation,
            isPaidRequirement: false);
    }

    private static string ProvenanceExplanation(ProvenanceAnchor? anchor, bool hasStampedProvenance)
    {
        if (anchor?.Status == "anchored")
            return "Free provenance stamp exists and the content hash has been batch-anchored.";
        if (anchor?.Status == "failed")
            return "Free provenance stamp exists, but batch anchoring failed and can be retried.";
        if (hasStampedProvenance)
            return "Free provenance stamp exists; batch anchoring may still be pending.";
        return "Generate a free provenance stamp from the uploaded audio hash.";
    }

    private static ComplianceChecklistItemDto Item(
        string key,
        string label,
        string status,
        string explanation,
        bool isPaidRequirement = false,
        DateTime? completedAt = null) =>
        new()
        {
            Key = key,
            Label = label,
            Status = status,
            Explanation = explanation,
            TargetSection = key,
            IsPaidRequirement = isPaidRequirement,
            CompletedAt = completedAt,
        };

    private static bool HasNarrativeAuthorship(TrackAuthorship? authorship) =>
        authorship is not null && (
            HasText(authorship.Edits) ||
            HasText(authorship.ArrangementNotes) ||
            HasText(authorship.ProcessNotes) ||
            authorship.LyricsAuthored);

    private static bool HasAny(IEnumerable<string>? values) =>
        values is not null && values.Any(HasText);

    private static bool HasText(string? value) =>
        !string.IsNullOrWhiteSpace(value);
}
