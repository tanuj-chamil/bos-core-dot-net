# BOS Core 1.0 implementation specification

**Product version:** 1.0\
**Document revision:** 1.0.0\
**Date:** 3 November 2026\
**Classification:** Internal release using record types, records and YAML flows

## Purpose and implementation decision

Build a small single-tenant headless business platform in C# and .NET. Store versioned record types and validated records in SQLite. Expose typed methods through one application service layer. Execute short YAML flows by interpreting a restricted instruction tree whose calls resolve only to registered BOS operations.

This document defines observable behavior, method contracts, data structures, flow semantics, security, recovery and release tests. It is intended for the developer implementing the core and the business owner reviewing its scope. **MUST** denotes a release requirement. **SHOULD** denotes a recommendation requiring an explicit documented exception.

| Decision | Internal release baseline |
| --- | --- |
| Host | ASP.NET Core on a supported .NET LTS release |
| Database | SQLite on a local disk with transaction-scoped repositories |
| Identity | ASP.NET Core Identity local accounts and role administration |
| Automation | BOS YAML instruction format and restricted value expressions |
| Execution | Manual synchronous invocation with persisted run receipts |
| Transactions | One atomic database transaction for an entire short command flow |
| Distribution | Self-contained executable per supported OS and CPU |
| Trust | Administrators publish flows; users run permitted versions |
| Interface | Headless HTTP JSON API and matching internal SDK |

### Version 1.0 decision

This specification freezes the internal BOS 1.0 contract. It carries forward the record and YAML architecture from v0.1, resolves shared DTO and execution rules, and adds a ten-agent build plan. Target one working day for the first integrated build. Release as 1.0 only after its required acceptance gates pass; a working demo alone is not that evidence. Reserved later methods remain outside this release.

This is a specification only. No runtime implementation, deployment, performance claim or passed software test is implied.

## 01 Scope and reading map

### Required release surface

The pilot MUST provide type drafts and publication, typed records with create/read/update/delete, bounded queries and counts, role grants, controlled named operations, published YAML flows, atomic rollback, execution receipts, audit history, backup/restore and a reference Material Request application.

### Deferred surface

No multitenancy, distributed execution, arbitrary user code, general SQL, unrestricted network access, public registration, dynamic native plugins, BPMN execution, graphical editor, attachments, reporting designer, accounting engine or ERP is required. Schedules, event triggers, durable waiting and automatic execution retries are later capabilities. API discovery supports future AI tools without giving agents separate privileges.

| Sections | Read for |
| --- | --- |
| 02–05 | Architecture, values, schemas and evolution |
| 06–10 | Record behavior, queries, security and registered operations |
| 11–15 | YAML syntax, expressions, control flow, transactions and runs |
| 16–17 | Persistence ports and HTTP contract |
| 18–23 | Complete initial and reserved method catalogue |
| 24–26 | Record schemas and a complete executable flow example |
| 27–30 | Limits, audit, deployment and acceptance tests |
| 31–39 | Ten-agent plan, build gates, shared DTOs, compatibility and references |

### Completeness boundary

Every initial method is normative under the common contracts in this document. A method marked **Later** is a reserved product direction, returns `FEATURE_NOT_AVAILABLE` if addressed, and MUST NOT be advertised as implemented. Its signature may evolve before a future release. This keeps the current spec implementable without silently requiring every future feature.

### Size target

Aim for a small domain core and a self-contained release under 50 MB where practical. The 1–50 MB preference is a packaging target requiring measurement, not a guarantee: a self-contained .NET artifact includes runtime dependencies. Record application assembly size, uncompressed distribution size and installed footprint separately. Record any size-target miss in the build report; optimize packaging after the working path is measured. Size is a target rather than a reason to weaken correctness checks. [R5]

## 02 Architecture and trusted boundaries

| Module | Responsibility |
| --- | --- |
| `Bos.Contracts` | DTOs, typed values, method descriptors and stable error codes |
| `Bos.Core` | Schema validation, record lifecycle, write guards and expression rules |
| `Bos.Application` | Commands, authorization, unit-of-work ownership and flow orchestration |
| `Bos.Sqlite` | Repositories, query compilation, constraints, snapshots and migrations |
| `Bos.Identity` | Identity adapters, role grants, cookie and API-token authentication |
| `Bos.Flows` | YAML parser adapter, publication compiler and bounded interpreter |
| `Bos.Host` | HTTP endpoints, local CLI, dependency injection and health |
| `Bos.Conformance` | Contract tests shared by current and future adapters |

### One route to business state

HTTP requests, flow calls and future AI tools invoke the same application commands. Those commands authorize, normalize, validate and then access repositories. Direct repository access from a flow is impossible. External clients never receive database credentials or CLR objects.

### Adapter and repository rules

Use explicit ports for persistence, identity, clock, ID generation, flow parsing and backup. Register adapters in trusted host code. Configuration selects from registered identifiers; it never loads arbitrary assemblies. Repositories share their unit of work and never commit independently. A database adapter must pass the same transaction and permission tests before substitution.

### Execution boundary

The YAML interpreter operates only on validated nodes and immutable values, and calls a fixed registry of trusted .NET methods. It has no eval, reflection, shell, module-loading or HTTP instruction. The evaluator is still security-relevant software: its parser, allocations, recursion and registered methods require bounded behavior and tests. It is not a general-purpose code sandbox.

### Deployment shape

One process owns one data directory using an exclusive OS lock. Business writes serialize through SQLite. Additional read connections may serve bounded snapshots. Multiple web replicas opening the same workspace are unsupported in 1.0. Forms and print formats can be independent clients of the API.

## 03 Values, identifiers and serialization

| Kind | Wire form and validation |
| --- | --- |
| `string` | NFC Unicode string; case-sensitive ordinal comparison; max 16 KiB UTF-8 |
| `integer` | Signed 64-bit integer encoded as a base-10 JSON string; no exponent |
| `decimal` | JSON string with declared precision 1–28 and scale 0–12; scale ≤ precision |
| `boolean` | JSON `true` or `false` only |
| `date` | `YYYY-MM-DD` Gregorian date without a timezone |
| `instant` | RFC 3339 with explicit offset; canonical UTC `Z`, milliseconds; reject finer precision |
| `enum` | Exact string selected from a published value list |
| `reference` | Canonical lowercase UUID string to a live record of one specified type |
| `object` | Declared nested fields; no unknown fields |
| `array` | Declared item schema; bounded count and nesting |
| null and missing | Null is an explicit value; missing is absence. Nullable and required are independent |

### Metadata and arithmetic

IDs are server-generated UUID v4. Record revision, draft revision and definition version are positive metadata numbers ≤ 2147483647; business integer fields remain strings. Money is a decimal plus an explicit currency field. Calculations never use binary floating point for integer or decimal values. Arithmetic is checked; overflow and division by zero fail. Detect unrequested precision loss; use scaled `BigInteger` intermediates where needed rather than accepting implicit decimal rounding. Rounding must be named when precision is discarded; default named mode is `halfEven`.

### Normalization

Reject duplicate JSON keys, invalid Unicode, non-finite numbers and implicit string-to-number conversion. Decimal storage uses the field scale, with no thousands separators or exponent; excess fractional digits fail unless the caller explicitly rounded beforehand. Integer strings omit leading zeros and a plus sign. Defaults use the same codecs.

### Canonical hashing

Canonical JSON uses ordinally sorted object keys, preserved array order and normalized typed values. For request receipts, canonicalize structure and NFC strings before schema defaults; include explicit method version, inputs and preconditions. Keep absent and null distinct. Publication stores canonical compiled definition JSON plus original YAML text and both hashes.

### Time

An invocation captures `startedAt` in UTC. `now` returns that value for the entire run. `today` requires an explicit timezone or uses the workspace timezone configured at initialization, initially `Asia/Colombo`. Persist dates independently of local midnight. Reject unknown timezone IDs.

## 04 Record type schema contract

### Definition grammar

`TypeDefinition` contains `name`, `description`, `fields`, `accessMode` and `allowedOperations`. `fields` is an object mapping field names to `FieldDefinition`. Required common properties: `kind`. Optional properties: `required=false`, `nullable=false` and literal `default`. `string` adds `maxLength`; numeric kinds add `minimum`/`maximum`; `decimal` requires `precision`/`scale`; `enum` requires `values`; `reference` requires `targetType`; `object` requires `fields`; `array` requires `items` and `maxItems`.

Names match `[A-Za-z][A-Za-z0-9_]{0,63}`. Type names are unique and case-sensitive. System names beginning with underscore are reserved. Unknown definition properties fail. Fields may declare `indexed` and `unique` only at top level on scalar kinds; `unique` implies `indexed` and is unavailable for references, objects and arrays in 1.0.

### Write rules

`accessMode` is `direct` or `operationsOnly`. `direct` permits authorized record methods. `operationsOnly` restricts create/update/delete to an explicitly allowed registered operation name. A field may declare `managedBy` operation names; direct create cannot supply it, and direct update cannot change it. A managed required field needs a literal default unless the creating operation sets it. Record-read permission is required for any mutation that returns a complete record.

### References and uniqueness

References store identity only. Assigned targets must exist, be live, match `targetType` and be readable by the caller. Nested references are tracked too. Deletion is RESTRICT while any live record refers to the target; no cascades. Unique values are enforced across all live records of the type regardless of schema version. Missing/null values reserve nothing; comparisons use canonical typed values.

### Validation result

`ValidationReport` is `{valid:boolean, errors:[{path,code,message}]}`, with at most 100 errors. Validation never returns hidden record values. Advisory validation cannot promise later uniqueness or authorization because state can change. The actual write always repeats checks inside its transaction.

### Schema versus business rule

Schema validation enforces local shape and constraints. Cross-record rules such as a balanced posting or non-negative stock MUST be implemented inside an allowed named operation. Generic schema validity does not establish business correctness.

## 05 Publication and safe evolution

### Draft and published state

Each type or flow name has at most one mutable draft with `draftRevision` and zero or more immutable published versions. `create` makes revision 1; `update` requires `expectedRevision` and increments it. `publish` assigns the next version, freezes canonical content and dependencies, and advances the active pointer in one write transaction. Republishing unchanged content returns `NO_CHANGE`. No published version is deleted.

### First-release type changes

Initial publication permits the complete supported schema. Subsequent publication permits adding optional non-indexed fields, adding enum choices and relaxing non-indexed local constraints. It rejects removing/renaming fields, changing kinds or decimal precision/scale, adding required fields, changing reference targets, changing indexes/unique flags or changing `accessMode`/`managedBy`/`allowedOperations`. Incompatible evolution waits for an explicit migration feature.

### Existing records

A record stores `typeVersion` and validates against that version. Publication does not rewrite or reinterpret old data. Creates use active version unless an allowed operation or pinned flow specifies an exact supported version. Flow dependencies list exact type versions accepted for reads and the version for creates. Unexpected versions fail `SCHEMA_VERSION_MISMATCH` before exposing data to the flow.

### Flow publication

