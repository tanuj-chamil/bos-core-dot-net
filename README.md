# BOS Core

Headless, single-workspace business API in C# / .NET 10, backed by SQLite. It provides versioned record schemas, validated records, role-based authorization, registered business operations and bounded atomic YAML flows.

**Build status: `1.0.0-preview.1` review build.** The initial HTTP catalogue is implemented. Release certification is tracked separately in [the build report](docs/build-report.md) and [acceptance tracker](docs/acceptance.md).

## Build and test

Requires the SDK pinned in `global.json`:

```sh
dotnet restore Bos.slnx --locked-mode
dotnet build Bos.slnx --no-restore
dotnet test Bos.slnx --no-restore
```

Tests use actual SQLite and Kestrel, including revision/unique-value races, grouped rollback, reference operations, authentication, backup/restore and real child-process termination around commit boundaries. The crash-probe project is test-only and is not published with the host.

## Initialize and serve

```sh
dotnet run --project src/Bos.Host -- init --data-dir ./bos-data --bootstrap-file ./bootstrap.secret
dotnet run --project src/Bos.Host -- serve --data-dir ./bos-data --listen https://localhost:8080
```

Initialization creates an active `Admin` with a generated, owner-only bootstrap secret file. Its password admits **one successful login**. Exchange it for a normal password in that first session; losing the session before doing so requires the stopped-host `reset-admin` command. Credentials are never CLI arguments.

HTTPS requires a certificate. Development can use an installed ASP.NET Core development certificate (`dotnet dev-certs https`). Self-contained deployments can configure a PKCS#12 certificate and protected password file in `bos.json`; see [TLS provisioning](docs/api-usage.md). Bind loopback by default. Cookie login and password administration require HTTPS, and cookie mutations require antiforgery.

## API

```text
POST /v1/methods/{methodName}
GET  /v1/openapi.json
GET  /v1/auth/antiforgery
POST /v1/auth/login
POST /v1/auth/logout
POST /v1/auth/change-password
GET  /health/live
GET  /health/ready
```

All business calls use JSON argument objects and the common result/error envelopes. Authenticated mutations require a 16–128-character `Idempotency-Key`; read-only operations do not. `operations.call` resolves this from its pinned operation's mode. Service tokens are scoped opaque bearer credentials; human sessions use fixed-lifetime secure cookies.

The initial HTTP method families are `types`, `records`, `operations`, `flows`, `identity`, `authorization`, `users`, `roles`, `tokens`, `audit` and `system`. `system.getCapabilities` and authenticated OpenAPI expose the caller-visible implemented contracts. Reserved methods return `FEATURE_NOT_AVAILABLE` and are absent from enabled discovery.

Start with the [public documentation](https://tanuj-chamil.github.io/bos-core-dot-net/), [reference definitions](docs/examples/README.md), and [full normative specification](docs/specification-1.0.0.md).

## Documentation site

The static documentation site compiles curated public guides under `site/content/` with Markdoc. Internal architecture, acceptance, provenance, and release-engineering records under `docs/` remain repository references and are not published as website navigation:

```sh
cd site
npm ci
npm run dev
```

`npm run build` writes the production site to `site/dist`. The GitHub Pages workflow publishes the same static build from `main`; local development remains available at `http://127.0.0.1:4173`.

## Operator commands

```sh
dotnet run --project src/Bos.Host -- doctor --data-dir ./bos-data
dotnet run --project src/Bos.Host -- backup --data-dir ./bos-data --output ./snapshot.bosbak
dotnet run --project src/Bos.Host -- restore --input ./snapshot.bosbak --data-dir ./restored
dotnet run --project src/Bos.Host -- reset-admin --data-dir ./bos-data --secret-file ./reset.secret
dotnet run --project src/Bos.Host -- version
```

Offline commands require the host to be stopped and acquire its exclusive workspace lock. Restore requires an empty destination, verifies its snapshot and invalidates restored sessions, tokens and cursor keys while preserving committed data and receipts.

## Publish

```sh
dotnet publish src/Bos.Host -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/linux-x64
dotnet publish src/Bos.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/win-x64
dotnet restore tools/Bos.OpenApiGenerator --locked-mode
dotnet run --project tools/Bos.OpenApiGenerator --no-restore -- artifacts/linux-x64/openapi.json
dotnet run --project tools/Bos.OpenApiGenerator --no-build --no-restore -- artifacts/win-x64/openapi.json
dotnet msbuild build/Package.proj -t:Archive
```

Each distribution includes sample definitions, API usage, dependency notices and host dependency locks. RID/single-file publish locks are separate from normal SDK-build locks, so publishing does not invalidate the next locked solution restore. Generate the matching OpenAPI snapshot after publishing. Each RID needs independent runtime evidence; publishing a Windows executable from Linux is not Windows execution proof.

## Source layout

| Location | Ownership |
| --- | --- |
| `src/Bos.Contracts` | Shared DTOs, ports, execution context and budgets |
| `src/Bos.Core` | Strict codecs, normalization, schemas and typed ordering |
| `src/Bos.Application` | Root commands, dispatcher, receipts, flow orchestration and reference operations |
| `src/Bos.Sqlite` | Transactions, repositories, queries, backup/restore |
| `src/Bos.Identity` | Identity stores/managers, dynamic grants and opaque tokens |
| `src/Bos.Flows` | Restricted YAML compiler, typed interpreter and pure helpers |
| `src/Bos.Host` | HTTP, authentication, CLI and composition |
| `tests/Bos.Conformance` | Unit, adapter and integrated conformance scenarios |
| `tests/Bos.CrashProbe` | Separate-process crash fixture, excluded from distribution |
| `tools/Bos.OpenApiGenerator` | Build-time export of the same HTTP schemas |
| `site` | Markdoc static documentation compiler and browser shell |

No automatic business retries, background execution, arbitrary code, network effects or multitenancy are enabled in this contract.
