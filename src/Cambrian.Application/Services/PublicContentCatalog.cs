using Cambrian.Application.DTOs.Public;

namespace Cambrian.Application.Services;

/// <summary>
/// Curated, public, evergreen informational content (FAQ + content pages) served to the
/// read-only MCP server and SEO/AI consumers. The copy reflects the current product —
/// creator legitimacy, provenance, Release Ready mastering, and creator subscriptions —
/// and intentionally avoids retired positioning. <see cref="PublicApiService"/> layers the
/// canonical URL and remaining SEO fields on top of these bodies.
/// </summary>
internal static class PublicContentCatalog
{
    /// <summary>Stable last-modified date for evergreen content (bump when copy changes).</summary>
    public static readonly DateTime LastModified = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<PublicFaqItemDto> Faq() => new List<PublicFaqItemDto>
    {
        new()
        {
            Category = "General",
            Anchor = "what-is-cambrian",
            Question = "What is Cambrian?",
            Answer = "Cambrian is a platform for music creators to publish their catalogue with verifiable " +
                     "provenance, prepare release-ready masters, and run a public storefront with creator " +
                     "subscriptions. Each track can carry a free, independently verifiable provenance stamp."
        },
        new()
        {
            Category = "Provenance",
            Anchor = "what-is-a-provenance-stamp",
            Question = "What is a provenance stamp?",
            Answer = "Every track can be hashed (SHA-256) and signed with the platform's ECDSA key the moment " +
                     "it is processed. The stamp is free, instant, and independently verifiable with the public " +
                     "key, so anyone can confirm a file matches what the creator published and when."
        },
        new()
        {
            Category = "Provenance",
            Anchor = "how-do-i-verify-a-track",
            Question = "How can I verify a track's provenance?",
            Answer = "Use the public provenance verification endpoint with the track's content hash and signature. " +
                     "The platform also publishes its public key so verification can be done independently."
        },
        new()
        {
            Category = "Release Ready",
            Anchor = "what-is-release-ready",
            Question = "What is Release Ready?",
            Answer = "Release Ready turns an uploaded track into a distribution-ready master and captures the " +
                     "disclosures distributors expect, including AI-use disclosure. Paid plans include a monthly " +
                     "allotment of Release Ready masters."
        },
        new()
        {
            Category = "Authorship",
            Anchor = "what-is-authorship",
            Question = "What does authorship / commercial-rights attestation mean?",
            Answer = "Creators attest that they hold the commercial rights to what they publish, and disclose any " +
                     "AI involvement. This authorship metadata travels with the track and supports its provenance record."
        },
        new()
        {
            Category = "Pricing",
            Anchor = "how-much-does-it-cost",
            Question = "How much does Cambrian cost?",
            Answer = "There is a free plan for getting started, plus paid Creator and Pro plans that unlock unlimited " +
                     "uploads, more Release Ready masters per month, the full provenance suite, and analytics. " +
                     "See the pricing endpoint for current amounts."
        },
        new()
        {
            Category = "Creators",
            Anchor = "how-do-i-get-paid",
            Question = "How do creators earn on Cambrian?",
            Answer = "Creators sell tracks and offer fan subscriptions from their public storefront. Payouts are " +
                     "handled through the connected payments provider; a transparent platform fee applies per plan."
        },
        new()
        {
            Category = "For AI tools",
            Anchor = "is-the-data-public",
            Question = "Is Cambrian data available to AI tools?",
            Answer = "Yes. Cambrian exposes a public, read-only API of safe, crawlable data — tracks, creators, " +
                     "genres, trending, pricing, and platform stats — with canonical URLs and structured-data hints, " +
                     "designed for SEO and AI assistants."
        },
    };

