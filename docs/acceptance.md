# Acceptance and evidence tracker

**Authority:** [Specification 1.0.0](specification-1.0.0.md), especially §§18–22, 27, 30 and 35.\
**Current result:** 0 of 24 acceptance gates verified; all **Not run**.\
**Implementation status:** not started. This file defines planned verification, not executable tests or software test results.

## 1. Status meanings

| Status | Meaning |
| --- | --- |
| Not run | No qualifying implementation evidence has been recorded. |
| Blocked | A named dependency, platform or unresolved decision prevents execution. Record the blocker. |
| Partial | Some scenarios were executed; the gate has unverified scenarios or missing required evidence. |
| Failed | At least one required observable behavior failed. Link a reproducible defect. |
| Passed | All required scenarios for that gate passed on the identified source/build with qualifying evidence. |

Do not turn **Not run**, **Blocked** or **Partial** into Passed because a nearby unit test, mock or demo succeeded. A source/configuration change affecting a gate requires its evidence to be re-evaluated.

## 2. A01–A24 matrix

Planned suites are descriptive ownership labels; no such test files exist yet.

| ID | Required observable result | Planned suite / lead | Required evidence | Status |
| --- | --- | --- | --- | --- |
| A01 | Duplicate JSON/YAML keys, custom tags, aliases and oversize/deep documents fail before dispatch. | Strict input and parser / Agents 2, 6, 8 | Actual HTTP/parser rejections plus dispatch-not-entered assertions; byte/depth boundaries. | Not run |
| A02 | Typed values round-trip; decimal overflow, wrong scale, null/missing confusion and invalid dates fail. | Value conformance / Agent 2 | Fixed boundary vectors and property-based codec/canonicalization checks; exact arithmetic cases. | Not run |
| A03 | Type publication is atomic; immutable versions cannot be edited; forbidden schema changes fail. | Definition lifecycle / Agents 2, 3 | Draft/version snapshots before/after success, stale revisions and failed publication; safe-evolution matrix. | Not run |
| A04 | HTTP and flow calls enforce the same grants, token scopes and operation/field guards. | Cross-entry authorization / Agents 5, 8, 9 | Same principals/scopes via HTTP/application/flows; denied operation-only and managed-field bypass attempts. | Not run |
| A05 | A flow updates two records then fails; neither update/history/index change survives. | Atomic rollback / Agents 3, 7, 9 | Real SQLite pre/post state including references, unique/index entries, history, audit and failed run. | Not run |
| A06 | Two updates with the same revision commit at most one. | Revision race / Agents 3, 9 | Separate connections, coordinated competing updates, one committed result and stale-conflict evidence. | Not run |
| A07 | Two stock-issue operations cannot both consume the last unit. | Business-invariant race / Agents 7, 9 | Test-only trusted operation checking stock inside actual root transactions; separate competitors, final nonnegative stock. | Not run |
| A08 | Concurrent unique values conflict correctly; live references block deletion, including nested references. | Unique/reference persistence / Agents 3, 9 | Concurrent insert/update conflict; nested incoming references; tombstone release and rollback checks. | Not run |
| A09 | Query numeric order is correct; cursor tampering/filter changes fail; no SQL injection. | Query and cursor conformance / Agent 4 | Typed order vectors/properties, ties/sentinels/descending, binding/tamper rejections and hostile value tests. | Not run |
| A10 | Paging/count never silently truncate; reference flow rejects more than 100 lines. | Query completeness / Agents 4, 10 | Multi-page data, exact count or QUERY_LIMIT, nextCursor behavior and oversized reference line set rejection. | Not run |
| A11 | Expression short-circuiting, scope, Missing, pure helpers and loop budgets match specified semantics. | Language conformance / Agent 6 | Full helper vocabulary, lexical paths, early return, missing semantics and shared node/item-budget assertions. | Not run |
| A12 | Output validation or MISSING_RETURN rolls back all writes. | Output/termination rollback / Agents 6, 7 | Publication rejection where statically known; runtime malformed/absent result rollback at a controlled test seam. | Not run |
| A13 | Same key/body replays once; changed body conflicts; replay reauthorizes and redacts inaccessible results. | Idempotency and replay disclosure / Agents 5, 7, 8 | Durable receipts, concurrent duplicate states, different hashes, revoked grants and secret-command replay exceptions. | Not run |
| A14 | Kill before commit leaves no business mutation; kill after commit preserves success and its receipt. | Process crash boundaries / Agents 7, 9 | Actual child-process termination before/after commit, restart, fresh-connection state/history/receipt inspection. | Not run |
| A15 | Startup marks remaining running runs interrupted and never automatically repeats them. | Startup recovery / Agents 7, 10 | Abandoned durable reservation, exclusive-lock recovery, RUN_INTERRUPTED replay and zero repeated business execution. | Not run |
| A16 | Revocation/disablement before execution denies the run; running transaction uses a consistent snapshot. | Authorization/control timing / Agents 5, 7, 9 | Coordinated reservation/execution-boundary changes and in-flight snapshot behavior; no partial state on denial. | Not run |
| A17 | Version publication leaves old flows pinned; missing operation contract versions block execution. | Dependency/host compatibility / Agents 2, 6, 7 | Old/new type/flow publication, pinned reads/creates, schema mismatch, missing operation/AST version and hash checks. | Not run |
| A18 | Deadline, SQL work and allocation budgets stop work; host remains responsive. | Resource conformance / Agents 4, 6, 7, 8 | Actual VM progress interruption, cooperative cancellation, expression/item/size/call limits and subsequent healthy requests. | Not run |
| A19 | Audit correlates actors/versions/steps without tokens, passwords or default whole-record logs. | Audit and diagnostic disclosure / Agents 5, 7, 8 | Persisted audit/history/run/step correlations, redacted errors and secret-marker scans of logs/receipts. | Not run |
| A20 | Disk-full, storage-busy and uncertain commit faults create no false success or partial state. | Storage fault boundaries / Agents 3, 7, 9 | Real transaction fault seams, fresh-connection outcome resolution, failed-terminal-persistence recovery and no fabricated success. | Not run |
| A21 | Backup restores readable data and receipts; sessions/tokens invalidated; recovery objectives measured. | Offline recovery / Agents 9, 10 | Verified snapshot/manifest, empty-target restore, token/session/cursor rejection, replay preservation and measured RPO/RTO. | Not run |
| A22 | Clean Windows/Linux hosts run their artifacts without an installed .NET runtime. | Distribution / Agents 9, 10 | Per-RID checksums, clean-host environment identity, launch/fixture output, native-library behavior and sizes. | Not run |
| A23 | Material Request calculates 900.00 and preserves lifecycle guards through all entry points; a line edit makes prior parent revision stale. | Reference application / Agents 9, 10 | Two-line HTTP/flow fixture, exact totals, revise/stale/approval/bypass tests and after-first-write overflow/finalization failure rollback. | Not run |
| A24 | Identity and BOS grants/audit updates share atomic persistence; last admin cannot be removed. | Identity transaction conformance / Agents 3, 5, 9 | Real shared-transaction failures/races across roles/users/grants/receipts/audit; all last-admin loss paths rejected. | Not run |

