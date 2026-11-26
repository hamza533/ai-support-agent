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