Compile the allowed YAML syntax to a versioned instruction tree. Validate identifiers, lexical scope, expression types, operation names, argument schemas, output schema, capabilities and limits. Pin operation contract versions and type dependencies. Keep policy/grant decisions current at execution. Publication does not run business operations or prove business logic correct.

### Later activation and migration

`types.activate` and `records.migrate` remain reserved. Initial active pointers advance through publish only. A later migration will be an audited atomic bounded batch with expected revisions, validated target data and rollback on any failing item. No implicit live relational DDL is performed when adding a BOS record type.

### Host upgrades

Application database schema migrations are a separate operator operation. If the binary cannot supply an operation contract version referenced by a published flow, refuse new runs of that flow with `DEPENDENCY_UNAVAILABLE`; never silently bind to a new implementation.

## 06 Record lifecycle and write contracts

```text
Record = {
  id, type, typeVersion, revision, deleted,
  data, createdAt, createdBy, updatedAt, updatedBy
}
Patch = { fieldName: replacementValue, ... }
Page<T> = { items: T[], nextCursor: string | null }
```

### Create

`records.create` applies literal defaults for omitted fields, normalizes data, authorizes and checks guards, validates values/references/uniqueness, allocates identity and writes revision 1. A client cannot assign system metadata. Return the complete committed Record. A create inside a flow returns a transaction-local envelope.

### Update

`records.update` accepts a nonempty map of top-level field assignments. Omitted fields remain unchanged; explicit null requires nullable. An object or array assignment replaces that whole value; no recursive merge or path expressions. Unsetting fields is not supported initially. Require exact `expectedRevision`; no blind overwrite. Validate the full merged result. Identical normalized data returns `NO_CHANGE` without audit or revision increment.

### Revision rule

Each actual mutation increments revision by one, including multiple mutations to the same record within one flow. Later calls must use the returned staged revision. History contains every mutation if the flow commits, all sharing `commandId`/`runId`; rollback removes all those revisions. This simple rule avoids collapsing intermediate writes. Create then delete in the same run is rejected.

### Delete

Soft-delete only. Check revision, permissions, operation guards and incoming live references. Write a tombstone with preserved data/history; release unique reservations and outgoing live-reference entries. Normal get/list excludes tombstones. Return `{id,type,revision,deleted:true}`. IDs are never reused; no restore or physical purge in the pilot.

### Write sequence

Decode → authenticate → open write unit of work → reload grants → resolve schema → authorize → enforce guards → merge/default → validate → check references/unique constraints → persist record, indexes, history, audit and receipt → commit. Any failure rolls back all changes. Errors must not reveal inaccessible target or source record details.

## 07 Query model and consistency

```json
{
  "where": [{"field": "requestId", "op": "eq", "value": "uuid"}],
  "orderBy": [{"field": "id", "direction": "asc"}],
  "select": ["quantity", "unitPrice"],
  "limit": 50,
  "cursor": null
}
```

### Filter semantics

`where` is an AND of up to 10 comparisons. Initial operators are `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `in`, `notIn`, `isNull` and `isMissing`. Only `id` and declared indexed top-level fields may be filtered or sorted; all fields may be selected if the principal can read the type. `in`/`notIn` accepts at most 50 values. `eq`/`ne` against null is rejected; use `isNull`. Missing/null values do not match ordinary comparisons, including `ne`/`notIn`.

`isNull` takes boolean and tests explicit null only. `isMissing` takes boolean and tests absence only. The negation of `isNull` excludes missing values; `isMissing=false` includes explicit null. Typed equality does not coerce. Numeric comparisons are numeric, never decimal-string lexical order. Complex boolean expressions, substring searches, joins and aggregates are deferred.

### Sort and projection

One indexed sort field plus `id` as a tie-breaker is allowed. Default is `id` ascending. Order missing before null before present values in ascending order, reversing for descending. Projected rows still contain `id`/`type`/`typeVersion`/`revision` and `data` with only requested fields. An absent `select` returns all data fields. Unknown fields fail rather than being ignored.

### Pagination

Default limit 50, maximum 200. Cursor is an authenticated opaque token bound to type, principal, normalized filter, sort, projection and schema dependency set. It contains the last sort keys, not a database offset. Each API page is its own committed snapshot; concurrent changes may affect later pages. Within one atomic flow, all pages share its unit of work and see its staged writes.

### Count and work limits

`count` returns `{count:"base10"}` or fails `QUERY_LIMIT`; it never returns an approximate or partial result. All queries must be parameterized and use permitted indexed access. The adapter enforces a one-second per-query deadline within the run deadline and a 1,000,000-instruction SQLite work budget. Configure the progress callback interval to 1,000 virtual-machine instructions and interrupt at 1,000 callbacks or the deadline. This accounting is approximate at callback granularity, not a hard CPU-time guarantee. [R6] Interrupt expensive work before returning results. This is a SQL work budget, not an estimated row-count limit. There is no unrestricted `getAll`. A bounded flow must check `nextCursor` before treating a page as the complete set.

## 08 Authentication, roles and claims

### First-party identity integration

Use ASP.NET Core Identity `UserManager`, `RoleManager`, password hashing and account lockout with a SQLite store. Identity supplies local account primitives; BOS must implement its own dynamic resource authorization. A route attribute alone does not enforce record-level decisions. [R1, R2]

### Authentication profile

Humans authenticate through a same-origin HTTPS cookie session using Identity. Session lifetime is 8 hours with no sliding extension. State-changing cookie requests require ASP.NET antiforgery validation, including login. Public registration, email recovery, external providers and MFA are deferred. Bootstrap and emergency reset are operator CLI actions while the host is stopped.

Service clients use BOS-issued opaque API tokens tied to an active service principal. Generate 256 random bits using .NET cryptographic APIs; persist SHA-256 of the random secret, a lookup ID, scopes, expiry and revocation timestamp. Compare hashes in constant time. Tokens default to 30 days, maximum 365. Plaintext is disclosed once. This small API-token adapter supplements Identity; it is not an OAuth/OIDC server.

### Roles and claims

Roles contain grants `{action,resource}`; resource is an exact type/operation/flow name or `*`. Grants are additive, deny by default. Claims are server-maintained user facts; clients cannot self-assert them. Initial grants use roles only. Runtime conditional policy authoring is reserved for a later module; initial business state checks live in named operations and flows protected by operation-only types.

### Enforcement context

At every write transaction start and every standalone read request, reload principal activity and effective role grants from storage. Effective access is the intersection of principal grants, API-token scopes if present, and flow/operation capabilities. Cookie claims alone are not authoritative. Grant changes take effect on the next transaction boundary; ongoing atomic transactions use their authorization snapshot.

### Administration

An initial admin is created by local CLI with a one-use bootstrap secret file. Disablement revokes tokens and invalidates session security stamps. Prevent disabling or demoting the last active administrator. Passwords, authentication cookies and tokens never enter flow context, logs, examples or run receipts.

## 09 Authorization and lifecycle guards

| Grant | Scope and use |
| --- | --- |
| `types.read` / `types.manage` | Type name or `*`; draft editing and publication require manage |
| `record.read` / `record.create` / `record.update` / `record.delete` | Type name; read permits all records and fields of that type |
| `operations.execute` | Registered business operation name |
| `flows.read` / `flows.manage` / `flows.execute` | Flow name; publication requires manage |
| `runs.readAll` | Operator access to all run metadata and permitted result inspection |
| `identity.manage` / `audit.read` / `system.manage` | Administrative capabilities scoped to `*` |

### Capabilities are ceilings

A flow manifest declares exact operations and record permissions it may use; no wildcard capabilities in published flows. Possessing a flow execution grant does not grant its record permissions. A named operation declares an equivalent capability ceiling. Identity is never elevated to the publisher or administrator during execution.

### Business write gates

For `operationsOnly` types, a mutation must occur under an allowed registered operation. The dispatcher preserves a `guardOwner` equal to the nearest registered business operation. Calls into built-in records methods preserve that owner; another custom operation becomes the new owner. A direct generic record call has no `guardOwner` and cannot impersonate an allowed operation. Custom flows do not automatically become privileged business operations. Guarded fields require `managedBy` permission even inside otherwise permitted writes.

### Example

`MaterialRequest` and `MaterialRequestLine` are `operationsOnly`. `CreateRequest` and `ReviseRequest` control entry; `RepriceRequestLine` and `FinalizeRequest` control submission, and `ApproveRequest` controls approval. `ReviseRequest` checks the parent status is Draft. Submission recalculates and updates lines/request inside the caller transaction. A YAML flow can orchestrate allowed operations but cannot bypass the state transition by editing status directly.

### Read visibility

The pilot uses type-level permissions, without row filters or hidden fields. This is an explicit limitation. `get` of an unreadable or nonexistent record returns `NOT_FOUND`. Denied queries return `FORBIDDEN`. If field/row policies are later introduced, list/count/aggregate/history/run receipts must apply them consistently before they are enabled.

### Run results

Only the originating principal may read its runs unless `runs.readAll` applies. Returning result/step data additionally requires current `flows.execute` and read rights for every type touched by the run; otherwise return metadata only with `resultRedacted=true`. Administrative result access requires the same type read grants. Security and audit operators may see failure metadata without business payloads.

### Reference permission fixture

Requester: read/create/update both request types; execute `CreateRequest`, `ReviseRequest`, `RepriceRequestLine` and `FinalizeRequest`; execute/read `SubmitMaterialRequest` flow. Approver: read/update `MaterialRequest` and execute `ApproveRequest`, which checks Submitted status and expected revision. Neither role gets delete or type/flow/identity management. Admin receives explicit management grants; all live transactions still pass schema and write guards.

## 10 Registered operation contract

```text
OperationDescriptor = {
  name, contractVersion, description, inputSchema, outputSchema,
  mode: "read" | "write", flowCallable: true | false,
  capabilities: ["record.read:MaterialRequest", ...]
}

ExecuteAsync(ExecutionContext, UnitOfWork, Input, CancellationToken)
  -> validated Output
```

### Registry

Register trusted .NET implementations at startup. All descriptors use immutable contract versions; name/version pairs must be unique. Mark a narrow set of record services and business operations `flowCallable`. Type administration, Identity management, backup, flow publication, run invocation and arbitrary reflection remain unavailable to flows. Registry validation fails startup on duplicate names or unresolved schemas.

### Invocation

`operations.call` validates the descriptor input, authenticates/authorizes the operation, intersects capabilities and invokes the implementation using the current transaction when present. A top-level write call starts its own transaction. A read operation called from a write flow reuses that transaction. Write calls from a read-only flow fail `MODE_VIOLATION`. Validate output before permitting commit.

### Error and side-effect rules

An operation may throw a typed BOS error. Any failed operation poisons the enclosing transaction; flows have no catch/continue in 1.0. Methods must honor cancellation and bounds. They must not open another writable database connection, commit independently, perform filesystem/network I/O or send external messages while executing a transactional operation.

### Bounded composition

Trusted .NET operations may call other registered operations only through the dispatcher, preserving identity, budgets and transaction. Depth is capped at 8 and cycles fail publication/startup where statically known and fail runtime at the cap. YAML flow-to-flow calls remain deferred. No user-controlled type names or method names are resolved through reflection.

### Discovery and validation

`list`/`get` exposes only descriptors permitted to the caller, including schemas, versions and flow-callability. `validate` returns structural input errors without execution. It is advisory and cannot approve a future write. Operations should describe business intent precisely enough that a human client or AI agent can use the same contract.

## 11 YAML definition grammar

```yaml
format: bos.flow/1.0
name: SubmitMaterialRequest
mode: command
transaction: atomic
inputSchema: {fields: {requestId: {kind: string, required: true}}}
outputSchema: {fields: {requestId: {kind: string, required: true}}}
dependencies:
  types: {MaterialRequest: [1], MaterialRequestLine: [1]}
  operations: {records.get: 1, records.list: 1}
