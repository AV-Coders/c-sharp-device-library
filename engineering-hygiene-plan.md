# Plan: Engineering hygiene

**Status: READY TO IMPLEMENT** — all numbers below were measured on 2026-06-12 (warning
counts via instrumented builds in an isolated worktree; CI/style facts from repo survey).

## Context

The 2026-06-10 code review (code-review.md) flagged a cluster of "enforcement and
completeness" gaps: no analyzers, no warnings-as-errors, no XML docs, no coverage
reporting, divergent CI version computation, and missing hygiene files (.editorconfig,
dependabot, global.json, CHANGELOG, CONTRIBUTING). This plan sequences that work using
measured counts, so each phase is sized and the build gets stricter without ever breaking.

**Guiding principle — ratchet, never regress:** enforcement (errors, severities) is turned
on only after its warning category is at zero. Measured state:

| Configuration | Warnings |
| --- | --- |
| Today (no changes) | **23** |
| + `EnableNETAnalyzers`, `AnalysisLevel=latest-recommended` | **847** (+824) |
| + `GenerateDocumentationFile` everywhere | **3,142** (+2,295 CS1591: 1,715 production / 580 test) |
| `latest-all` instead of latest-recommended | +433 over recommended — **not worth it; stay on recommended** |

The 824 analyzer warnings bucket cleanly: **CA1707 = 445** (all xUnit `Method_Scenario`
test names — one editorconfig line), **CA1051 = 128** (public/protected instance fields on
published API — a semver decision, not a sweep), **culture cluster CA1304/05/10/11 = 126**
(mechanical `Ordinal`/`InvariantCulture` — correct for ASCII device protocols), leaving
**~125 scattered** small fixes. Three buckets cover 85% of the fallout.

## Phase 0 — One-file additions (zero build risk, one sitting)

1. **`.editorconfig`** codifying existing practice — 4-space indent (including csproj/yml),
   file-scoped namespaces, **Allman braces**, `_camelCase` private fields, PascalCase
   consts/statics, expression bodies for one-liners, `var` only when type is apparent,
   `trim_trailing_whitespace`, `insert_final_newline`. All style severities `silent`
   initially (the style survey found the codebase is already consistent; the file exists to
   prevent drift, not to generate work). Include now, scoped to test projects:
   `dotnet_diagnostic.CA1707.severity = none` — pre-clears 445 of the Phase 3 warnings.
2. **`global.json`** — pin SDK 8.0.x, `rollForward: latestMinor`. (CI installs 6.0.x and
   8.0.x; confirm whether 6.0.x is still needed — nothing targets net6.0 — and drop it.)
3. **`.github/dependabot.yml`** — weekly `nuget` + `github-actions` ecosystems.
4. **`CONTRIBUTING.md`** — build/test instructions, test conventions (xUnit/Moq,
   TestFactory, loopback transport tests), commit/PR expectations, versioning explanation.
5. **`CHANGELOG.md`** — seed with the recent fixes; adopt GitHub Releases auto-generated
   notes as the ongoing mechanism (cheapest sustainable option).