    public static PublicContentPageDto ReleaseReady() => new()
    {
        Slug = "release-ready",
        Title = "Release Ready",
        Headline = "Turn a track into a distribution-ready master",
        Summary = "Release Ready prepares an uploaded track for distribution and captures the disclosures " +
                  "distributors expect — including AI-use disclosure — alongside its provenance record.",
        Description = "Release Ready prepares masters for distribution and captures required disclosures.",
        Tags = new List<string> { "release ready", "mastering", "distribution", "ai disclosure" },
        Sections = new List<PublicContentSectionDto>
        {
            new()
            {
                Anchor = "overview",
                Heading = "Overview",
                Body = "Release Ready takes a track you have uploaded and produces a clean, distribution-ready " +
                       "master. It records the metadata and disclosures needed downstream so the release is " +
                       "consistent and traceable."
            },
            new()
            {
                Anchor = "whats-included",
                Heading = "What's included",
                Body = "A processed master, captured AI-use disclosure (DDEX-aligned), and a provenance record " +
                       "linking the master to the creator's authorship attestation."
            },
            new()
            {
                Anchor = "credits",
                Heading = "Monthly credits",
                Body = "Paid plans include a monthly allotment of Release Ready masters (Creator and Pro tiers " +
                       "include more). Unused credits do not roll over. See the pricing endpoint for current amounts."
            },
        }
    };

    public static PublicContentPageDto Authorship() => new()
    {
        Slug = "authorship",
        Title = "Authorship & Commercial Rights",
        Headline = "Stand behind what you publish",
        Summary = "Authorship on Cambrian means attesting you hold the commercial rights to a track and " +
                  "disclosing any AI involvement, so the work carries a clear, verifiable origin.",
        Description = "Authorship attestation and AI disclosure that travels with every track.",
        Tags = new List<string> { "authorship", "commercial rights", "ai disclosure", "provenance" },
        Sections = new List<PublicContentSectionDto>
        {
            new()
            {
                Anchor = "attestation",
                Heading = "Commercial-rights attestation",
                Body = "Creators attest that they hold the commercial rights to the material they publish. The " +
                       "attestation is recorded with the track and reflected in its provenance status."
            },
            new()
            {
                Anchor = "ai-disclosure",
                Heading = "AI-use disclosure",
                Body = "Creators disclose whether a track was AI-generated or AI-assisted, with structured, " +
                       "DDEX-aligned detail. This disclosure is surfaced publicly so listeners and tools know the origin."
            },
            new()
            {
                Anchor = "provenance-link",
                Heading = "Linked to provenance",
                Body = "Authorship metadata is bound to the track's content hash and signature, so the claim of " +
                       "origin and the file it describes stay together and verifiable."
            },
        }
    };

    public static PublicContentPageDto CreatorGuide() => new()
    {
        Slug = "creator-guide",
        Title = "Creator Guide",
        Headline = "From upload to a storefront people can trust",
        Summary = "A step-by-step guide to publishing on Cambrian: upload, get a provenance stamp, attest " +
                  "authorship, prepare Release Ready masters, and grow a public storefront with subscriptions.",
        Description = "How to publish, prove provenance, and grow a storefront on Cambrian.",
        Tags = new List<string> { "creator guide", "getting started", "storefront", "provenance" },
        Sections = new List<PublicContentSectionDto>
        {
            new()
            {
                Anchor = "upload",
                Heading = "1. Upload your track",
                Body = "Add your audio, cover art, title, genre, mood, and tags. Good metadata makes your work " +
                       "discoverable in search and to AI assistants."
            },
            new()
            {
                Anchor = "provenance",
                Heading = "2. Get your provenance stamp",
                Body = "Your track is hashed and signed automatically, giving it a free, verifiable provenance " +
                       "stamp the moment it is processed."
            },
            new()
            {
                Anchor = "authorship",
                Heading = "3. Attest authorship",
                Body = "Confirm you hold the commercial rights and disclose any AI involvement so your release " +
                       "carries a clear origin."
            },
            new()
            {
                Anchor = "release-ready",
                Heading = "4. Go Release Ready",
                Body = "Use a Release Ready credit to produce a distribution-ready master with the disclosures " +
                       "distributors expect."
            },
            new()
            {
                Anchor = "storefront",
                Heading = "5. Build your storefront",
                Body = "Customise your public profile, pin your best work, and offer fan subscriptions. Your " +
                       "storefront has a canonical URL that search engines and AI tools can index."
            },
        }
    };
}
