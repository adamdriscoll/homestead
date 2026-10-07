# Phase 1 implementation decisions

F01–F05 implement the deployable foundation described in
[the implementation plan](planning/IMPLEMENTATION.md#phase-1--foundation-and-deployable-application).

| Task | Delivered |
|---|---|
| F01 | .NET 10 solution, Razor Pages shell, Tailwind/daisyUI build, xUnit integration host, shared quality settings, CI. |
| F02 | SQLite context and initial migration, five-type domain, separate drafts/snapshots, media/tag/step associations, UTC converters, unique keys and database checks. |
| F03 | Identity administrator, secret-protected setup, database-enforced single account, login lockout/rate limiting, POST logout, antiforgery, protected admin routes, persisted keys, trusted proxy allowlist. |
| F04 | Responsive dashboard with five disabled create actions; persistent validated site settings. |
| F05 | Multi-stage non-root image, persistent volume, Caddy Compose deployment, startup migration, liveness/readiness, deployment smoke test. |

## Decisions resolving planning ambiguities

`HOMESTEAD_BASE_URL` initializes the first singleton settings row. Thereafter the
database's canonical URL is authoritative and edits survive restart even when the
environment value differs. The environment value remains required and validated
for deployment configuration. Canonical URLs must use HTTP(S), have no credentials,
query, or fragment, and refer to the domain root. Production requires HTTPS.

The empty generated public homepage is a Phase 2 P04 acceptance criterion. Phase 1
explicitly requires no public publication. It supplies a fixed welcome shell at
`/`, without consulting the content database, and an admin status saying no public
generation exists. It does not start the renderer or generation pipeline early.

Settings store the basic identity, About Markdown, bio, footer, time zone, and fixed
accent preset. Avatar/logo/header media references exist in the schema; selecting
images waits for Phase 2's authenticated upload support. Accent settings will apply
to generated output when that pipeline is implemented. There is no theme picker.

Review rating can be null so incomplete drafts can be persisted, but any entered
rating must be 1–5. Publication-time required fields belong to later phases. Slugs
can be null before publication; once the first-publication timestamp is set, a
SQLite trigger prohibits changing or clearing that timestamp or slug.

## Acceptance evidence

Verified locally on 2026-10-07:

- Restore and Release build passed with no warnings or errors.
- All 39 domain and integration tests passed.
- Formatting verification and migration/model consistency checks passed.
- The Linux Docker image built and the production Compose smoke test passed over local HTTPS, including container recreation without a setup secret.
- Playwright checked setup, sign-in, dashboard, and settings at 390px phone width; the settings form saved with blank optional fields. The dashboard has five disabled create actions and no horizontal overflow. Desktop layout was checked at 1440px.

Run the commands in [contributing](contributing.md) and the isolated deployment
test in [deployment](deploy.md#repeatable-deployment-smoke-test). Tests cover fresh
migration and restart, slug/tag/rating constraints, foreign keys, snapshot isolation,
single-account setup races, missing/invalid secret rejection, setup closure,
authentication and CSRF, secure cookies, untrusted forwarded headers, private paths,
canonical URL/preset/time-zone validation, and settings/session persistence.

Public-domain DNS and ACME issuance are an operator check. The automated deployment
test uses local Caddy TLS and exercises the same reverse-proxy topology and persisted
volume. Phone-sized layout checks complement the HTTP acceptance suite; actual
mobile authoring is required by Phase 2 when the editor exists.