capabilities:
  - record.read:MaterialRequest
  - record.read:MaterialRequestLine
steps: []
```

### Safe parsing profile

Use YamlDotNet as a parser adapter, with an event-level prevalidation pass. Accept exactly one UTF-8 document containing mappings, sequences and scalar nodes. Reject duplicate keys, aliases, anchors, merge keys, custom tags and non-string mapping keys. Never deserialize arbitrary CLR types. YAML source is data parsed into closed DTOs. [R4]

### Scalars and canonical form

Use JSON-like scalar values: text (quoted or plain unless reserved), `true`, `false`, `null` and bounded metadata integers. Business numbers must appear in typed expressions or quoted record fields. Reject implicit YAML timestamps, octal/hex floats and non-finite values. The canonical instruction tree is stored as JSON; YAML layout/comments have no execution meaning.

### Manifest rules

`mode` is `command` or `query`. `command` requires `transaction: atomic`. `query` requires `transaction: readOnly` and cannot mutate. All dependencies are explicit versions; first release rejects mutable `latest` references. Operation and type names referenced by steps must appear in the manifest. The host adds published version, hashes and publisher metadata. Unknown properties fail.

### Publication checks

Every step has a unique `id` within the entire flow, including branches/loops. Validate operand types and variable visibility where statically possible, with runtime checks at every dynamic boundary. Pin the compiled AST format version. A host without that AST version returns `DEPENDENCY_UNAVAILABLE`; it never evaluates the original text using a different language silently.

## 12 Expression language and typing

| Value expression | Meaning |
| --- | --- |
| `{literal: value}` | Literal JSON-compatible value; record-schema conversion occurs at the operation boundary |
| `{decimal: "12.50"}` | Exact decimal value; maximum 28 significant digits |
| `{integer: "12"}` | Checked signed 64-bit integer |
| `{path: "request.data.status"}` | Read a variable or object field using a literal dotted path |
| `{fn: "equal", args: [a,b]}` | Call an allowlisted pure helper |
| `{object: {field: expression}}` | Construct an object from evaluated named fields |
| `{array: [expression, ...]}` | Construct an ordered array |

### No string evaluation

Expressions are structured nodes. A string containing `${...}`, C#, JavaScript, SQL or a method path is ordinary text; it is never executed. Each expression mapping contains exactly one discriminator, except `fn` which also requires `args`. Dynamic path construction, method invocation on returned objects and access to CLR members are unsupported.

### Path and missing semantics

Paths start at `input`, `ctx` or a visible variable. Permit field segments only; array indexing uses a future explicit helper. Unknown path at compile time fails; an optional absent field evaluates to a Missing sentinel. Reading through null/missing returns Missing. Only `isMissing`, `isNull`, `isEmpty` and `coalesce` accept Missing; other helpers and output serialization fail `MISSING_VALUE`.

### Typing and coercion

Runtime values are String, Boolean, Integer, Decimal, Date, Instant, Null, Missing, Array and Object. No implicit numeric/string conversion or boolean truthiness. Operation binding uses its declared schema to decode literal strings and serialize typed values. `equal` compares same-kind scalars and supports Null equality; structural object equality is deferred. Mixed integer/decimal arithmetic requires explicit conversion.

### Evaluation order

Evaluate arguments left to right, except `and`/`or`/`coalesce` which short-circuit. Helpers are pure and cannot query records. Expression depth is capped at 16; each evaluated node consumes the shared step budget. Parsing and evaluation fail fast on invalid arguments or arithmetic overflow.

### State and time

`ctx` exposes `principalId`, `runId`, `startedAt` and `workspaceTimezone` only. `now` returns `ctx.startedAt`. `today` uses the captured instant with an explicit timezone. Date arithmetic counts calendar dates; instant subtraction is not silently substituted. Dictionary iteration order must never determine business results.

## 13 Flow instruction semantics

| Instruction | Required fields and behavior |
| --- | --- |
| `let` | `id`, `let: identifier`, `value: expression`. Bind once in current lexical scope. |
| `call` | `id`, `call: operationName`, `args: parameter-to-expression map`, optional `as` identifier. |
| `if` | `id`, `if: boolean expression`, `then: steps`, optional `else: steps`. Execute one branch. |
| `forEach` | `id`, `forEach: expression`, `as: item variable`, `maxItems: positive integer`, `steps`. |
| `require` | `id`, `require: boolean expression`, `code` and `message` literals. False aborts. |
| `log` | `id`, `log: message literal`, optional `fields` map and `level` info/warn. Bounded metadata only. |
| `return` | `id`, `return: expression`. Validate output and end the entire run. |

### Transactions as a manifest setting

The earlier transaction block concept is fixed as `transaction: atomic` for the entire command flow in 1.0. There are no nested or multiple transaction blocks, loose begin/commit calls or partially committed segments. This removes ambiguity about which steps roll back. Query flows use one read snapshot.

### Scope

`input` and `ctx` are read-only roots. `let` and `as` bindings cannot overwrite an existing visible name. Branch-local and iteration-local variables do not escape their block. Flow definitions cannot mutate variables or returned record objects. Build patches explicitly with object expressions. For totals, use `sum` on a bounded scalar list through the pure `pluck` helper; mutable accumulators are deferred.

### Iteration

`forEach` evaluates its source once and requires an array already within `maxItems` and the global limit. Exceeding the limit fails before its first iteration; no silent truncation. Iteration is sequential, each item gets a fresh scope, and the whole run shares one budget and transaction. A return inside a loop ends the flow; there is no break/continue initially.

### Termination

Require a return reachable on every control path. Runtime fallthrough returns `MISSING_RETURN` and rolls back. A failed call, require, expression, output validation or limit aborts the run. There is no catch, rollback handler or onError that can rescue and commit partial work in the pilot.

### Nested execution

`flows.call` is deferred. Trusted dispatcher composition still shares budgets and transaction. No flow may recursively submit or run another flow, create a schedule or publish code.

## 14 Transaction and concurrency algorithm

### Atomic command execution

1. Authenticate and validate the run request; resolve the exact flow version. In a short write transaction, store a running receipt/run ID and request hash. Commit this reservation before execution. It is not a durable job queue.
2. Acquire a write unit of work with `BEGIN IMMEDIATE`. Recheck current principal, scopes, flow status and all required capabilities. Load pinned definitions and operation versions. Capture `startedAt` and the run deadline. Execute the validated AST sequentially.
3. Every record read, query, write, reference check and index update uses that same connection/transaction. Enforce expected revisions and business guards on every mutation. Reads see staged writes. A caught host exception still poisons the transaction.
4. Validate the flow output, finalize record history/audit, and set run succeeded with output and receipt in the same business transaction. Commit once; only then return success. Failure before commit rolls back all business state.
5. On failure, persist sanitized error and failed state in a separate short transaction. If that persistence fails, the run remains running and startup recovery marks it interrupted. Never invent success.

### Serialization

SQLite permits a single writer; `BEGIN IMMEDIATE` reserves the write transaction before business decisions read state. This prevents concurrent functions from both consuming the same last stock unit if their operation checks the invariant within that transaction. WAL permits committed snapshot reads alongside the writer. [R3]

### User intent and revisions

Every update/delete requires `expectedRevision`. Serialization protects in-transaction invariants; `expectedRevision` also protects a user decision based on an earlier displayed record. Fail stale revisions rather than silently re-reading and applying the old intent. No automatic retry of business decisions in 1.0.

### Read-only runs

Query flows execute in a read snapshot. Their result receipt is persisted after the snapshot closes. A crash before receipt persistence yields interrupted; re-running explicitly may observe newer data. They have no business mutations. All log output is provisional until associated with the terminal run status.

## 15 Run receipts, idempotency and interruption

### States

Run state is `running`, `succeeded`, `failed` or `interrupted`. The pilot has no queued/waiting/retrying state. The initial running reservation is durable. A succeeded command stores its business changes and final receipt atomically. On startup, after acquiring the exclusive workspace lock and database recovery, remaining running runs become interrupted; they are not automatically restarted.

### Idempotency

All authenticated mutating BOS methods require `Idempotency-Key`: 16–128 ASCII characters. Scope is `principalId` plus method/target; hash canonical arguments and expected revisions. The same key/hash returns the original terminal receipt without another execution. A different hash returns `IDEMPOTENCY_CONFLICT`. A still-running key returns 202 with `runId`; an interrupted key returns 409 `RUN_INTERRUPTED` and its ID. Keys remain reserved indefinitely in the pilot.

Malformed or unauthorized requests do not reserve keys. Direct record/admin commands store successful receipts inside their transaction; failed direct commands leave no receipt and may be attempted again. Accepted flow failures retain their receipt. Authenticate and reauthorize before disclosing any replay result. Authentication credentials and plaintext passwords never enter receipt payloads. Secret-setting exceptions are specified in Section 37.

### Authentication exceptions

Login, logout, antiforgery issuance and password verification use their authentication endpoint rules instead of the command-receipt protocol. Token creation and administrative password changes retain redacted receipts; their replay behavior is specified in Section 37. A lost issuance response requires revoke/reset and a new key.

### Unknown outcomes

If a response is lost, replay its key or poll `getRun`; do not submit a new key until outcome is known. A database commit error of uncertain outcome must be resolved using a fresh connection and the stored run/receipt. The host cannot classify it as safe to retry merely because HTTP failed.

### Disconnect and shutdown

Client disconnect requests cancellation; deadline and cooperative database interruption remain active. If commit already completed, success remains authoritative. Shutdown stops admission, waits up to 10 seconds for bounded runs and cancels remaining work. An uncooperative trusted host operation can require process termination; such a failure is a release defect, not a user script feature.

## 16 Storage model and repository ports

| Storage group | Key and invariant |
| --- | --- |
| `TypeDrafts`, `TypeVersions`, `TypeActive` | Draft revision; immutable `(name,version)`; active pointer to published version |
| `FlowDrafts`, `FlowVersions`, `FlowActive` | Source, AST, hashes, pinned dependencies, disabled flag |
| `Records`, `RecordHistory` | UUID PK; type/version/revision/data JSON; history PK `(id,revision)` |
| `RecordReferences`, `UniqueValues`, `IndexValues` | Atomic relationship, unique and typed search entries |
| Identity tables, `RoleGrants`, `ApiTokens` | Identity-managed users/roles; grants and hashed scoped tokens |
| `Runs`, `RunSteps`, `CommandReceipts` | Run ID; bounded step metadata; unique principal/scope/key |
| `Audit`, `RuntimeMetadata` | Monotonic sequence; actors/correlations; engine schema and workspace keys |

```text
IUnitOfWorkFactory.BeginRead / BeginWrite(context, deadline)
IUnitOfWork: Definitions, Records, Identity, Runs, Receipts, Audit
IWriteUnitOfWork.CommitAsync / RollbackAsync / DisposeAsync
IRecordRepository.Get / Query / Count / Insert / Update / Tombstone
IRecordRepository.SetReferences / SetIndexes / AppendHistory
IDefinitionRepository.GetDraft / SaveDraft / Publish / Resolve
IRunRepository.Reserve / Complete / Fail / MarkInterrupted
IReceiptRepository.Get / InsertUnique
IOperationRegistry.Resolve(name, contractVersion)
IFlowCompiler.Compile(source) -> validated AST
IFlowExecutor.Execute(ast, context, unitOfWork, budget)
IClock.UtcNow / IIdGenerator.NewId / IBackupAdapter.Snapshot
```

### Persistence requirements

Enable `foreign_keys` on every connection, WAL, `synchronous=FULL`, `read_uncommitted=OFF` and a 1-second busy timeout. All writes begin explicitly. Store custom record data as canonical JSON text; adding a type does not create a new relational table. `IndexValues` must preserve typed ordering and separate missing/null sentinels; numeric values cannot sort as plain strings. All index comparators must match the core ordinal/numeric comparators.

Identity administrative writes must use the same underlying transaction when combined with grants/audit/receipts. Ensure the Identity store and BOS repository share transaction ownership; separate independently committing contexts fail conformance. Repositories return bounded results, never lazy unbounded database enumerators.

### Schema and constraints

Use composite foreign keys for record type/version, uniqueness constraints for receipts and unique values, and indexed reverse references. Enforcement must survive concurrent calls. A future adapter must provide serializable business decisions, atomic receipts and recovery with equivalent semantics.

## 17 HTTP and internal SDK mapping

### One method registry

For compactness, expose `POST /v1/methods/{methodName}` with a JSON argument object matching the catalogue. Example: `/v1/methods/records.get` with `{type,id}`. Read-only methods use POST too and are declared `readOnly` in the registry; they require no idempotency key and create no business changes. This route does not expose reflection: dispatch is by registered descriptor only.

```http
POST /v1/methods/records.update
Authorization: Bearer <service-token>
Idempotency-Key: request-update-0001
Content-Type: application/json

