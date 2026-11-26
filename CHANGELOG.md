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
