# Kickoff – instructions for the AI coding agent

**Paste this into Claude Code (or any AI coding tool) to start:**
> Read `docs/KICKOFF.md`, then `docs/SPEC.md` and `docs/CONVENTIONS.md`, and build this project milestone by milestone as described in KICKOFF.md.

---

## Your job
Build `dotnet-ai-support-agent` exactly as described in `docs/SPEC.md`, following `docs/CONVENTIONS.md`.

This is a **.NET 10** project. `SPEC.md` section 4a lists how the CONVENTIONS defaults translate to .NET; where they differ, `SPEC.md` wins. Don't add or swap packages without a `docs/DECISIONS.md` entry explaining why.

## How to work
1. **Plan first.** Before coding, create `docs/architecture.md` (Mermaid diagram, solution and project layout, tool contracts, data model) and `docs/PROGRESS.md` using the milestones below. Commit both.
2. **Check packages before using them.** Confirm current stable versions and APIs on NuGet and Microsoft Learn: `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, `CommunityToolkit.VectorData.PgVector`, `Microsoft.Extensions.AI.Evaluation.*`, NeoUI.Blazor. Pin them in `Directory.Packages.props`. These libraries move fast, so don't rely on memory.
3. **One milestone at a time** on `feat/<milestone>` branches:
   - Small conventional commits.
   - `make check` (build with warnings as errors, `dotnet format --verify-no-changes`, tests) passes before every commit.
   - Merge when the milestone's checks pass.
4. **After each milestone:** update `docs/PROGRESS.md` (status, next step, how to verify, session-log line) and `docs/DECISIONS.md`.
5. **Ask the user only when truly blocked** (for example a missing API key). Otherwise pick the option that fits the spec and record it.
6. **Never commit secrets.** Use user-secrets or `.env` for docker compose. `.env.example` lists every setting.
7. **UI:** match the designs in `docs/ui/`. If that folder has no images yet, use clean NeoUI defaults and leave a TODO in `PROGRESS.md`.

## Milestones
| # | Milestone | Done when |
|---|---|---|
| M0 | **Solution foundation** | Solution and projects per SPEC section 5; `global.json`; `Directory.Packages.props`; analyzers and warnings as errors; `AGENTS.md` (template + gotchas), `CLAUDE.md` (`@AGENTS.md`); `Makefile`; `docker-compose.yml` (app, legacy API, Postgres + pgvector, Aspire Dashboard); `.env.example`; `.gitignore`; CI (build, format, test); MIT `LICENSE`; `CHANGELOG.md`. **NeoUI.Blazor license verified** and recorded in `DECISIONS.md`. `make setup && make check` is green. |
| M1 | **Legacy Orders API** | `Brightline.LegacyOrders` (minimal API, own schema, EF migrations, seed data, API key, OpenAPI) and `Brightline.LegacyOrders.Client` (typed client + resilience). Integration tests with Testcontainers. |
| M2 | **Data, auth, foundation services** | App `DbContext` + migrations, ASP.NET Core Identity (admin/staff), optional Entra ID OIDC by config, Data Protection keys in the database, health checks, rate limiting, security headers, OpenTelemetry to the Aspire Dashboard. |
| M3 | **AI provider + knowledge base** | `Brightline.Ai` (`IChatClient`/`IEmbeddingGenerator` per `Ai:Provider`, Gemini compatibility endpoint by default). Ingestion `BackgroundService` with a Postgres jobs table, chunking, pgvector via `CommunityToolkit.VectorData.PgVector`, hybrid search (RRF), atomic re-index. |
| M4 | **Agent + tools** | Agent Framework agent with the 7 tools. Server-side verification state, approval-required write tools, idempotency keys, injection-safe prompt. Unit tests: forged customer ID ignored, approval needed before writes. |
| M5 | **Customer chat UI** | Blazor chat with streaming, numbered citations, confirmation cards for approvals, "I don't know" + ticket offer, verification flow, legacy-API-down message. bUnit tests. |
| M6 | **Admin console** | Documents and jobs, conversations (tool calls, approvals, tokens), tickets, settings, "Test as customer". Role checks. |
| M7 | **Evaluation** | `tests/Eval`: Microsoft.Extensions.AI.Evaluation quality evaluators on 40 questions plus 8 unanswerable, and 25 agent scenarios with state assertions. `make eval` writes `eval/results.md` and `.json`. |
| M8 | **End-to-end + demo + production profile** | Playwright for .NET end-to-end flow. Demo mode (daily cap, nightly legacy reset). Render (two services) + Neon deploy guide. `deploy/azure/` Bicep (Container Apps, Postgres Flexible with pgvector, Azure OpenAI, Key Vault, managed identity, App Insights), validated in CI, with a deploy workflow disabled by default. README per the CONVENTIONS template, with results and a cost estimate. |
| M9 | **Final check** | Every acceptance criterion in `SPEC.md` section 9 and the CONVENTIONS section 8 checklist ticked in `PROGRESS.md`. A fresh clone passes `make setup && make check`. |

## What the user does (not the agent)
- Gets a **Gemini API key**.
- Creates the **Neon** databases and **Render** services when the deploy step is reached.
- Optionally registers an **Entra ID** app for testing the OIDC sign-in.
- Records the demo video at the end.

When a step needs one of these, stop, list exactly what's needed and where it goes, and continue with unblocked work.
