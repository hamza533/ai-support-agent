# PROGRESS – handoff log

## Status
- **Current milestone:** M9 – Final check
- **Done:** M0 foundation; M1 legacy Orders API + typed client; M2 app DB (EF migrations incl. pgvector/tsvector), Identity (admin/staff, optional Entra ID), Data Protection keys in DB, Postgres job queue + worker + retention purge, health/ready, rate limiting, security headers/CSP, OpenTelemetry; M3 AI provider switch (Gemini compat / Azure OpenAI / OpenAI) + knowledge base (PDF/DOCX/MD parsing, chunking, pgvector + FTS hybrid search with RRF, atomic re-index, durable ingestion jobs, 10 synthetic help-centre docs); M4 Agent Framework agent with 7 tools, server-side verification, approval-gated writes with server-built confirmation cards, idempotency keys, injection-safe prompt, citation binding; M5 customer Help Center + streaming chat (citations, verified chip, order card, confirmation cards, What I did, ticket offer, order-system-down state, cold-start message) with bUnit tests; M6 admin console (overview stats + chart, conversations with transcript/citations/agent trace/tokens/cost, tickets with drawer, documents + live ingestion jobs with retry, settings with admin-only save and locked approvals, staff users, Test as customer); M7 eval harness (40+8 questions with AI.Evaluation quality evaluators, 25 agent scenarios with DB + legacy-state assertions, performance metrics, results.md/json writer); M8 Playwright E2E (chat → verify → return with confirmation → staff sees ticket, real browser), demo mode (cap, nightly reset, read-only staff), render.yaml + Render/Neon guide, Azure Bicep (validated: build + lint), CI (e2e, images + Trivy, Bicep), disabled-by-default deploy workflow, README; M9 independent code review with fixes and regression tests (128 tests + E2E green)
- **In progress:** – (all build work done; remaining items need the user: Gemini key for `make eval`, GitHub remote, Render/Neon deploy, GIF/video)
- **Next:** run `make eval` with a Gemini key and put the numbers in README; deploy the demo; record GIF/video

| # | Milestone | Status |
|---|---|---|
| M0 | Solution foundation | ✅ done |
| M1 | Legacy Orders API | ✅ done |
| M2 | Data, auth, foundation services | ✅ done |
| M3 | AI provider + knowledge base | ✅ done |
| M4 | Agent + tools | ✅ done |
| M5 | Customer chat UI | ✅ done |
| M6 | Admin console | ✅ done |
| M7 | Evaluation | ✅ harness done · numbers need a Gemini key |
| M8 | End-to-end + demo + production profile | ✅ done |
| M9 | Final check | ✅ done (independent review fixed; 128 tests + E2E green; fresh clone verified) · eval numbers pending key |

## Acceptance checklist (SPEC section 9)
- [x] `make setup && make dev` starts app, legacy API, Postgres and Aspire Dashboard; `make seed` loads documents and legacy data. *(Gemini key needed for live answers.)*
- [ ] Policy questions answered with valid citations; unanswerable → "I don't know" + ticket offer. *Built and tested with a scripted model (bUnit + E2E); real-model numbers need `make eval` with a key.*
- [x] Order tools refuse without verification; with order number + email, status / order list / eligibility are correct against the legacy API (unit + integration tests).
- [x] `create_return_request` / `create_support_ticket` show a confirmation card and run only after Confirm; Cancel cancels; retries don't duplicate (unit, integration, bUnit, E2E).
- [x] Legacy API reached only through the typed client over HTTP; architecture tests fail if the app references the legacy project or its connection string.
- [x] Legacy API stopped → retries, circuit breaker, "can't reach the order system" + ticket offer (resilience unit tests + agent tests + scenario S21).
- [x] Admin console shows conversations with tool calls, approvals and token usage; ingestion jobs visible and durable across restarts (Postgres jobs table, lease recovery test).
- [x] Traces (agent steps, tool calls, LLM calls) exported via OTLP to the Aspire Dashboard (`Experimental.Microsoft.Agents.AI*`, `Microsoft.Extensions.AI*`, `Brightline.*` sources).
- [ ] `make eval` reproduces README numbers with 100% verification, 100% approvals, 0 leaks, 0 duplicates. *Harness ready; needs a Gemini key to produce `eval/results.md`.*
- [x] `Ai:Provider` switch between `gemini-openai-compat` and `azure-openai` needs no code change (configuration test).
- [x] Production readiness: EF migrations · Data Protection keys in DB · rate limiting · `/health` + `/ready` · security headers/CSP · Key Vault in production (Bicep) · optional Entra ID · Playwright E2E · Dependabot, CodeQL, Trivy · Bicep validated in CI with a disabled deploy workflow · README production section with 1k/10k cost estimate.
- [~] UI matches `docs/ui/`: built from `docs/ui/STITCH_PROMPT.md` (all 7 screens, same story data) with NeoUI.Blazor; exported Stitch images not available to compare.
- [x] MCP: only `neon` (read-only) in `.mcp.json`, added at the deploy step.
- [x] `AGENTS.md` Gotchas cover verification, approvals, legacy boundary, Gemini compat + provider switch, pgvector dimension, WebSockets/sticky sessions, Data Protection keys, Render cold starts.

