# BOS Core API review-build report

**Product:** `1.0.0-preview.1` against specification `1.0.0`\
**Decision:** integrated API review build; **not a certified 1.0 release**.\
**Workspace:** `/home/tan0s/Projects/bos-core-dot-net`\
**Build platform:** Linux x64; .NET SDK 10.0.111; .NET / ASP.NET Core 10.0.11.

## Implemented surface

- All **53 initial HTTP method routes**, four authentication routes, health routes, caller-filtered OpenAPI and capabilities.
- Eight documented module areas, versioned type/flow drafts and publication, safe schema evolution, strict typed records and bounded indexed queries/counts.
- ASP.NET Core Identity managers/stores, human sessions/antiforgery, live role grants, scoped hashed API tokens and last-active-administrator protection.
- Registered operation dispatch, capability intersections, operation-only/managed-field guards, exact metadata/business-number binding and checked decimal arithmetic.
- Restricted YAML parser/compiler, immutable typed interpretation, all **39 pure helpers**, pinned dependencies, lexical scope and shared execution limits.
- Atomic flow execution, durable reservations, idempotent receipts, reauthorized/redacted replay, bounded terminal logs and interruption recovery.
- All five Material Request business operations and the reference submission flow.
- Offline initialization, health/doctor, reset, verified backup/restore and credential/cursor invalidation.
- Self-contained Linux/Windows publish targets, samples, dependency inventory, generated OpenAPI and ZIP packaging.

Later methods remain unavailable and unadvertised. `system.backup` / `system.restore` are operator-only CLI facilities. No production fault-injection API is shipped; process probes live in a separate test-only project.

## Build identity and provenance

At build time, the implementation was an **uncommitted working tree over documentation commit `4d1c1bf4ffcfc139011b65f96867a54010a6b8d0`**. That hash appears in assembly informational versions; it does not claim the runtime source was committed there. Subsequent Git publication does not rebuild those measured binaries. See the final measurements below for the source-snapshot digest.

The SDK pin, fixed package versions and restore lockfiles are present. Native SQLite is supplied by `SourceGear.sqlite3` 3.50.4.2 through SQLitePCLRaw 3.0.2; the provider is Microsoft.Data.Sqlite 10.0.11. The [dependency inventory](dependencies.md) records selected versions/licenses. Package vulnerability checks reported no known vulnerable packages from the configured NuGet source at build time.

Final test counts, timestamps, source digest and artifact hashes are recorded below and in [the machine-readable manifest](build-manifest.json). Artifact/test output directories are intentionally Git-ignored; they are local build deliverables. RID-specific single-file restore locks are separate from normal SDK locks. Locked solution restore passed again after both locked RID publishes.

## Verification commands

```sh
dotnet restore Bos.slnx --locked-mode
dotnet test Bos.slnx -c Release --no-restore --logger "trx;LogFileName=conformance.trx" --results-directory artifacts/test-results
dotnet publish src/Bos.Host -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/linux-x64
dotnet publish src/Bos.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/win-x64
dotnet run --project tools/Bos.OpenApiGenerator --no-restore -- artifacts/linux-x64/openapi.json
dotnet run --project tools/Bos.OpenApiGenerator --no-build --no-restore -- artifacts/win-x64/openapi.json
dotnet msbuild build/Package.proj -t:Archive
dotnet list Bos.slnx package --vulnerable --include-transitive
```

## Qualifying integration evidence

Tests use actual file-backed SQLite, Kestrel, the application boundary and Identity stores. The reference fixture produces `300.00`, `600.00`, total `900.00` and Submitted status. Line-only edits invalidate the captured parent revision. Overflows, stale finalization, explicit operation failures and invalid output roll back record/history/index/reference/audit effects.

Separate connections exercise expected-revision, unique-value and last-stock-unit races. Real SQLite page exhaustion (`SQLITE_FULL` through `max_page_count`) and writer contention verify rollback and responsiveness. Commit-uncertainty tests exercise fresh-connection receipt resolution through fault seams; they do not simulate every filesystem failure.

