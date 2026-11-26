# PROGRESS – handoff log

## Status
- **Current milestone:** M6 – Admin console
- **Done:** M0 foundation; M1 legacy Orders API + typed client; M2 app DB (EF migrations incl. pgvector/tsvector), Identity (admin/staff, optional Entra ID), Data Protection keys in DB, Postgres job queue + worker + retention purge, health/ready, rate limiting, security headers/CSP, OpenTelemetry; M3 AI provider switch (Gemini compat / Azure OpenAI / OpenAI) + knowledge base (PDF/DOCX/MD parsing, chunking, pgvector + FTS hybrid search with RRF, atomic re-index, durable ingestion jobs, 10 synthetic help-centre docs); M4 Agent Framework agent with 7 tools, server-side verification, approval-gated writes with server-built confirmation cards, idempotency keys, injection-safe prompt, citation binding; M5 customer Help Center + streaming chat (citations, verified chip, order card, confirmation cards, What I did, ticket offer, order-system-down state, cold-start message) with bUnit tests (85 tests green)
- **In progress:** M6
- **Next:** M7 – Evaluation

| # | Milestone | Status |
|---|---|---|
| M0 | Solution foundation | ✅ done |
| M1 | Legacy Orders API | ✅ done |
| M2 | Data, auth, foundation services | ✅ done |
| M3 | AI provider + knowledge base | ✅ done |
| M4 | Agent + tools | ✅ done |
| M5 | Customer chat UI | ✅ done |
| M6 | Admin console | in progress |
| M7 | Evaluation | – |
| M8 | End-to-end + demo + production profile | – |
| M9 | Final check | – |

## Blockers / open questions
- **UI designs:** the Stitch project (stitch.google.com/projects/1080427977122841862) could not be read automatically
  (screens render in a cross-origin frame). TODO: export each screen as PNG (+HTML) into `docs/ui/`
  (`01-help-center.png` … `07-settings.png`, chat states `02a`–`02d`). Until then the UI follows
  `docs/ui/STITCH_PROMPT.md` with NeoUI defaults.
- **Gemini API key** needed from M3 on for live runs (`Ai__Gemini__ApiKey` in `.env` / user-secrets). Tests don't need it.
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