{"type":"Note","id":"<uuid>","expectedRevision":2,"patch":{"title":"Revised title"}}
```

```json
{"result":{"id":"<uuid>","revision":3,"data":{}},"meta":{"traceId":"<uuid>"}}
```

The response above illustrates the result envelope; a real full-record result includes the complete Record contract.

### Flow invocation

`POST /v1/methods/flows.run` takes `{name,version,input}`. A normally completed run returns 200 with RunReceipt including succeeded or failed status; a business failure is inside that receipt because execution was accepted. Admission failures use HTTP errors. Duplicate still-running invocation returns 202. `getRun` returns 200 for any visible existing run state.

### Additional routes

Authentication uses `/v1/auth/antiforgery`, `/login`, `/logout` and `/change-password`. `/v1/openapi.json` and `system.getCapabilities` expose versioned method schemas. `/health/live` returns only liveness; `/health/ready` returns a basic status publicly and details only to operators. Bind loopback by default; non-loopback requires explicit HTTPS or a trusted TLS proxy.

| HTTP code | Meaning |
| --- | --- |
| 400 / 413 / 415 | Malformed request or cursor / body too large / wrong media type |
| 401 / 403 / 404 | Unauthenticated / forbidden method or type / unavailable object |
| 409 / 412 | Conflict including uniqueness, references, key or schema / revision mismatch |
| 422 | Validation, business guard or unsupported feature error |
| 429 / 503 / 500 | Rate/capacity limit / storage busy or not ready / internal failure |

Public errors use `{error:{code,message,details:[{path,code,message}],traceId,retryable}}`; all fields are present and details may be empty. Never return SQL, stack traces, filesystem paths or credentials. Expected revisions are explicit arguments in this RPC profile; `If-Match` is not additionally required. OpenAPI MUST describe every initial method, its arguments, result, grant and mutation flag.

## 18 Type methods and their contracts

All methods in this section are initial unless marked Later. Context/cancellation are implicit internal parameters, never caller-supplied identity fields.

| Method and arguments | Result and rules |
| --- | --- |
| `types.create(definition)` | TypeDraft `{name,draftRevision:1,definition}`. `types.manage`; duplicate name fails. |
| `types.update(name,definition,expectedRevision)` | Updated TypeDraft. Edits only draft; published versions remain immutable. |
| `types.validate(definition)` | ValidationReport. `types.manage`; no write or publication. |
| `types.publish(name,expectedRevision)` | PublishedType `{name,version,hash,publishedAt}`. Validates safe evolution. |
| `types.get(name,version?)` | PublishedType; omitted version selects active. `types.read` or `record.read`. |
| `types.list(query)` | Page<TypeSummary>; filter only name/status; no hidden type discovery. |
| `types.listVersions(name,query?)` | Page<TypeVersionSummary> in ascending version order. |
| `types.getDraft(name)` | TypeDraft. `types.manage`; added to make editing the published type practical. |
| `types.activate(name,version)` | **Later:** move active pointer, preserve old records. |
| `types.archive(name)` | **Later:** prevent creates, retain reads and history. |
| `types.compare(name,fromVersion,toVersion)` | **Later:** schema difference report without conversion. |

### Common method rules

Mutations require a command key. Draft revision is separate from published version. List methods default to 50 and cap at 200. Names and versions are validated before retrieval. Schema compilation returns paths such as `fields.quantity.scale`; no partial type is published when one field fails.

### Name creation and evolution

For a new name, create makes the draft and reserves the name. For an existing published type, update acts on its retained draft using its current `draftRevision`. Publishing does not destroy the draft; it records the published hash and increments `draftRevision` to prevent stale follow-up edits. Any unchanged publication fails `NO_CHANGE`.

### Direct record creation policy

A caller may create records only against the active version through the public API. A registered operation or flow may create a pinned version declared in its dependency contract. The ordinary client cannot choose an obsolete type version to bypass newer constraints.

## 19 Record and query methods

| Method and arguments | Result and initial behavior |
| --- | --- |
| `records.create(type,data)` | Record. Active schema for API, pinned schema for an operation/flow. |
| `records.get(type,id)` | Record; `NOT_FOUND` for unreadable, deleted or missing record. |
| `records.update(type,id,patch,expectedRevision)` | Record; shallow top-level assignments; require existing revision. |
| `records.delete(type,id,expectedRevision)` | TombstoneReceipt; no incoming live references. |
| `records.validate(type,data,typeVersion?)` | ValidationReport for complete proposed data; no defaults persisted. |
| `records.list(type,query)` | Page<Record or projected Record>; bounded typed query. |
| `records.count(type,filter)` | `{count:string}`; exact count within deadline/budget or `QUERY_LIMIT`. |
| `records.validateUpdate(type,id,patch,expectedRevision)` | **Later:** merged validation without writing. |
| `records.find(type,id)` | **Later:** accessible Record or null. |
| `records.replace(type,id,data,expectedRevision)` | **Later:** replace complete data; guards still apply. |
| `records.restore(type,id,expectedRevision)` | **Later:** validate references and uniqueness before restoration. |
| `records.getHistory(type,id,query)` | **Later** public API; internal history still mandatory in 1.0. |
| `records.getRevision(type,id,revision)` | **Later:** immutable historical snapshot. |
| `records.exists(type,filter)` | **Later:** true if one accessible match exists. |
| `records.first(type,query)` | **Later:** first matching record or null under explicit sort. |
| `records.single(type,filter)` | **Later:** fail unless exactly one accessible match. |
| `records.getMany(type,ids)` | **Later:** bounded explicit IDs with per-record visibility. |
| `records.aggregate(type,query)` | **Later:** typed permitted measures and groupings. |
| `records.distinct(type,field,query)` | **Later:** bounded distinct values. |

### Flow exposure

Initial get/list/count/validate are available as read activities; create/update/delete are write activities. Every call requires the relevant type grant and manifest capability, and obeys operation-only guards. The SDK returns typed immutable values rather than CLR entity objects.

### Counting and validation permission

Count requires `record.read`. Validate requires `record.create` or `record.update` plus `record.read`. Reference checks reveal only authorized targets. A validation error may say a reference is unavailable without distinguishing missing from forbidden.

## 20 Operation and flow methods

| Method and arguments | Result and access |
| --- | --- |
| `operations.list(query?)` | Page<OperationDescriptor>; visible executable operations. |
| `operations.get(name,contractVersion?)` | OperationDescriptor; default current registered contract. |
| `operations.validate(name,input,contractVersion?)` | ValidationReport; schema checks only. |
| `operations.call(name,input,contractVersion?)` | Validated output; execute grant for custom operation, record grants for built-in `records.*`. |
| `flows.create(definition)` | FlowDraft revision 1; `flows.manage`. |
| `flows.update(name,definition,expectedRevision)` | FlowDraft; rejects stale revision. |
| `flows.getDraft(name)` | FlowDraft; `flows.manage`. |
| `flows.validate(definition)` | CompileReport `{valid,errors,warnings,dependencies}`; no business execution. |
| `flows.publish(name,expectedRevision)` | PublishedFlow `{name,version,hash}`; pins dependencies. |
| `flows.get(name,version?)` | PublishedFlow; `flows.read` or execute; source requires manage. |
| `flows.list(query)` | Page<FlowSummary>; only visible flows. |
| `flows.listVersions(name,query?)` | Page<FlowVersionSummary>; no implicit activation. |
| `flows.run(name,version,input)` | RunReceipt under caller identity; explicit version required. |
| `flows.getRun(runId)` | RunReceipt with reauthorized output or `resultRedacted`. |
| `flows.listRuns(query)` | Page<RunSummary>; default own runs only. |
| `flows.getRunSteps(runId,query?)` | Page<StepResult>; statuses/timings/errors, no automatic payload dumps. |
| `flows.disable(name,expectedRevision)` | FlowControl `{disabled:true,revision}`; reject new runs. |
| `flows.enable(name,expectedRevision)` | FlowControl `{disabled:false,revision}`. |
| `flows.call(name,version,input)` | **Later:** nested flow under shared budget and transaction. |
| `flows.cancel(runId)` | **Later:** cancellation request, never undo a commit. |
| `flows.enqueue(name,version,input,options?)` | **Later:** durable job admission and queue semantics. |

FlowControl revision is returned by get/list and is independent of draft/published version. Definition/control mutations use `flows.manage`. Disabling affects new runs and runs not yet inside their execution transaction; it does not retroactively reverse committed state. In-flight atomic runs keep their authorization/control snapshot.

## 21 Identity administration and operator methods

| Method | Initial result and behavior |
| --- | --- |
| `identity.getCurrent()` | `{id,displayName,roles,effectiveGrants}`; flow receives only safe ctx fields. |
| `authorization.can(action,resource)` | Boolean advisory check; data operations always recheck. |
| `users.create(user)` | UserSummary; admin creates inactive account pending local activation/reset. |
| `users.get(id)` and `users.list(query)` | UserSummary/Page; `identity.manage`. |
| `users.update(id,changes,expectedRevision)` | Updated summary; displayName/active only, never password hashes. |
| `users.disable(id,expectedRevision)` | Disable, revoke tokens and update security stamp atomically. |
| `users.setPassword(id,newPassword,expectedRevision)` | Admin HTTPS endpoint/CLI; secret handling, stamp invalidation, no password receipt. |
| `roles.create(role)` | Role `{id,name,grants,revision}`; validate known actions. |
| `roles.update(id,changes,expectedRevision)` | Role; replace complete grants list. |
| `roles.get(id)` and `roles.list(query)` | Role/Page; `identity.manage`. |
| `roles.assign(userId,roleId,expectedUserRevision)` | Updated user membership; audit and revision increment. |
| `roles.remove(userId,roleId,expectedUserRevision)` | Remove membership; preserve last active admin. |
| `tokens.create(principalId,scopes,expiresAt?)` | One-time token + metadata; scopes cannot exceed issuer administration authority. |
| `tokens.list(principalId,query)` | Metadata only; `identity.manage`. |
| `tokens.revoke(id)` | Revocation receipt; subsequent requests denied. |
| `audit.list(query)` | Page<AuditEntry>; `audit.read`; payloads redacted by default. |
| `system.getCapabilities()` | Supported format versions, initial methods, limits and schemas. |
| `system.getHealth()` | Readiness, storage/schema checks and basic queue-free execution metrics. |
| `system.backup(destination)` | Operator CLI only, stopped host; verified snapshot manifest. |
| `system.restore(source)` | Operator CLI only, empty target directory and stopped host. |

`authorization.require` is an internal application method, not a public endpoint. `users.setPassword` is an administrative recovery facility; human password change requires current-password verification through `/v1/auth/change-password`. Never let business flows call identity or operator administration.

## 22 Pure helper catalogue

| Helper family | Arguments, result and rules |
| --- | --- |
| `equal(a,b)`, `notEqual(a,b)` | Boolean; same typed scalar kinds; null comparison allowed. |
| `greaterThan`, `greaterOrEqual`, `lessThan`, `lessOrEqual` | Two same-kind ordered scalars → Boolean; null/missing invalid. |
| `and(values...)`, `or(values...)`, `not(value)` | Boolean operands only; short-circuit left to right. |
| `isNull(value)`, `isMissing(value)` | Boolean; distinguish explicit null from absent value. |
| `isEmpty(value)` | True for missing/null, empty text, array or object; false for zero/false. |
| `coalesce(values...)` | First value neither missing nor null; result may be null if none. |
| `add`, `subtract`, `multiply` | Two Integers or two Decimals → same kind; checked precision. |
| `divide(a,b,scale,rounding)` | Decimal operands → Decimal at explicit scale; zero/overflow fail. |
| `round(value,scale,rounding)`, `abs(value)` | Exact numeric result; rounding names `halfEven`, `halfAwayFromZero`, `towardZero`. |
| `length(list)`, `contains(list,value)` | Integer count / Boolean; scalar typed equality for contains. |
| `sum(list)`, `min(list)`, `max(list)` | Homogeneous numeric list; sum(empty)=decimal zero; min/max empty fail. |
| `pluck(list,path)` | Bounded scalar array selected by a literal field path; missing value fails. |
| `trim`, `concat`, `lower`, `upper` | Text only; ordinal invariant behavior; output length cap. |
| `startsWith(text,prefix)`, `endsWith(text,suffix)` | Ordinal case-sensitive Boolean. |
| `today(timezone?)`, `now()` | Date from captured instant / captured UTC Instant. |
| `addDays(date,days)`, `daysBetween(start,end)` | Date / signed Integer; calendar days, no implicit instant arithmetic. |
| `toString(value)` | Canonical scalar representation; no object dumps. |
| `parseInteger(text)`, `parseDecimal(text)`, `parseDate(text)` | Strict conversion or `CONVERSION_FAILED`; no locale guessing. |

### Required helper set

Implement all helpers in this table for 1.0 so published flow expressions have one stable vocabulary. `pluck` is explicitly added to the earlier catalogue so immutable flow variables can produce useful totals without introducing mutable loops. Each helper has one registered versioned descriptor for tooling and type checking.

### Budgets

Every examined list item consumes a step budget unit, including sum/min/max/contains/pluck. Object construction and concatenation check result sizes before materializing excessive allocations. The same budget covers all expressions, steps, loops and nested trusted operation calls. Strings never turn into executable expressions.

## 23 Reserved methods and extension contracts

| Reserved method | Intended behavior before future specification |
| --- | --- |
| `records.getRelated(type,id,field,query)` | Paged related records under current permissions |
| `records.link(type,id,field,targetId,expectedRevision)` | Add a supported reference through normal write validation |
| `records.unlink(type,id,field,targetId,expectedRevision)` | Remove reference while preserving schema constraints |
| `records.createMany(type,items)` | Bounded atomic batch; return all created envelopes |
| `records.updateMany(type,items)` | Explicit IDs and revisions; all-or-nothing batch |
| `records.deleteMany(type,items)` | Explicit IDs/revisions; reference-safe atomic batch |
| `records.migrate(type,id,targetVersion,data,expectedRevision)` | Controlled version transition without changing identity |
| `policies.create(definition)`, `policies.update(...)` | Draft runtime rules built from restricted conditions |
| `policies.validate(definition)`, `policies.publish(...)` | Validate/publish immutable resource policies |
| `events.enqueue(name,payload)` | Transactional outbox event created with business commit |
| `triggers.create(definition)`, `triggers.list(query)` | Bind supported events to pinned published flows |
| `triggers.enable(id)`, `triggers.disable(id)` | Audited enablement with revision preconditions |

### Reserved lifecycle additions

The Later rows in Sections 18–20 also belong to this reservation list. All reserved methods return `FEATURE_NOT_AVAILABLE` and remain absent from enabled capabilities. Future role/row/field policies must define query filtering, counting, history access and receipt redaction together. Future background jobs must define claim ownership, recovery and idempotent effects before being enabled.

### Approval waits

For the pilot, approval is a saved `ApprovalTask` record plus a later manual `ApproveRequest` operation/run. A function never sleeps until tomorrow or holds an open transaction while awaiting a person. Recheck the current approver and record revision when the approval is acted upon.

### External systems

HTTP, email, payments and filesystem actions require future after-commit delivery and stable effect IDs. They cannot be made rollback-safe by wrapping a YAML step in the database transaction. The current interpreter exposes none of them.

### Commercial extension

Internal trusted publishing is the initial threat model. Public customer flow authoring, multiple tenants, native extensions, arbitrary scripts and app marketplaces each require separate security and lifecycle specifications. Preserve the narrow method registry when extending the product.

## 24 Reference record definitions

These schemas define the initial test application. The two types use `operationsOnly` to keep lifecycle rules enforceable. Publish the parent type first; registered allowed operation names must already exist.

```json
{
  "name": "MaterialRequest",
  "description": "Material requisition",
  "accessMode": "operationsOnly",
  "allowedOperations": ["CreateRequest", "ReviseRequest", "FinalizeRequest", "ApproveRequest"],
  "fields": {
    "title": {"kind": "string", "required": true, "maxLength": 200},
    "status": {"kind": "enum", "values": ["Draft", "Submitted", "Approved"], "required": true, "default": "Draft"},
    "total": {"kind": "decimal", "precision": 18, "scale": 2, "required": true, "default": "0.00"},
    "currency": {"kind": "enum", "values": ["LKR", "USD"], "required": true},
    "contentVersion": {"kind": "integer", "required": true, "minimum": "1", "default": "1", "managedBy": ["CreateRequest", "ReviseRequest"]}
  }
}
```

```json
{
  "name": "MaterialRequestLine",
  "description": "Requested item",
  "accessMode": "operationsOnly",
  "allowedOperations": ["CreateRequest", "ReviseRequest", "RepriceRequestLine"],
  "fields": {
    "requestId": {"kind": "reference", "targetType": "MaterialRequest", "required": true, "indexed": true},
    "item": {"kind": "string", "required": true, "maxLength": 200},
    "quantity": {"kind": "decimal", "precision": 12, "scale": 3, "minimum": "0.001", "required": true},
    "unitPrice": {"kind": "decimal", "precision": 18, "scale": 2, "minimum": "0.00", "required": true},
    "amount": {"kind": "decimal", "precision": 18, "scale": 2, "required": true, "default": "0.00"}
  }
}
```

### Required fixture

Create one Draft LKR request with two lines: quantity `2.000` at `150.00` and quantity `3.000` at `200.00`. Initial amounts/total are `0.00`. Successful submission must produce amounts `300.00` and `600.00`, total `900.00`, and status Submitted. An overflow fixture uses individually valid second-line quantity and unitPrice whose product exceeds amount precision, failing after the first staged write.

## 25 Reference flow manifest and reads

The original source spans Sections 25–26. It is combined here into one YAML document, including the writes and return described in Section 26. Record metadata revisions are typed Integers in the SDK, even though the HTTP envelope serializes them as JSON numbers.

```yaml
format: bos.flow/1.0
name: SubmitMaterialRequest
mode: command
transaction: atomic
inputSchema:
  fields:
    requestId: {kind: string, required: true}
    revision: {kind: integer, required: true}