Three child-process tests terminate after reservation, after staged mutations before commit, and after successful commit. Fresh processes/connections prove interruption without re-execution, pre-commit rollback, and post-commit state/receipt durability. The successful-run probe is killed after application commit/return and before its test process exits; it is not a claim to cover every possible machine/power-loss boundary.

A review found and fixed a caught-SDK-limit transaction-poisoning gap, double SDK accounting, replaceable built-in contracts and name-based metadata inference. Regression tests now exercise these through the actual application/SQLite path. Further integrated fixes cover cursor error statuses, canonical instant formatting, exact YAML source preservation and engine-schema refusal before touching an unsupported database.

## Clean Linux container evidence

Image: `mcr.microsoft.com/dotnet/runtime-deps@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832` (Ubuntu noble, Linux x64). This image supplies native OS dependencies and **no installed .NET runtime/SDK**; the test explicitly checks `command -v dotnet` is absent.

The artifact is bind-mounted read-only. The container has no network and an executable temporary filesystem for native extraction. The single-file executable runs version, initialization, doctor/integrity checks, backup, restore, a second doctor, API listener startup and graceful SIGTERM shutdown. No bootstrap secret contents are printed.

Representative invocation:

```sh
docker run --rm --network none --read-only \
  --tmpfs /tmp:rw,exec,nosuid,mode=1777,size=1g \
  -e DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/bos-native \
  --mount type=bind,src="$PWD/artifacts/linux-x64",dst=/app,readonly \
  mcr.microsoft.com/dotnet/runtime-deps@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832 \
  /bin/sh -c 'set -e; if command -v dotnet; then exit 1; fi; /app/bos version; /app/bos init --data-dir /tmp/bos-data --bootstrap-file /tmp/bootstrap.secret; /app/bos doctor --data-dir /tmp/bos-data; /app/bos backup --data-dir /tmp/bos-data --output /tmp/snapshot.bosbak; /app/bos restore --input /tmp/snapshot.bosbak --data-dir /tmp/restored; /app/bos doctor --data-dir /tmp/restored; /app/bos serve --data-dir /tmp/restored --listen http://127.0.0.1:8080 & server=$!; sleep 2; kill -0 "$server"; kill -TERM "$server"; wait "$server"'
```

The first smoke attempt used Docker's default `noexec` tmpfs and could not load the extracted SQLite library. Repeating with an executable protected extraction location succeeded. Native-loader startup failures now exit with a sanitized CLI message rather than an unhandled stack trace.

Windows is cross-built only; no Windows execution or ACL-runtime result is claimed.

## Material release gaps

1. **A22:** Windows clean-host startup and independent Windows transaction/auth/backup behavior remain unverified.
2. **A21:** Daily backup operation and the 24-hour RPO / one-hour RTO have not been measured on deployment hardware. Small local fixture restores are functional evidence only.
3. **A20:** Real SQLite page-limit exhaustion and transaction fault seams are tested; physical device-full/power-loss and every uncertain I/O outcome are not fully certified.
4. **A01–A24:** Executable scenario evidence is substantial, but the matrix is not signed off as a complete release certification. The acceptance tracker distinguishes covered scenarios from full gates.
5. The contract's `ApprovalTask` mention and other [recorded clarifications](decisions.md) still need accountable product review. The implemented reference app follows the two normative schemas and five explicit operation contracts.
6. The initial runtime-schema baseline and v0-preview adoption are implemented, but a historical released-engine migration/rollback exercise does not exist yet.
7. Uncompressed self-contained artifacts exceed the preferred 50 MB size target. Compressed and installed measurements are reported separately; correctness was not weakened to reduce size.

## Implementation choices to review

