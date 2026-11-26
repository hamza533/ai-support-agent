# PROGRESS – handoff log

## Status
- **Current milestone:** M1 – Legacy Orders API
- **Done:** M0 – solution foundation (build, format, 3 architecture tests green; docker compose stack starts: web, legacy API, Postgres 16 + pgvector 0.8.7 with two databases, Aspire Dashboard)
- **In progress:** M1
- **Next:** M2 – Data, auth, foundation services

| # | Milestone | Status |
|---|---|---|
| M0 | Solution foundation | ✅ done |
| M1 | Legacy Orders API | in progress |
| M2 | Data, auth, foundation services | – |
| M3 | AI provider + knowledge base | – |
| M4 | Agent + tools | – |
| M5 | Customer chat UI | – |
| M6 | Admin console | – |
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

Expected: build succeeds with 0 warnings, `dotnet format --verify-no-changes` clean, all tests pass.

## Session log
- 2026-11-26 – Claude Code – plan, architecture.md, package/licence research, M0 scaffold.