outputSchema:
  fields:
    requestId: {kind: string, required: true}
    total: {kind: decimal, precision: 18, scale: 2, required: true}
dependencies:
  types: {MaterialRequest: [1], MaterialRequestLine: [1]}
  operations:
    records.get: 1
    records.list: 1
    RepriceRequestLine: 1
    FinalizeRequest: 1
capabilities:
  - record.read:MaterialRequest
  - record.read:MaterialRequestLine
  - record.update:MaterialRequest
  - record.update:MaterialRequestLine
  - operations.execute:RepriceRequestLine
  - operations.execute:FinalizeRequest
steps:
  - id: request
    call: records.get
    args:
      type: {literal: MaterialRequest}
      id: {path: input.requestId}
    as: request
  - id: checkStatus
    require:
      fn: equal
      args: [{path: request.data.status}, {literal: Draft}]
    code: INVALID_STATE
    message: Only a draft request can be submitted
  - id: loadLines
    call: records.list
    args:
      type: {literal: MaterialRequestLine}
      query:
        object:
          where:
            array:
              - object:
                  field: {literal: requestId}
                  op: {literal: eq}
                  value: {path: input.requestId}
          limit: {literal: 100}
    as: lines
  - id: bounded
    require: {fn: isNull, args: [{path: lines.nextCursor}]}
    code: TOO_MANY_LINES
    message: This flow accepts at most 100 lines
  - id: nonempty
    require:
      fn: greaterThan
      args:
        - {fn: length, args: [{path: lines.items}]}
        - {integer: "0"}
    code: EMPTY_REQUEST
    message: At least one line is required
  - id: priceLines
    forEach: {path: lines.items}
    as: line
    maxItems: 100
    steps:
      - id: priceLine
        call: RepriceRequestLine
        args:
          id: {path: line.id}
          expectedRevision: {path: line.revision}
  - id: finalize
    call: FinalizeRequest
    args:
      id: {path: input.requestId}
      expectedRevision: {path: input.revision}
    as: final
  - id: output
    return:
      object:
        requestId: {path: final.id}
        total: {path: final.data.total}