## Done checklist (CONVENTIONS section 8)
- [ ] Every SPEC acceptance criterion passes – two items wait on a Gemini key (eval numbers).
- [x] Production readiness (CONVENTIONS 10): met or covered; `deploy/azure/` Bicep validates (build + lint) in CI.
- [x] Handoff: `AGENTS.md`, `CLAUDE.md` (`@AGENTS.md`), Makefile targets, up-to-date PROGRESS; fresh clone `make setup && make check` green (verified in the toolbox container).
- [x] `docker compose up` works on a clean machine (verified with a fresh volume).
- [ ] Evaluation numbers in README – needs a Gemini key.
- [ ] CI green – needs a GitHub remote (workflows are in place).
- [ ] Demo GIF + 60–90 s video – user records.
- [ ] Live demo deployed – user creates Neon + Render (guide: docs/deploy-render-neon.md).
- [ ] Upwork case study – after eval numbers.

## What the user does
1. Add `Ai__Gemini__ApiKey` to `.env`, run `make dev` then `make eval`; commit `eval/results.md` + `.json` and copy the summary into the README Results table.
2. Push to GitHub (enables CI, CodeQL, Dependabot).
3. Create Neon databases + Render services (docs/deploy-render-neon.md); add the demo link and demo staff credentials to the README.
4. Record the demo GIF (`docs/images/demo.gif`) and a 60–90 s video.
5. Optional: export the Stitch screens into `docs/ui/` for a pixel comparison; optional Entra ID app registration.

## Blockers / open questions
- **UI designs:** the Stitch project (stitch.google.com/projects/1080427977122841862) could not be read automatically
  (screens render in a cross-origin frame). TODO: export each screen as PNG (+HTML) into `docs/ui/`
  (`01-help-center.png` … `07-settings.png`, chat states `02a`–`02d`). Until then the UI follows
  `docs/ui/STITCH_PROMPT.md` with NeoUI defaults.
- **Gemini API key** needed for live chat and for `make eval` numbers (`Ai__Gemini__ApiKey` in `.env`). All automated tests run without it. Without a key the app starts, indexes documents for keyword search only, and the chat explains that the assistant isn't configured.
- **No git remote yet:** milestones are merged locally with `--no-ff`. Add a GitHub remote to get CI, CodeQL and Dependabot running.
- **Local toolchain:** the .NET SDK and `make` are not installed on the dev machine; everything runs in the
  `docker/dev.Dockerfile` toolbox via `scripts/dev.ps1` / `scripts/dev.sh` (see "How to verify").

## How to verify
With a local .NET 10 SDK + make:
```
make setup && make check
```
Without them (Docker only):
```
./scripts/dev.ps1 make setup check      # PowerShell
./scripts/dev.sh make setup check       # bash
```
Stack: `make dev` → app http://localhost:5080/health, legacy API http://localhost:5081/health, Aspire Dashboard http://localhost:18888, Postgres on host port 55432.

Legacy API by hand (with `make dev` running):
```
curl -X POST localhost:5081/api/verifications -H "X-Api-Key: dev-legacy-api-key-change-me" -H "Content-Type: application/json" -d '{"orderNumber":"BL-10482","email":"maya.t@example.com"}'
```
→ `{"customerId":1,"firstName":"Maya"}`. OpenAPI UI: http://localhost:5081/scalar. Integration tests need Docker (Testcontainers).

Expected: build succeeds with 0 warnings, `dotnet format --verify-no-changes` clean, all tests pass.

## Session log
- 2026-11-26 – Claude Code – plan, architecture.md, package/licence research, M0 scaffold.
- 2026-11-26 – Claude Code – M1: legacy Orders & Returns API (EF migrations, deterministic seed, API key, OpenAPI/Scalar, idempotent returns, reset) and typed client with standard resilience; Testcontainers integration tests.
- 2026-11-26 – Claude Code – M2: app schema + migrations, Identity/Entra, Data Protection, job queue (SKIP LOCKED, backoff, dead-letter, lease recovery), retention, health/ready, rate limiting, CSP, OTel; integration tests.
- 2026-11-27 – Claude Code – M3: Brightline.Ai provider registration (config switch test), Brightline.Knowledge ingestion + hybrid search, sample help-centre docs, shared test fakes.
- 2026-11-27 – Claude Code – M4: agent + tools (Brightline.Agent), ChatService streaming/approvals, conversation audit store; unit + integration tests with scripted model and fake legacy API.
- 2026-11-27 – Claude Code – M5: Help Center home/articles, ChatPanel (streaming, citations, approvals, errors), safe Markdown, keyword-only indexing without a key, bUnit tests; verified in the browser against docker compose.
- 2026-11-27 – Claude Code – M6: admin console pages (AdminService read models), role checks (staff read, admin change), integration tests for every page; verified in the browser.
- 2026-11-27 – Claude Code – M7: tests/Brightline.Eval harness (quality evaluators, 25 scenarios, metrics, report), eval/README.md; flaky-test fixes (DB timeouts, ranking tie). Real numbers pending a Gemini key.
- 2026-11-27 – Claude Code – M8: E2E (Playwright, scripted model, screenshots), demo mode services, render.yaml + docs/deploy-render-neon.md, deploy/azure Bicep + CI validation, deploy workflow, README; fixed admin transcript HTML rendering and empty usage messages.
- 2026-11-27 – Claude Code – M9: independent security/correctness review; fixed parallel-call approvals, outage cards, citation stability, line-keyed idempotency + legacy row lock, job-queue poison/lease/fencing, verification oracle, ticket order ownership, link allow-list, admin hardening; regression tests; fresh-clone `make setup && make check` verified.
