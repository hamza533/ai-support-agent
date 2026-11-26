# AGENTS.md
## Project
Add an AI agent to your existing .NET systems: it answers from your documents and safely acts through your current APIs.
Full requirements: docs/SPEC.md. Shared rules: docs/CONVENTIONS.md (the .NET overrides are in SPEC section 4a).

## Start here
1. Read docs/PROGRESS.md (current status + next step).
2. Read docs/SPEC.md, docs/architecture.md and docs/DECISIONS.md before changing the stack.

## Commands
make setup | make dev | make test | make lint | make check | make eval | make seed

No local .NET 10 SDK or make? Run the same targets in the dev toolbox container:
`./scripts/dev.sh make setup check` (bash) or `./scripts/dev.ps1 make setup check` (PowerShell).

## Structure
- `src/Brightline.Web` – Blazor Web App (Interactive Server): customer chat, admin console, auth, health
- `src/Brightline.Agent` – Agent Framework agent, the 7 tools, prompts, server-side conversation state
- `src/Brightline.Ai` – `IChatClient` / `IEmbeddingGenerator` registration per `Ai:Provider`
- `src/Brightline.Knowledge` – ingestion (PDF/DOCX/MD), chunking, hybrid search (pgvector + FTS, RRF)
- `src/Brightline.Data` – EF Core context, migrations, Postgres job queue
- `src/Brightline.LegacyOrders` – stand-in "existing" Orders & Returns API (own DB)
- `src/Brightline.LegacyOrders.Client` – typed HTTP client + resilience (the only way to reach the legacy API)
- `tests/` – Unit, Integration (Testcontainers), Components (bUnit), E2E (Playwright), Eval (AI.Evaluation)
- `deploy/azure/` – Bicep production profile · `docker/` – Dockerfiles · `eval/` – committed results

## Rules
- Follow docs/SPEC.md stack; adding a dependency requires a docs/DECISIONS.md entry. Versions live in Directory.Packages.props.
- `make check` must pass before committing (build with warnings as errors, `dotnet format --verify-no-changes`, tests); never commit secrets.
- Small conventional commits on a feat/ or fix/ branch.
- End every session by updating docs/PROGRESS.md (status, next step, session log line).

## MCP servers
| Server | Why | Claude Code | Cursor / Codex / Antigravity |
|---|---|---|---|
| neon (deploy step only) | manage the demo DB | `claude mcp add --transport http neon https://mcp.neon.tech/mcp` then `/mcp` to log in | add URL `https://mcp.neon.tech/mcp` in the tool's MCP settings |

shadcn's MCP targets React, so it isn't used here (UI is NeoUI.Blazor).

## Gotchas
- **Verification state is server-side only.** Order tools read the verified customer id from `ConversationSession`, never from model arguments. Don't add a `customerId` parameter to any tool.
- **Approval-required tools.** `create_return_request` and `create_support_ticket` are wrapped in `ApprovalRequiredAIFunction`; nothing runs until the customer clicks Confirm. Write tools re-check business rules on the server and use idempotency keys.
- **Legacy API boundary.** The app reaches `Brightline.LegacyOrders` only over HTTP via `Brightline.LegacyOrders.Client`. Never reference the legacy project or `ConnectionStrings:LegacyOrders` from app code – an architecture test fails the build.
- **Gemini compatibility endpoint.** `Ai:Provider=gemini-openai-compat` uses the OpenAI SDK against Gemini's OpenAI-compatible endpoint; some features may be missing (see DECISIONS.md). Switching to `azure-openai` is config only. Free-tier rate limits apply (eval is throttled and cached); free-tier data may be used by Google – synthetic data only.
- **pgvector dimension must match the embedding model** (`Ai:EmbeddingDimensions`, default 768 = `vector(768)` column). Changing models means a migration + full re-index.
- **Blazor Server needs WebSockets and sticky sessions** behind load balancers (Container Apps: session affinity on).
- **Data Protection keys** are stored in the app database so Identity cookies and antiforgery tokens work across instances and restarts.
- **Render cold starts:** the free plan sleeps; the UI shows a "waking up the demo server" message.
- **Migrations** run on startup only in docker compose (`Database__MigrateOnStartup=true`); production runs them as a deploy step.