```

## 26 Reference flow writes and result

The complete flow in Section 25 includes this section's bounded check, nonempty check, pricing loop, finalization and return.

### Registered operation behavior

`RepriceRequestLine` reads the line and its parent, requires parent Draft, checks `expectedRevision`, calculates `round(multiply(quantity,unitPrice),2,halfEven)` and updates amount only if it changed; otherwise it returns the existing line without calling update. It returns the updated line and has read permissions for both types plus update permission for the line type. It never commits independently.

`FinalizeRequest` reads the parent using `expectedRevision`, requires Draft, queries all its lines within the same 100-line bound, recomputes each amount independently and checks the stored amounts match. It sums them with checked decimal arithmetic, writes total and status Submitted, and returns the parent. Recalculation here prevents a caller from bypassing pricing by invoking `FinalizeRequest` directly.

### Publication and invocation

Publish the two schemas, then this flow after all four operation descriptors are registered at contract version 1. Invoke with `{requestId:"UUID",revision:"1"}` and a unique command key. On success output is `{requestId:"UUID",total:"900.00"}`. Existing input revision is checked by `FinalizeRequest` before any commit; a mismatch rolls back earlier line writes.

### Rollback proof

Inject a typed test failure in `FinalizeRequest` after the staged line updates. Assert both line amounts and parent total/status retain their previous committed values, no new business history remains, and one failed RunReceipt identifies the finalize step. A production flow cannot enable test failure injection.

## 27 Limits, admission and cancellation

| Resource | Initial limit |
| --- | --- |
| HTTP/record body | 256 KiB request and per record; reject early by byte count |
| YAML/AST | 64 KiB YAML; 256 KiB compiled AST; 500 static steps |
| Schema | 200 fields total; nesting 8; arrays 200 items; 2,000 scalars per record |
| Expressions | Depth 16; 10,000 evaluated nodes/items per invocation |
| Loops | 100 items per loop; global step cap covers nesting |
| Record work | 200 SDK calls; 250 mutations; 1,000 returned record envelopes per run |
| Queries | Page max 200; filter max 10; IN max 50; SQL VM instructions max 1,000,000 per query |
| Duration | 2 seconds execution including DB/operation calls; lock admission max 1 second |
| Output and logs | 64 KiB output; 16 KiB diagnostics; 500 step-result entries |
| Concurrent work | One write flow; at most 4 read flows; no durable execution queue |
| Rate | Per principal 60 write commands and 120 reads per minute |
| Login attempts | Identity lockout after 5 failures for 15 minutes; IP-based admission throttling |
| Storage | 2 GiB initial database budget including WAL; stop business writes below 512 MiB free space |

### Enforcement

Limits can be lowered in configuration; raising the hard maxima requires a tested specification revision. Budget each iteration, expression and method call. Count records returned repeatedly against the run total. Nested dispatcher calls count toward the SDK-call cap. Per-loop maxima are ceilings; combined invocation budgets can reject a smaller input.

Timeouts include all host operations; use database interruption and cooperative CancellationToken checks. Do not describe this as a hard OS-level CPU/memory boundary.

### Memory and parser safety

The parser rejects aliases/anchors before expansion and enforces bytes/depth before full object construction. Bound every string, collection and intermediate expression result to 256 KiB. Trusted operations must obey the same limits. Malformed YAML never reaches a method dispatcher. A malicious native/CLR extension is outside the trusted-operation model.

### Overload

Reject excess concurrency with 429 and `Retry-After`; do not store a misleading queued state. Storage guard rejects new writes before reservation and preserves headroom for failure receipts/operator actions. Disk-full failures still must rollback cleanly. Retention is indefinite in the pilot, so the operator must monitor growth.

## 28 Audit, errors and diagnostics

### Audit entries

Persist audit for successful record mutations, type/flow publication, role/claim changes, token issuance/revocation and operator actions. Include monotonic sequence, timestamp, actor, credential ID, method, command/run ID, object/type/version, old/new revision and outcome. RecordHistory stores full record revisions in the same transaction. No API can edit or delete audit entries in the pilot.

### Run details

RunReceipt contains `runId`, flowName/version/hash, `principalId`, status, startedAt/completedAt, `inputHash`, result or sanitized error, `readTypes` and `changedRecordIds`. StepResult contains `stepId`, iteration path, ordinal, start/duration, outcome and error code. Business input/output snapshots are disabled by default; no secret or whole-record dumping into logs.

### Failure model

Errors have stable code, safe message, field/step path, `traceId` and `retryable` flag. Validation failures list field rules. Runtime failures include flow version, `stepId` and loop index. Do not expose stack traces through user endpoints. No automatic retry uses `retryable` in the pilot; it is diagnostic information for clients/operators.

| Family | Representative codes |
| --- | --- |
| Input/schema | `INVALID_REQUEST`, `INVALID_SCHEMA`, `UNKNOWN_FIELD`, `CONVERSION_FAILED` |
| Access/lifecycle | `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `POLICY_VIOLATION`, `INVALID_STATE` |
| Consistency | `REVISION_CONFLICT`, `UNIQUE_CONFLICT`, `IN_USE`, `NO_CHANGE` |
| Definitions | `SCHEMA_VERSION_MISMATCH`, `DEPENDENCY_UNAVAILABLE`, `FLOW_DISABLED` |
| Execution | `REQUIRE_FAILED` or named business code, `OUTPUT_INVALID`, `MISSING_RETURN` |
| Limits/recovery | `QUERY_LIMIT`, `EXECUTION_LIMIT`, `DEADLINE_EXCEEDED`, `RUN_INTERRUPTED` |
| Availability | `STORAGE_BUSY`, `CAPACITY_EXCEEDED`, `FEATURE_NOT_AVAILABLE`, `INTERNAL_ERROR` |

### Observability

Expose structured metrics for run counts/durations, failure codes, current writers/readers, database/WAL size, free space, query limits and readiness. Audit is append-only through BOS but not tamper-proof against a filesystem/database administrator. Failed-attempt logs can be absent after a sudden crash; the durable run reservation supports recovery diagnosis.

## 29 Packaging, backup and operations

### Release artifact

Publish a self-contained executable for `linux-x64` and `win-x64`; test each independently. Native libraries may extract to a protected directory. Ship version information, checksums, dependency/license inventory, OpenAPI and sample definitions. Native AOT, hot plugins and a 1 MB complete distribution are not assumed. Single-file applications are OS/architecture-specific. [R5]

```text
bos init --data-dir ./bos-data --bootstrap-file ./bootstrap.secret
bos serve --data-dir ./bos-data --listen https://localhost:8080
bos doctor --data-dir ./bos-data
bos backup --data-dir ./bos-data --output ./snapshot.bosbak
bos restore --input ./snapshot.bosbak --data-dir ./restored
bos reset-admin --data-dir ./bos-data --secret-file ./reset.secret
bos version
```

### Configuration and secrets

`bos.json` selects registered storage/parser adapters, listener, workspace timezone, lowered limits and allowed origins. Unknown settings fail startup. Persist Data Protection keys and API/cursor secrets under owner-only permissions; configure encryption at rest for the host volume. Never place credentials in flow source or CLI arguments. CORS is denied unless configured, and proxy headers are trusted only from allowed proxy addresses.

### Backup and restore

Take offline backups with exclusive workspace lock using the database backup API. Bundle a verified database snapshot, schema/version manifest, configuration and required protected keys. Hash every entry, write to a temporary destination and atomically rename only after verification. The backup contains sensitive business and identity data; protect/encrypt it as an operator responsibility.

Restore only into an empty directory while stopped. Verify hashes, SQLite integrity and supported engine version. Invalidate restored sessions/API tokens, regenerate cursor keys, mark abandoned runs interrupted and issue a fresh local admin recovery secret. Replay keys and committed results remain in the snapshot. A restore can lose changes after the snapshot; set pilot RPO to 24 hours with daily backups and test a one-hour RTO on the deployment hardware.

### Upgrade and shutdown

Back up before transactional engine migrations. Refuse a newer database schema. Roll back an incompatible upgrade by restoring the pre-upgrade snapshot. Drain bounded work on shutdown; uncommitted transactions rollback on termination. Do not copy only the live `.db` file while WAL is active.

## 30 Acceptance tests and release evidence

| ID | Required observable test |
| --- | --- |
| A01 | Duplicate JSON/YAML keys, custom tags, aliases and oversize/deep documents fail before dispatch. |
| A02 | Typed values round-trip; decimal overflow, wrong scale, null/missing confusion and invalid dates fail. |
| A03 | Type publication is atomic; immutable versions cannot be edited; forbidden schema changes fail. |
| A04 | HTTP and flow calls enforce the same grants, token scopes and operation/field guards. |
| A05 | A flow updates two records then fails; neither update/history/index change survives. |
| A06 | Two updates with the same revision commit at most one. |
| A07 | Two stock-issue operations cannot both consume the last unit. |
| A08 | Concurrent unique values conflict correctly; live references block deletion, including nested references. |
| A09 | Query numeric order is correct; cursor tampering/filter changes fail; no SQL injection. |
| A10 | Paging/count never silently truncate; reference flow rejects more than 100 lines. |
| A11 | Expression short-circuiting, scope, Missing, pure helpers and loop budgets match the specified semantics. |
| A12 | Output validation or `MISSING_RETURN` rolls back all writes. |
| A13 | Same key/body replays once; changed body conflicts; replay reauthorizes and redacts inaccessible results. |
| A14 | Kill before commit leaves no business mutation; kill after commit preserves success and its receipt. |
| A15 | Startup marks remaining running runs interrupted and never automatically repeats them. |
| A16 | Revocation/disablement before execution denies the run; running transaction uses a consistent snapshot. |
| A17 | Version publication leaves old flows pinned; missing operation contract versions block execution. |
| A18 | Deadline, SQL work and allocation budgets stop work; host remains responsive. |
| A19 | Audit correlates actors/versions/steps without tokens, passwords or default whole-record logs. |
| A20 | Disk-full, storage-busy and uncertain commit faults create no false success or partial state. |
| A21 | Backup restores readable data and receipts; sessions/tokens invalidated; recovery objectives measured. |
| A22 | Clean Windows/Linux hosts run their artifacts without an installed .NET runtime. |
| A23 | Material Request fixture calculates `900.00` and preserves lifecycle guards through all entry points; a line edit makes the prior parent revision stale. |
| A24 | Identity and BOS grants/audit updates share atomic persistence; last admin cannot be removed. |

