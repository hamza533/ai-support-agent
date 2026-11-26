# Brightline AI Support Agent (.NET 10)

**Add an AI agent to your existing .NET systems: it answers from your documents and safely acts through your current APIs.**

> 🚧 Work in progress – see [docs/PROGRESS.md](docs/PROGRESS.md). The full README (demo, results, architecture, costs,
> production deployment) is written at milestone M8.

## Quick start
1. `cp .env.example .env` and add a Gemini API key (`Ai__Gemini__ApiKey`).
2. `make dev` (or `docker compose up --build`).
3. Open http://localhost:5080 (app), http://localhost:5081/scalar (legacy API), http://localhost:18888 (Aspire Dashboard).

No local .NET SDK? `./scripts/dev.sh make check` runs the checks in a container.

## License
MIT
