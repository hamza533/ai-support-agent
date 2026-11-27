# Changelog
All notable changes to this project are documented here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]
### Added
- M0 solution foundation: .NET 10 solution (`Brightline.slnx`) with the seven `src/` projects, central package management,
  analyzers with warnings as errors, Makefile, dev toolbox container, docker compose (web, legacy API, Postgres + pgvector,
  Aspire Dashboard), CI (build, format, test, image scan), CodeQL, Dependabot, legacy-boundary architecture tests.
- M1 legacy Orders & Returns API (`Brightline.LegacyOrders`): own database + EF migrations, deterministic seed (50 customers,
  150 orders, demo story), API key auth, OpenAPI + Scalar, idempotent return creation, demo reset endpoint, health/ready.
- M1 typed client (`Brightline.LegacyOrders.Client`) with the standard resilience pipeline; Testcontainers integration tests.
- M2 app database (EF migrations, pgvector + generated tsvector), ASP.NET Core Identity (admin/staff) with optional Entra ID,
  Data Protection keys in Postgres, durable job queue + worker + retention purge, /health + /ready, rate limiting,
  security headers + CSP, OpenTelemetry (Aspire Dashboard / Application Insights), `--migrate` / `--create-admin`.
- M3 `Brightline.Ai` (IChatClient / IEmbeddingGenerator per `Ai:Provider`, throttle, timeouts/retries) and
  `Brightline.Knowledge` (PDF/DOCX/Markdown parsing with magic-byte checks, chunking, pgvector + full-text hybrid
  search merged with RRF, atomic re-index, ingestion jobs); synthetic help-centre documents in `sample_data/`.
- M4 `Brightline.Agent`: Agent Framework agent with 7 tools, server-side verification, approval-required writes with
  server-built confirmation cards, idempotency keys, untrusted-data wrapping, citation binding, conversation audit trail.
- M5 customer Help Center (home, search, articles) and streaming chat panel with numbered citations, verification chip,
  order card, confirmation cards, "What I did" steps, ticket offer, error and cold-start states; bUnit component tests.
- M6 admin console: overview (stats, conversations-per-day chart, recent agent actions, needs attention), conversations
  (transcript, citations, agent trace with masked inputs and approvals, tokens, cost), tickets (drawer with status +
  notes), documents (upload, re-index, delete, live ingestion jobs with retry), settings (agent, policies, locked
  approvals, access), "Test as customer"; role checks (staff read, admin change).
- M7 evaluation harness (`make eval`): AI.Evaluation groundedness/relevance/completeness on 40 questions, "I don't know"
  on 8 unanswerable, 25 agent scenarios with database and legacy-API state assertions, TTFT/tokens/cost, results.md/json.
- M8 Playwright end-to-end test (real browser, both apps on Kestrel, scripted model) + README screenshots; demo mode
  (daily cap, nightly legacy reset, read-only demo staff); `render.yaml` + Render/Neon guide; Azure Bicep production
  profile validated in CI; disabled-by-default deploy workflow; full README.
### Fixed
- Admin transcript rendered escaped HTML for assistant messages; tool-only turns no longer store empty messages.
- Independent review fixes: parallel read+write tool calls no longer produce a blocked card; order-system outage while
  building a confirmation card shows a ticket offer; citation numbers stay stable across turns; return idempotency keyed
  on the order line + legacy row lock against concurrent over-returns; poison jobs dead-letter, lease heartbeat and
  fencing; re-index failures keep the old version; verification no longer acts as an oracle; ticket order links are
  ownership-checked; model links limited to this site; admin "Add user" can't reset accounts; forwarded headers trusted
  only when configured.