Run integration tests against real SQLite connections and the actual HTTP/application paths. Use property-based checks for codecs, typed ordering and expression budgets. Fault injection must exercise real transaction boundaries. A mocked happy-path demonstration is insufficient release evidence.

## 31 Ten-agent ownership plan

Planning baseline: ten AI engineering agents with one accountable human reviewer. Agent 1 owns integration; specialist agents own separate modules and must deliver executable tests with their changes. The plan describes a future build; no agents or implementation have been launched by this specification.

| Agent | Owned area | First integration deliverable |
| --- | --- | --- |
| 1 | Contracts and integration | Solution skeleton; frozen DTOs/ports; main branch; final gate report |
| 2 | Values and types | Canonical codecs; schema compiler; draft/publish contracts |
| 3 | Storage and record writes | SQLite unit of work; CRUD; references/uniqueness; record history |
| 4 | Queries | Typed filters/sort; cursors; counts; SQL cancellation/work budgets |
| 5 | Identity and authorization | Identity store; roles/grants; token scopes; operation write guards |
| 6 | YAML compiler | Restricted parser; typed AST; publication validator; pure helpers |
| 7 | Executor and receipts | Atomic flow runner; budgets; run state; idempotency/recovery |
| 8 | HTTP and operation registry | Method dispatch; descriptors; OpenAPI; auth integration routes |
| 9 | Independent verification | Black-box contract tests; rollback/race/crash fixtures; defect reports |
| 10 | Packaging and reference app | CLI; backup/restore; Material Request operations; clean-host packaging |

### Ownership rules

Each agent edits only its module and tests after contract freeze. Shared contract changes go through Agent 1, who updates consumers and tests in the same integration change. Agents 3, 5 and 7 agree on one transaction owner before coding Identity writes, receipt persistence or run completion. No repository adapter commits independently.

### Working arrangement

Use isolated branches/worktrees. Merge small compiling changes at the checkpoints in Section 32. Keep main runnable with seeded fixtures. Specialists may use temporary fakes in their isolated tests; no fake persistence, permissive authorization or stubbed business behavior may pass the integrated gates.

### Human review

The reviewer confirms scope and reviews the transaction/security boundaries and test evidence. AI review complements automated tests. Agent count does not multiply throughput linearly; when a shared dependency blocks an agent, it prepares tests or documentation against the frozen contract instead of inventing a competing interface.

## 32 One-day integration schedule

Use a focused eight-hour build day as the first measurement. Times below are elapsed engineering-session checkpoints, not promised completion times. Start with a clean repository, available .NET SDK, SQLite dependencies and an agreed test runner. The release scope is not reduced merely because the day ends.

| Elapsed | Integration target | Evidence required |
| --- | --- | --- |
| 0–1 hour | Freeze contracts and bootstrap solution | Build/test commands run; DTOs, UoW, auth context and AST agreed |
| 1–3 hours | First data path | Create type; create/get/update/delete record; query; stale update rejected |
| 3–5 hours | First automation path | Parse/publish YAML; call a BOS method; return a typed result |
| 5–6 hours | Atomic and permission path | Two-record rollback; denied mutation; lifecycle bypass rejected |
| 6–7 hours | Restart and duplicate path | Committed data survives restart; replay cannot duplicate writes |
| 7–8 hours | Reference run and build report | Material Request flow; artifact runs; test matrix and remaining defects |

### Minimum day-one demonstration

The executable must define a type, save/query records, run a published YAML flow, rollback a failed grouped change, deny an unauthorized call, reject a stale revision and retain committed data after restart. Use the actual HTTP API, SQLite and executor. Screenshots or mocked tests do not substitute for these behaviors.

### Status at the end of the day

Report one of: **demo achieved**; **integrated release candidate with named failing gates**; or **1.0 release with every release gate passed**. Preserve the same scope in all cases. If only the demonstration passes, deliver that build with its limitations and estimate the remaining work from the actual failures.

### Dependency fallback

If a dependency cannot be integrated, keep the affected feature blocked rather than silently replacing it with a weaker guarantee. If packaging on the second OS is unavailable, label that target unverified. Document exact build commands, source revision, test outputs and artifact checksums so the next session continues from evidence.

### After day one

Prioritize failures that affect authorization, atomicity, receipt correctness or durable state before convenience methods and performance. Re-estimate after integration rather than assigning a fixed multi-week duration in advance. No production rollout is part of this one-day build target.

## 33 Shared DTO and invocation contract

### Common transport values

```text
MethodRequest = JSON object of exactly the declared arguments
MethodResponse<T> = { result: T, meta: { traceId: UUID } }
ErrorResponse = { error: { code, message, details, traceId, retryable } }
ValidationReport = { valid: Boolean, errors: FieldError[] }
FieldError = { path: String, code: String, message: String }
Page<T> = { items: T[], nextCursor: String | Null }
TombstoneReceipt = { id: UUID, type: String, revision: Int32, deleted: true }
ExecutionContext = {
  principalId, credentialId, commandId, runId?, startedAt,
  effectiveGrants, guardOwner?, budget
}
```

### Binding and defaults

Reject unknown arguments. Optional query defaults to an empty bounded query; limit defaults to 50. Optional published version selects active unless the catalogue explicitly requires a version. A supplied null is not omission. `expectedRevision` is a positive Int32 metadata value in public record/administrative calls; business integer inputs to flows remain canonical strings. Internally both bind to the Integer value kind with their own range checks.

### Nested call behavior

The root invocation alone owns the idempotency key, receipt, transaction and command ID. Internal operation calls receive that ExecutionContext and unit of work; they do not reserve separate receipts or demand user-supplied keys. They add step/operation audit details under the same root command. Any attempt to replace identity, transaction or `guardOwner` from arguments fails.

### Administrative revisions

Users and roles expose a positive revision in every administrative get/list result. Identity framework concurrency stamps remain implementation details. Update grant lists and membership with compare-and-swap against the specified revision inside the shared transaction. Flow control revision is distinct from `draftRevision` and published version and is returned by get/list.

### Canonical paths

Validation paths use dot-separated fields and zero-based bracketed array indices, for example `fields.quantity.scale` and `input.lines[0].quantity`. Flow errors additionally identify `stepId` and `iterationPath`, for example `priceLines[2]/priceLine`. A path is diagnostic data; the interpreter never evaluates an error path as code.

## 34 Publication and operation metadata

### Type and flow envelopes

```text
TypeDraft = { name, draftRevision, definition, updatedAt, updatedBy }
PublishedType = {
  name, version, hash, definition, publishedAt, publishedBy
}
FlowDraft = { name, draftRevision, source, updatedAt, updatedBy }
PublishedFlow = {
  name, version, hash, sourceHash, astHash, format,
  inputSchema, outputSchema, dependencies, capabilities,
  publishedAt, publishedBy, controlRevision, disabled, source?
}
CompileReport = { valid, errors: FieldError[], warnings: FieldError[], dependencies }
RunReceipt = {
  runId, flowName, flowVersion, hash, principalId, status,
  startedAt, completedAt?, inputHash, readTypes,
  changedRecordIds, result?, error?, resultRedacted
}
```

### Definition argument shape

`types.create`/`update`/`validate` takes the TypeDefinition JSON object in Section 04. `flows.create`/`update`/`validate` takes `definition: {source: string}`; the source is one BOS YAML document. `name` inside the source must match the route method argument when supplied. Original text is preserved for editing. Active published get omits source unless `flows.manage` is granted; getDraft returns it to managers.

### Published source and hashes

Hash UTF-8 source bytes with SHA-256 and separately hash canonical AST JSON. Runtime uses the compiled AST and validates its recorded format/hash, not a fresh interpretation of editable draft text. Whitespace changes can alter `sourceHash` while leaving `astHash` unchanged; publishing semantic duplicates returns `NO_CHANGE`. `hash` is an alias of `astHash`. Published content and publication metadata are immutable. The `disabled` and `controlRevision` fields are current control-state projections and may change without changing a published version.

### Operation descriptor additions

Every descriptor includes `name`, `contractVersion`, `inputSchema`, `outputSchema`, `mode`, `flowCallable`, `requiredGrants` and `capabilities`. Built-in record methods require type grants resolved from their `type` argument. Custom methods require `operations.execute` plus their record capabilities. Discovery must not leak administrative method details to unprivileged callers.

### Input and output schemas

Flow and operation data schemas use the same field/value grammar as records, with persistence-only properties such as `indexed`, `unique`, `accessMode` and `managedBy` forbidden. Referenced record IDs are strings validated by the receiving method; record references are not implicitly dereferenced during input binding. Output must be serializable without Missing values, cycles or oversize collections.

### Business operations stay explicit

The sample `CreateRequest`, `ReviseRequest`, `RepriceRequestLine`, `FinalizeRequest` and `ApproveRequest` operations are application modules registered by trusted code. Their APIs and schemas are discoverable through the registry. Adding a new kind of trusted operation requires a code deployment; composing existing operations into a new YAML flow does not.

## 35 Compatibility and release report

### Version identifiers

The product contract is BOS Core 1.0; this document revision is 1.0.0; the YAML format is `bos.flow/1.0`; HTTP routes remain `/v1`. These identifiers are independent of published type/flow versions and operation contract versions. A new record or flow version does not imply a new server release.

### Compatibility rule

Existing published artifacts keep exact pinned interpretation. The host must reject unknown AST formats and unavailable operation contract versions. Within HTTP v1, additive optional response fields may be added; callers must tolerate them. Existing request meanings and required fields cannot change silently. A semantic change requires a new method/format contract or an explicitly versioned migration.

### Dependency lock

Before the first integration merge, commit the selected .NET SDK version, all package versions, restore lockfiles and supported target RIDs. Record the SQLite engine version at runtime. Do not use floating versions or claim reproducible packaging without saving the build manifest. The selected libraries must satisfy these contracts; this spec does not pre-approve an untested package version.

### Release evidence

| Item | Required report content |
| --- | --- |
| Build identity | Source revision, SDK/package lock, UTC build time and platform |
| Artifacts | Executable paths, SHA-256, compressed/uncompressed size and installed footprint |
| Functional evidence | A01–A24 status, test names, output and failure reproductions |
| Concurrency and crash | Real separate connections/process termination at commit boundaries |
| Operational evidence | Verified restore, revoked credentials, clean-host startup and known limits |
| Decision | Demo / release candidate / 1.0 release; named unpassed gates and next actions |

