# Homestead v2 planning

These documents were imported from `Homestead-v2-planning.zip` and reviewed on
2026-10-07. The three supplied files are preserved without content changes.

| Document | Purpose |
|---|---|
| [Product specification](SPEC.md) | Defines the v0.1 product scope and acceptance criteria. |
| [Technical architecture](ARCHITECTURE.md) | Describes the proposed implementation and operational boundaries. |
| [Implementation plan](IMPLEMENTATION.md) | Orders implementation into foundation, article publishing, public experience, remaining post types, and portability/release phases. |

The implementation plan identifies the product specification as the authoritative
requirements document. Its coding-agent instructions and example first assignment
are planning content for future work; importing and reviewing them does not start
those assignments.

## Review

The documents agree on the central product and technical choices: one administrator,
five fixed post types, one theme, Markdown authoring, a single ASP.NET Core application,
SQLite and local media, and complete static generation on public changes. They also
consistently separate editable drafts from published snapshots and distinguish
static export, Hugo export, and full backup.

The plan includes acceptance checks for privacy, failed generation, mobile authoring,
independent exports, and restore. Its article publishing milestone provides an early
end-to-end checkpoint before adding the remaining experiences.

The following details should be clarified in the relevant implementation task:

1. **Hugo export inclusion and content source (E02).**
   [SPEC section 10.B](SPEC.md#b-hugo-export) requires all nondeleted posts, while
   [ARCHITECTURE section 8](ARCHITECTURE.md#hugo-zip) says unpublished posts "may be
   included." Make their inclusion mandatory to match the specification. Also
   define whether a published post with pending edits exports its working draft or
   published snapshot, and how incomplete drafts receive bundle names and required
   front matter before their first publication. Add export fixtures for each case.

2. **Canonical URL precedence (F04/W03).**
   [ARCHITECTURE section 9](ARCHITECTURE.md#configuration-contract-initial) requires
   `HOMESTEAD_BASE_URL`, while [section 5](ARCHITECTURE.md#content-and-asset-rules)
   generates metadata from `SiteSettings.CanonicalBaseUrl`. The specification also
   exposes the URL as an editable setting. Define whether the environment value is
   an initial default or a persistent override, including restart behavior after
   an administrator changes the setting.

3. **Active publication evidence and retry behavior (P04/P05).**
   [ARCHITECTURE section 4](ARCHITECTURE.md#publish-transaction-and-consistency)
   commits desired snapshots before activation and requires accurate active-public
   status after failure or restart. The example `current.json` records only a
   generation ID. Define where each generation's post membership and content/settings
   revisions are recorded so recovery can distinguish active content from pending
   changes. Specify whether an unrelated later publish retries all previously
   failed desired changes, including unpublish/delete, and test that behavior.

4. **First empty-site generation (F03/P04).**
   [ARCHITECTURE section 5](ARCHITECTURE.md#content-and-asset-rules) calls for an empty
   public homepage immediately after setup. [IMPLEMENTATION Phase 1](IMPLEMENTATION.md#phase-1--foundation-and-deployable-application)
   defers public publishing, and the generator arrives in P04. Identify when the
   empty generated homepage becomes an acceptance requirement so the foundation
   phase does not need the full publishing pipeline prematurely.

## Repository placement

The architecture's suggested tree places these files at the repository root. This
import keeps them together under `docs/planning/`; their companion links still
resolve, and the root README links to all three. Future task references should use
these repository paths.

This review checks the documents' scope, internal consistency, and implementation
handoff. No application build, runtime acceptance checks, or external API/version
verification were performed as part of this documentation import.