## 3. Evidence protocol

For every test execution intended as release evidence, capture:

```text
Acceptance ID(s) and scenario names
Source revision and dirty/untracked source status
Build/artifact identity, target RID, SDK/package locks and SQLite engine version
OS, architecture, filesystem/storage context where relevant
Exact commands, UTC timestamps, duration and exit codes
Test output and assertions / safe state comparison
Fault/race/crash coordination method where relevant
Result, remaining gaps and linked defect reproduction
```

Store durable evidence under an identified build/review record and link it from this tracker and the release report. Sanitize credentials and sensitive business payloads; an evidence bundle must not become a secret log. Do not fill missing provenance with guessed values.

### Required test boundaries

- Use real SQLite connections and actual HTTP/application paths for integration gates.
- Race tests use separate database connections, not two calls serialized by one fake repository.
- Crash tests terminate an actual process at a controlled durable boundary, then inspect from another process/connection.
- Fault injection exercises actual transaction ownership and commit/error handling. Production APIs/artifacts expose no fault toggles.
- Use property-based checks for codecs, typed ordering and expression-budget behavior.
- Unit tests can isolate rules; their results do not substitute for persistence/security/recovery conformance.

## 4. Scenario details that must not be lost

### Inputs and values — A01/A02

- Duplicate/unknown keys at root and nested positions; invalid Unicode/NFC handling; exactly one YAML document.
- Aliases/anchors/merge keys/custom tags/non-string YAML keys rejected before expansion or dispatch.
- String UTF-8 bytes, field count, depth, scalar count, arrays and raw/compiled body limits at and beyond boundaries.
- Canonical integer endpoints and formatting, metadata Int32 limits, exact decimal precision/scale and named rounding modes.
- Required and nullable independently tested; explicit null and missing distinguishable in defaults, paths, indexes and output.
- Strict dates, leap days, RFC 3339 offsets/milliseconds and excess precision; unknown timezone rejection.

### Mutation and publication — A03/A05–A08

- Publishing increments retained draftRevision; semantic duplicate publication returns NO_CHANGE without new version.
- Supported additive/relaxing evolution succeeds; every prohibited change fails atomically.
- Normalized no-op updates leave revision/history/audit unchanged.
- Multiple mutations to a record in one flow keep each staged revision/history if committed and remove all on rollback.
- Create-then-delete in a run fails; tombstones retain data/history; no live-reference cascade or ID reuse.
- Unique values span record schema versions; missing/null do not reserve values; deleted sources release outgoing live references.