### Release gate

All initial methods in Sections 18–22, the mandatory record/flow semantics, the reference scenario and A01–A24 must pass before declaring the complete 1.0 release. Reserved later methods stay disabled. If there are unresolved correctness failures, ship only a labeled review build. A short schedule does not turn an untested requirement into a passed one.

### Scope change control

If a required feature is deliberately postponed, revise this contract and record the decision instead of leaving a stub behind a successful response. The authenticated capabilities endpoint is the authoritative list of what the running artifact implements.

## 36 Catalogue queries and result shapes

### List query contract

```text
CatalogueQuery = {
  where?: [{field, op: "eq", value}], limit?: Int32, cursor?: String | Null
}
RecordCountFilter = { where?: [RecordComparison] }
TypeSummary = { name, activeVersion, hash }
TypeVersionSummary = { name, version, hash, publishedAt }
FlowSummary = { name, activeVersion, disabled, controlRevision }
FlowVersionSummary = { name, version, hash, publishedAt }
RunSummary = {
  runId, flowName, flowVersion, principalId, status, startedAt, completedAt?
}
```

CatalogueQuery applies to the list methods below; `records.list` retains the richer Query in Section 07. All catalogue filters are ANDed; at most 10 comparisons; limit defaults to 50 and caps at 200. Omitted `where` means no filter. Unknown fields/operators fail. Each list uses the same authenticated-cursor binding rules as record queries. Visibility is applied before paging.

| Method family | Allowed equality filters | Fixed order |
| --- | --- | --- |
| `types.list` | name; status=published | name ascending |
| `flows.list` | name; disabled Boolean | name ascending |
| `types.listVersions` / `flows.listVersions` | none; parent name is required | version ascending |
| `operations.list` | name; mode; flowCallable | name then contractVersion |
| `flows.listRuns` | flowName; flowVersion; status; principalId | startedAt descending then runId |
| `flows.getRunSteps` | stepId; outcome | ordinal ascending |
| `users.list` | userName; kind; active | userName then id |
| `roles.list` | name | name then id |
| `tokens.list` | revoked Boolean; principalId is required | createdAt descending then id |
| `audit.list` | actor; method; commandId; runId; type; objectId | sequence ascending |

### Discovery visibility

Type/flow lists include published names only; drafts are accessed by exact getDraft name under management permission. `activeVersion` is a positive integer. A disabled flow stays visible to authorized readers. Run lists default to the current principal; requesting another principal requires `runs.readAll`. Operation lists contain only executable versions the caller may discover. Cursor continuation never restores an expired permission.

### Count and health

`records.count` takes `filter: RecordCountFilter`; it rejects sort, select, limit and cursor. `system.getHealth` returns `{status:ready|notReady,checks:[{name,status}],metrics:{activeReaders,activeWriters,databaseBytes,walBytes}}` to `system.manage` callers. Public health routes expose only status; capability discovery requires authentication and advertises only callable methods with transport, mode, grants and schemas.

## 37 Identity payloads and authentication routes

```text
Grant = { action: String, resource: String }
UserCreate = { userName, displayName, kind: "human" | "service" }
UserSummary = { id, userName, displayName, kind, active, roles: UUID[], revision }
UserChanges = { displayName?, active? }
RoleCreate = { name, grants: Grant[] }
Role = { id, name, grants: Grant[], revision }
RoleChanges = { grants: Grant[] }
TokenMetadata = {
  id, principalId, scopes: Grant[], createdAt, expiresAt, revokedAt?
}
TokenIssue = {
  token: String | Null, metadata: TokenMetadata,
  replayCode?: "SECRET_ALREADY_DISCLOSED"
}
```

### Account and role binding

`userName` and role name use the name grammar in Section 04 and are unique under invariant case-insensitive comparison. `displayName` is NFC text, 1–200 characters. User kind and userName are immutable. `users.create` makes an inactive account with no roles. `users.update` requires a nonempty change object; activating a human requires a password set. Role creation permits an empty grant list; role updates replace grants only. Duplicate grants collapse; unknown actions fail. Users/roles start at revision 1; password or membership changes increment user revision. Empty/no-op changes return `NO_CHANGE`.

### Token binding

`tokens.create` accepts an active service principal, an explicit nonempty Grant array and optional `expiresAt`; omitted expiry is 30 days from issuance, maximum 365. Effective scopes must remain a subset of target-principal grants and issuer authority. Token wire form is `bos_<lowercase-UUID>.<base64url-256-bit-secret>`, without padding. Store only the random-secret hash and metadata. Revocation returns TokenMetadata; repeated revocation returns unchanged metadata. `tokens.list` never returns secret material.

| Route | Request and result |
| --- | --- |
| `GET /v1/auth/antiforgery` | No body; sets antiforgery cookie, returns `{requestToken,headerName}` |
| `POST /v1/auth/login` | `{userName,password}`; antiforgery header; human accounts only; session cookie plus UserSummary |
| `POST /v1/auth/logout` | `{}`; session and antiforgery; clears current cookie; 204 |
| `POST /v1/auth/change-password` | `{currentPassword,newPassword}`; session and antiforgery; invalidates sessions; 204 |

### Secret command receipts

Administrative `users.setPassword` accepts 12–128 characters and uses Identity hashing, never a reversible password store. It returns `{userId,revision,passwordChanged:true}`; it does not activate an account implicitly. Its key binds nonsecret arguments only; the password is excluded from receipt hashing/storage. Reusing that key returns the saved result without changing a password, even if a different secret is supplied. A new change requires a new key. Token issuance replay returns `{token:null,metadata:TokenMetadata,replayCode:SECRET_ALREADY_DISCLOSED}` in the normal result envelope and no secret; revoke and issue under a new key if the first response is lost.

### Cookie and response handling

Use HttpOnly, Secure and SameSite=Lax session cookies. Login returns the normal result envelope; logout/password-change return no body. Authentication errors use the common error envelope without disclosing whether a user exists. Password reset through the offline CLI has no network receipt. No identity or authentication route is flow-callable.

## 38 Reference business operation contracts

### Registered contracts at version one

```text
LineInput = {
  item: String, quantity: Decimal(12,3), unitPrice: Decimal(18,2)
}
CreateRequestInput = {
  title: String, currency: "LKR" | "USD", lines: LineInput[]
}
ReviseRequestInput = {
  id: UUID, expectedRevision: Int32, title?: String, lineChanges?: LineChange[]
}
LineChange = {
  id: UUID, expectedRevision: Int32, item?: String,
  quantity?: Decimal(12,3), unitPrice?: Decimal(18,2)
}
RequestResult = { request: Record, lines: Record[] }
```

All five methods are `mode=write` and `flowCallable=true`. Public callers use `operations.call` with name, `contractVersion:1` and input. Metadata revisions are JSON numbers; decimals use canonical strings. Each descriptor requires `operations.execute` for its own name and the exact type capabilities below. All writes use the root transaction.

| Operation | Input and output | Required type capabilities |
| --- | --- | --- |
| `CreateRequest` | CreateRequestInput → RequestResult | read/create both types |
| `ReviseRequest` | ReviseRequestInput → RequestResult | read/update both types |
| `RepriceRequestLine` | `{id,expectedRevision}` → line Record | read both; update line |
| `FinalizeRequest` | `{id,expectedRevision}` → parent Record | read both; update parent |
| `ApproveRequest` | `{id,expectedRevision}` → parent Record | read/update parent |

### Create and revise

`CreateRequest` requires 1–100 lines, creates a Draft parent and lines with zero total/amounts, and returns both. `ReviseRequest` requires a Draft parent with the specified revision; it supports title changes and edits to existing lines only. A line change must belong to that parent, supply its current revision and change at least one allowed field. It may not change `requestId`, `amount`, `status`, `contentVersion` or `currency`. Duplicate line IDs fail.

A title or line edit resets affected amounts and the parent total to `0.00`; it increments the managed `contentVersion` counter once and therefore the parent revision, even when only a line changed, so stale parent decisions conflict. `contentVersion` is a business Integer, separate from record metadata revision. A wholly unchanged request returns `NO_CHANGE`. Adding/removing lines after creation is deferred.

### Submit and approve

`RepriceRequestLine` and `FinalizeRequest` follow Section 26. `FinalizeRequest` additionally requires at least one line; it checks the full page bound, rejects arithmetic mismatch, and changes Draft to Submitted. `ApproveRequest` requires Submitted and the supplied revision, changes only status to Approved and returns the parent. Further edit, submission or approval attempts fail `INVALID_STATE`. There is no rejection/reopening path in 1.0.

### Fixture and combined limits

The normative release fixture has two lines. Input limits remain subject to aggregate SDK-call, allocation and time budgets; a 100-line bound is not a performance promise. Business implementations use the same authorization and write-guard services as generic record methods. No test-only bypass is shipped in a release artifact.

## 39 Sources and specification precedence

### Primary technical sources

- **R1 — Microsoft Learn, Introduction to Identity on ASP.NET Core.** Identity provides user, password, role and claim primitives; BOS resource authorization still requires integration. <https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity>
- **R2 — Microsoft Learn, Resource-based authorization.** Resource decisions require imperative application checks after resource resolution. <https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based>
- **R3 — SQLite, Isolation in SQLite and transaction documentation.** Supports the single-writer and `BEGIN IMMEDIATE` design. <https://www.sqlite.org/isolation.html> and <https://www.sqlite.org/lang_transaction.html>
- **R4 — YamlDotNet project and author documentation.** Provides YAML parsing for .NET; the restricted BOS grammar and execution semantics are our application design. <https://github.com/aaubry/YamlDotNet> and <https://aaubry.net/pages/yamldotnet.html>
- **R5 — Microsoft Learn, Single-file deployment.** Documents self-contained packaging, platform-specific builds and native extraction considerations. <https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview>
- **R6 — SQLite, progress handler documentation.** Supports bounded database work and interruption through a progress callback; runtime budgets and thresholds are BOS design choices. <https://www.sqlite.org/c3ref/progress_handler.html>

Sources checked 3 November 2026, as recorded in the supplied specification. They support implementation mechanisms; they do not establish that BOS has been implemented or that its acceptance tests pass. Product rules, budgets, method contracts and examples in this document are proposed BOS requirements.

### Precedence

This 1.0 document supersedes the v0.1 specification where behavior differs; it preserves the established internal-release scope. In particular, transaction is a whole-run manifest setting, `operationsOnly` types require named trusted operation boundaries, mutable accumulators and nested flows are deferred, and durable asynchronous jobs are outside this internal pilot.

### Changes after implementation starts

Change observable behavior only through a numbered specification revision. Update schemas, examples, capability descriptors and conformance tests together. The initial methods are the release commitment; reserved methods require their own completed contract before activation.

### Review checklist

A reviewer should be able to identify the schema/version used by every record, the identity and capability set of every run, the transaction containing every mutation, the receipt resolving a lost response, and the behavior of every failure path. If any of those are unclear in an implementation, it is not ready for the pilot.
