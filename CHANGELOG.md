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