6. **Centralise duplicated MSBuild properties** — `ImplicitUsings`, `Nullable`,
   `LangVersion`, `TargetFramework` are repeated identically in all 27 csproj files; move
   to `Directory.Build.props`, delete from csproj. While there: drop `LangVersion=default`
   (it's a no-op) entirely.

## Phase 1 — CI correctness and reproducibility

1. **Shared `compute-version` job** — the gap both existing plans noted but didn't close:
   `build` uses the monthly zero-based counter (dotnet.yml:25-47) while `publish-nuget`
   uses `date +%Y.%m` + repo-wide `run_number` (dotnet.yml:88-93). These WILL drift (the
   run_number never resets monthly and counts all workflows). Factor the monthly-counter
   computation into one job with a `version` output; both jobs consume it. Stable/beta
   suffix stays a per-job concern.
2. **`nuget.config`** — centralise the GitHub Packages source currently hardcoded in
   workflow steps (dotnet.yml:67-68).
3. **Coverage** — coverlet.collector is already referenced by every test project; add
   `--collect:"XPlat Code Coverage"` to the test step and publish the report (Codecov if
   the repo is OK with the external service, otherwise an actions artifact + summary).
4. **Package quality** — in `Directory.Build.props`: `Deterministic`,
   `ContinuousIntegrationBuild` (CI-only), SourceLink (`Microsoft.SourceLink.GitHub`) +
   symbol packages (`snupkg`). Add a `dotnet package validate` step post-pack.

## Phase 2 — Baseline warnings to zero, then lock (23 warnings)

Fix the existing 23 — several are real bugs, not noise:

| Bucket | Count | Action |
| --- | --- | --- |
| CS4014 fire-and-forget | 6 | 3 are the `~IpComms` finalizer calling async `Stop()` — already slated for the `IAsyncDisposable` migration (Sprint 1, code-review.md); the SSH/TCP/Dsp ones get explicit `_ =` with a comment or proper awaits |
| CS0108 member hiding | 4 | One fix: `Display` re-declares `LogBase`'s `Events`/`EventsUpdated`/`ClearEvents`/`AddEvent` — decide override vs rename once |
| CS8600/8604/8618 nullability | 7 | Genuine null-handling fixes (Eaton.cs, Phonebook.cs, phonebook parser test) |
| CS0809/CS0618 SNMP obsolete | 4 | SHA-1 is device-protocol-mandated for SNMPv3 — suppress locally with justification; fix the obsolete-override pattern |
| CS0414 dead field, xUnit2013 | 2 | Delete / `Assert.Single` |

Then set **`TreatWarningsAsErrors=true`** in `Directory.Build.props`. From this point the
baseline cannot regress. (Order matters: this flips BEFORE analyzers are enabled, so the
error gate only ever guards a clean set.)

## Phase 3 — Analyzers at latest-recommended (824 → 0, bucketed)

Enable `EnableNETAnalyzers=true`, `AnalysisLevel=latest-recommended`,
`EnforceCodeStyleInBuild=true` (free — zero IDE warnings today) **as warnings first**
(`CodeAnalysisTreatWarningsAsErrors=false` until each bucket is cleared):

1. **CA1707 (445)** — already suppressed for tests by the Phase 0 .editorconfig. Done.
2. **Culture cluster (126)** — mechanical sweep adding `StringComparison.Ordinal` /
   `CultureInfo.InvariantCulture`; correct semantics for ASCII device protocols, one PR.
3. **CA1051 (128) — decision required, not a sweep**: public/protected instance fields on
   the published API surface (device base classes; includes the delegate-field pattern the
   review flagged as H9). Converting to properties/events is **semver-breaking**.
   Recommendation: suppress now (`dotnet_diagnostic.CA1051.severity = none` with a comment),
   and fold the conversion into the planned delegate-field → `event` migration as one
   deliberate breaking release.
4. **Scattered ~125** — mostly mechanical (CA1822 static, CA1805 redundant init, CA1854
   TryGetValue, CA1861 static readonly arrays, CA1725 param names). Worth genuine review:
   **CA1001** (7 types own a `CancellationTokenSource` without IDisposable — feeds the
   IAsyncDisposable work), **CA5351** (PjLink MD5 — mandated by the PJLink auth spec,
   suppress with justification), CA2201, CA1816.
5. When a project hits zero, flip analyzer warnings to errors for it; when all do, set it
   globally. Do NOT move to `latest-all` (+433 warnings, low signal).

## Phase 4 — XML documentation (1,715 members, the long tail)

1. Enable generation for production only:
   `<GenerateDocumentationFile Condition="!$(MSBuildProjectName.EndsWith('Test'))">true</...>`
   (excludes all 580 test-project CS1591s — every test project ends in `Test`).
2. Add `CS1591` to `NoWarn` globally, then **remove it per project as that project is
   documented** (the ratchet). Priority order by dependency + size: **Core (280)** first —
   every package's consumers see its IntelliSense — then Display (220), Matrix (244),
   Dsp (196) — these four are 55% of the total — then the remaining domains opportunistically.
3. Docs standard: every public type/member on published packages gets `<summary>`; protocol
   quirks (framing, auth, port numbers) belong in driver class docs — the device-doc
   verification notes from tcp-framing-plan.md are source material.

## Phase 5 — Versioning semantics (decision, then small)

`YYYY.MM.X` carries no breaking-change signal. Options: (a) document "API is
date-versioned; treat minor-month bumps as potentially breaking" in README+CONTRIBUTING,
or (b) move the NuGet.org stable channel to semver. Recommendation: (a) now — honest and
zero-cost — and revisit (b) when the CA1051/event-migration breaking release happens, which
is the natural moment to cut a real major version.

## Sequencing and effort

| Phase | Size | Dependencies |
| --- | --- | --- |
| 0 — hygiene files + props centralisation | ~half a day | none |
| 1 — CI version job, coverage, SourceLink | ~half a day + CI iteration | none (parallel with 0) |
| 2 — 23 warnings → TreatWarningsAsErrors | ~half a day | finalizer items overlap IAsyncDisposable work |
| 3 — analyzers (379 real after suppressions) | 2–3 focused passes | Phase 0 (.editorconfig), Phase 2 (error gate first) |
| 4 — XML docs (1,715, ratcheted per project) | ongoing, Core first | independent |
| 5 — versioning statement | minutes | decision only |

## Non-goals

- `AnalysisLevel=latest-all` — measured +433 warnings over recommended for marginal value.
- StyleCop.Analyzers — the built-in analyzers + .editorconfig cover the need without a new
  dependency and its opinionated defaults.
- Fixing CA1051 by sweep — public-field → property conversion on a published API is a
  breaking release decision, handled with the event migration, not hygiene.
- Documenting test projects (CS1591 excluded by the Test-suffix condition).
