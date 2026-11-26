# DECISIONS

Short entries: **chose X over Y because Z.** Newest at the bottom.

1. **.NET 10 LTS over .NET 11** (released Nov 2026): SPEC requires LTS; clients want long support. ASP.NET/EF packages pinned to the 10.0.x line.
2. **Microsoft.Extensions.AI 10.11.x over 11.0.x:** `Microsoft.Agents.AI` 1.28.0 is built against MEAI 10.11; staying on the same line avoids version skew.
3. **NeoUI.Blazor 4.5.5 for UI components:** licence verified on NuGet and GitHub (`jimmyps/blazor-shadcn-ui`) as **MIT**, which allows commercial use; targets net10.0. No fallback needed.
4. **Embeddings stored via `CommunityToolkit.VectorData.PgVector`, schema owned by EF migrations (raw SQL for `vector(768)` and the generated `tsvector`) over `Pgvector.EntityFrameworkCore`:** the EF plugin (0.3.0) targets Npgsql EF 9; keeping EF unaware of the vector column avoids a cross-version plugin while still giving versioned, migration-managed schema.
5. **`.slnx` solution format over `.sln`:** default in the .NET 10 SDK, readable XML, fewer merge conflicts.
6. **Docker "dev toolbox" (`docker/dev.Dockerfile` + `scripts/dev.*`) in addition to the Makefile:** the dev machine has no local .NET SDK or make; the toolbox runs the same `make` targets in the official SDK image so any machine with Docker can run `make check`.
7. **xUnit v3 over xUnit v2:** current major; better async support and Testcontainers fixtures. Assertions with plain xUnit `Assert` (no extra assertion library).
8. **Scalar over Swagger UI** for the legacy API's OpenAPI viewer: `Microsoft.AspNetCore.OpenApi` generates the document; Swashbuckle is no longer in the templates.
9. **Consumer-side DTOs in `Brightline.LegacyOrders.Client` over a shared contracts assembly:** a real client team only has the existing system's HTTP/OpenAPI contract. Sharing types would couple the app to the legacy project; compatibility is proven by integration tests that run the real typed client against the real API.
10. **`AddStandardResilienceHandler` (Microsoft.Extensions.Http.Resilience) with tuned defaults** (3 retries, exponential + jitter, 5 s attempt / 20 s total timeout, circuit breaker 50 % over 30 s, 15 s break) over hand-written Polly pipelines: the standard, documented .NET pipeline; every value can be overridden under `LegacyOrders:Resilience`. POST retries are safe because return creation always carries an `Idempotency-Key`.
11. **Return numbers from a Postgres sequence (`RR-` || nextval, starting at 2291) over app-generated numbers:** unique under concurrency with no extra round trip, and matches the demo story (first return is RR-2291 after each reset).
12. **Idempotency enforced by a unique index `(customer_id, idempotency_key)` in the legacy DB, plus a race-safe retry read:** concurrent duplicates can't create two returns.
13. **Typed-client failures map to two exceptions:** `LegacyOrdersUnavailableException` (unreachable, timeouts, circuit open, 5xx after retries → the agent offers a ticket) and `LegacyOrdersRejectedException` (legacy business rule refused the write).
14. **No Sentry SDK:** CONVENTIONS lists Sentry as optional; errors are already captured as OpenTelemetry exceptions in the Aspire Dashboard (local) and Application Insights (production), so a second error pipeline adds no client value.
15. **Blazor static SSR for account pages, Interactive Server for the rest** (the .NET template pattern): sign-in must set cookies over a normal HTTP POST with antiforgery; everything else benefits from the live circuit.
16. **Chat rate limiting in the chat service, not middleware:** Blazor Server messages arrive over one SignalR connection, so per-message limits use a `PartitionedRateLimiter` keyed by client IP; HTTP endpoints (login POST) use the ASP.NET Core rate limiting middleware.
17. **PII masking with a small regex masker (`PiiMasker`) over `Microsoft.Extensions.Compliance.Redaction`:** the data we must mask is known (emails, phone numbers in free text); 30 lines are easier to audit than wiring the redaction framework through every log call. Stored tool arguments and logs pass through it.
18. **Help-centre document bytes stored in Postgres (`sources.content`) for the demo/MVP, max upload 10 MB:** keeps the free demo to one database. The production profile moves uploads to Azure Blob Storage (documented in deploy/azure) – the stateless-container rule is satisfied because no file is kept on local disk.
19. **NeoUI.Icons.Lucide 3.1.0 (MIT):** icon set matching shadcn/ui's default (Lucide); same author as NeoUI.Blazor.
20. **Testing projects use an `extern alias` for the legacy API:** .NET 10 generates a public `Program` for every web app; the alias keeps both test hosts in one project.