### Queries — A09/A10

- `ne`/`notIn` exclude null/missing; `isNull=false` excludes missing; `isMissing=false` includes null.
- Filter/sort only id or declared indexed scalar fields; unknown fields fail; count rejects paging/projection/sort arguments.
- Cursor binds principal, type, normalized filter, sort, projection and schema dependency set; never restores lost permission.
- Missing < null < present in ascending order, reversed descending; numeric ordering and deterministic ID ties.
- Standalone pages observe committed per-request snapshots; pages within one atomic flow see the same transaction and staged writes.
- Count produces exact canonical string or QUERY_LIMIT; limit exhaustion never returns a misleading complete result.

### Authorization, replay and credentials — A04/A13/A16/A19/A24

- Direct record calls and custom operation calls have identical resource grants, token ceilings and lifecycle guards.
- Another custom operation changes nearest guardOwner; generic record dispatch preserves it; arguments cannot impersonate it.
- Revoked/expired token, disabled principal, invalidated session and removed roles are enforced at current request/transaction boundaries.
- Run owner/readAll access and current flow/type permissions independently gate metadata and payload/step disclosure.
- Malformed/unauthorized calls reserve no idempotency key; failed direct commands retain no successful receipt; accepted failed flows do retain one.
- Password secrets are absent from request hashes/receipts and cannot be changed by replaying the same secret-command key.
- Token replay returns null secret plus SECRET_ALREADY_DISCLOSED; revoked tokens cannot authenticate subsequent calls.
- Last-admin checks cover disablement, role removal, grant replacement and any equivalent user/role mutation.

### Language, execution and recovery — A11/A12/A14/A15/A17/A18/A20

- Strict boolean typing and same-kind arithmetic/equality; no executable string interpolation or CLR-member access.
- Missing permitted only by its four specified helpers; immutable scopes, nonescaping branch/item bindings and no rebinding.
- `and`/`or`/`coalesce` skip unsafe unevaluated branches; helper list items and nested calls debit the shared budget.
- Loop source evaluated once; oversize fails before the first iteration; return inside a loop terminates the run.
- Unavailable exact dependencies/AST formats never silently rebind; published AST hash verified independently of draft/source.
- Commit uncertainty resolved from durable receipt/run state on a fresh connection, including a commit followed by response loss.
- Failure to persist a terminal failed run leaves recoverable running state; restart interrupts it without replay.
- SQL callback settings and execution deadlines are exercised directly; timeout is not inferred from input row count.

### Operations and reference application — A21–A23

- Backup rejects a running workspace; restore rejects nonempty destination, tampered hashes and unsupported engine versions.
- Snapshot includes required protected keys/configuration; restored sessions/tokens/cursors are invalidated while business receipts remain readable under current permissions.
- Measure recovery time and backup age; do not infer RPO/RTO compliance from one successful restore.
- Linux cross-publishing Windows is build evidence only; Windows clean-host runtime evidence is separate.
- Reference overflow fails after the first staged line update; all line amounts, parent values and business history remain unchanged.
- Finalize recomputes pricing so direct invocation cannot skip it. Revise advances parent contentVersion/revision even for a line-only edit. Approve allows only Submitted with matching revision.

## 5. Required surface inventory

A01–A24 do not replace catalogue completeness. Before release, record a method-to-test and descriptor-to-OpenAPI inventory for all initial entries:

| Family | Initial surface to inventory | Current evidence |
| --- | --- | --- |
| Types | create, update, validate, publish, get, list, listVersions, getDraft | None |
| Records | create, get, update, delete, validate, list, count | None |
| Operations | list, get, validate, call; five registered Material Request operations | None |
| Flows | create, update, getDraft, validate, publish, get, list, listVersions, run, getRun, listRuns, getRunSteps, disable, enable | None |
| Identity/authorization | identity.getCurrent, authorization.can | None |
| Users | create, get, list, update, disable, setPassword | None |
| Roles | create, update, get, list, assign, remove | None |
| Tokens | create, list, revoke | None |
| Audit/system | audit.list, system.getCapabilities, system.getHealth; CLI-only system.backup/restore | None |
| Authentication | antiforgery, login, logout, change-password, fixed session/lockout/stamp behavior | None |
| Pure helpers | Every helper in specification §22; versioned descriptors and type/budget validation | None |
| Reserved surface | Every Later/reserved method returns FEATURE_NOT_AVAILABLE and is absent from enabled capabilities | None |
| Operator CLI | init, serve, doctor, backup, restore, reset-admin, version | None |

## 6. Release decision

Current decision: **documentation baseline only**.

Use the [release report template](release-report-template.md) once an implementation/build exists. Declare a complete **1.0 release** only after the full initial surface, mandatory semantics, reference scenario and all A01–A24 gates pass. A demo or release candidate must name remaining failed, partial, blocked and unrun gates.
