# Live demo: Render (two Docker services) + Neon

The public demo runs on free plans: **Render** hosts the web app and the legacy Orders API as two Docker web services,
**Neon** hosts Postgres (two databases). Cost: $0. Render's free services sleep after ~15 minutes idle; the first request
takes up to a minute ("Waking up the demo server…" is shown while the live connection starts).

> Free-tier data rule: Gemini's free tier may use prompts to improve Google's products, so the demo only ever handles the
> synthetic sample data in this repo. Client deployments use the production profile (Azure OpenAI) in `deploy/azure/`.

## 1. Neon (≈5 minutes)

1. Create a project at <https://console.neon.tech> (one project for this repo, region close to your Render region).
2. Create two databases: `brightline_app` and `brightline_legacy`.
3. In the SQL editor, connected to **brightline_app**, run: `CREATE EXTENSION IF NOT EXISTS vector;`
   (the EF migration also does this; running it once as the owner avoids permission surprises).
4. Copy the two connection strings in **.NET** format, e.g.
   `Host=ep-xxx.eu-central-1.aws.neon.tech;Database=brightline_app;Username=...;Password=...;SSL Mode=Require`.

Optional, for AI tools: the Neon MCP server (read-only) – see `AGENTS.md` → MCP servers.

## 2. Render (≈10 minutes)

1. Push this repo to GitHub. In Render: **New → Blueprint**, pick the repo; Render reads `render.yaml` and proposes
   `brightline-legacy-api` and `brightline-web` plus the `brightline-shared` env group (generated API keys).
2. Fill the `sync: false` values:
   | Service | Variable | Value |
   |---|---|---|
   | brightline-legacy-api | `ConnectionStrings__LegacyOrders` | Neon `brightline_legacy` connection string |
   | brightline-web | `ConnectionStrings__App` | Neon `brightline_app` connection string |
   | brightline-web | `LegacyOrders__BaseUrl` | the legacy service URL, e.g. `https://brightline-legacy-api.onrender.com` |
   | brightline-web | `Ai__Gemini__ApiKey` | key from <https://aistudio.google.com/apikey> |
   | brightline-web | `Demo__StaffPassword` | a password for the read-only demo staff account (published in the README) |
3. Deploy the legacy API first, then the web app. Both migrate on start-up (demo only), the legacy API seeds its sample
   data, and the web app indexes the help-centre documents.
4. Check `https://<web>/health` and `https://<web>/ready`.

## 3. What demo mode does (`Demo__Enabled=true`)

- Daily conversation cap (`Demo__DailyConversations`, default 40) and per-IP chat rate limits keep usage inside free tiers.
- The legacy order data is reset every night at 03:00 UTC (`Demo__ResetHourUtc`) through the legacy API's admin endpoint.
- The demo staff account has the `staff` role: it can browse conversations, tickets and documents but can't change
  settings, upload, re-index, delete or add users. There is no public sign-up.
- Blazor Server uses WebSockets – Render supports them on free web services; keep a single instance (no load balancer
  stickiness needed).

## 4. Redeploys and resets

- Manual deploys (`autoDeploy: false`) keep the demo stable.
- To reset the order data on demand: `curl -X POST https://<legacy>/api/admin/reset -H "X-Admin-Key: <LegacyOrders__AdminKey>"`.
- To rebuild the knowledge base: sign in as an admin and re-index from **Documents**, or run the image with `--seed`.