- One-use bootstrap/reset passwords are consumed on first successful login. The first session can replace the password; losing it requires offline recovery.
- The last-administrator predicate is an active principal with `identity.manage:*`; token scopes are bounded by both issuer authority and target grants.
- Multiple accepted read versions are supported; creates require one exact declared version rather than guessing a create version from an ambiguous set.
- Query/record pages and discovery responses have transport budgeting distinct from the flow result's 64 KiB limit. Flow intermediate values remain bounded and output is validated before commit.
- Native bundle extraction requires an owner-only executable filesystem. Data Protection key confidentiality at rest depends on protected workspace permissions and operator-provided volume encryption.
- The ten-agent implementation used disjoint owned files in one working tree rather than isolated worktrees; shared contracts were coordinated centrally. Build evidence was captured before the implementation commit; subsequent publication preserves that historical provenance.

## Final measurements

Measured at **2026-11-02T23:23:00Z** (3 November in the initial workspace timezone). The final Release suite passed **432 / 432 tests**, zero failed/skipped, in approximately eight seconds. The build emitted no compiler/analyzer warnings. TRX output: `artifacts/test-results/conformance.trx`.

| Test class | Cases passed |
| --- | ---: |
| ApplicationBoundaryTests | 5 |
| CrashTests | 3 |
| FlowLanguageTests | 100 |
| HttpTests | 30 |
| IdentityTests | 30 |
| IntegrationTests | 41 |
| MaterialRequestTests | 29 |
| QueryTests | 66 |
| RunTests | 36 |
| StorageTests | 36 |
| ValuesTests | 56 |
| **Total** | **432** |

TRX SHA-256: `0a6d94733adf656ff525e374f93d8d4dce5ef50cc8f8cbe14ab282471af956c2`.

Working-source digest: `d1c72b7c1353889a297c1f8d6a4eaf093763a96bda87947730d488c979df293c` across **84 files**. The digest is SHA-256 over sorted POSIX relative paths, each followed by a NUL and the 32 raw bytes of that file's SHA-256. Inputs are all files under `src/`, `tests/`, `tools/`, `build/` excluding `bin/` and `obj/`, plus `Directory.Build.props`, `global.json` and `Bos.slnx`. It identifies the runtime/build/test working source; ZIP hashes additionally identify packaged documentation/samples.

| Target | Single executable bytes | Uncompressed distribution bytes | ZIP bytes | Runtime evidence |
| --- | ---: | ---: | ---: | --- |
| linux-x64 | 103,312,473 | 103,870,876 | 47,061,251 | Clean no-installed-runtime container: init, schema-1 integrity, backup/restore, listener and shutdown passed. |
| win-x64 | 106,884,828 | 107,443,691 | 47,872,644 | Cross-publish passed; Windows execution unverified. |

Host assembly: **100,352 bytes**. All seven BOS managed assemblies together: **685,568 bytes**, excluding the framework, parser and SQLite dependencies. Linux native extraction adds **1,434,496 bytes**; measured distribution plus extracted native-cache files is **105,305,372 bytes**, excluding mutable workspace data. Filesystem block allocation and Windows installed footprint are not measured. Both uncompressed distributions exceed the preferred 50 MB target; the ZIP archives are below 50 MB.

| Artifact | SHA-256 |
| --- | --- |
| `artifacts/linux-x64/bos` | `26af18cbc434ebaa7c627c90137a0a504b774c6e2aaa4f97830ef9b543616b53` |
| `artifacts/bos-linux-x64.zip` | `9c86fca3908fbc64af3ef92d7edad039141089c6ccc87f7cf37b97c1a4263970` |
| `artifacts/win-x64/bos.exe` | `b0dcbe21cefe4cb41ef08f3d6bb5516f0c509a280da31d461aa719f6d947cc2b` |
| `artifacts/bos-win-x64.zip` | `561ca52fe6ae3c2f29d8f06c5708ac0cf88cd9c95742c6666bf4b52cc8b15382` |

Single-file output timestamps are `2026-11-02T23:22:00.359583Z` (Linux) and `2026-11-02T23:22:03.577580Z` (Windows). The final clean Linux smoke completed after these artifacts were measured, with database schema **1** and successful graceful listener shutdown.
