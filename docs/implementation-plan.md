# Documentation-first implementation plan

**Baseline:** [BOS Core 1.0.0](specification-1.0.0.md), Sections 31–35.\
**Status:** first implementation integrated as an API review build; certification remains open.\
**Release scope:** every initial method, mandatory semantic rule, Material Request scenario and A01–A24.

## 1. Delivery approach

Establish the normative documentation and test obligations first, freeze the shared implementation contracts, then integrate a runnable vertical path. Expand that path until the full required surface and acceptance gates pass. The eight-hour schedule is a measurement of the first integrated build, not a reduction in scope or a release promise.

Repository observations at the start of documentation work:

- The workspace was empty and had no Git repository initialized.
- The available SDK reported .NET SDK `10.0.111` and .NET / ASP.NET Core runtime `10.0.11` on Linux x64.
- No solution, dependency lock, executable, package restore or software test result exists yet.
- Available tooling is an observation, not a committed SDK choice or a clean-host verification result.

## 2. Checkpoint D — documentation baseline

Deliver these documents before runtime implementation:

- Complete 1.0.0 specification with all 39 sections and combined reference flow.
- Architecture and transaction/security invariants.
- This implementation sequence and ten-agent ownership map.
- Decision register for material ambiguities and unverified implementation choices.
- A01–A24 tracker and release-report template with no inferred passes.
- Standalone reference schemas/flow under [examples](examples/README.md).

Exit evidence: internal links resolve; all acceptance IDs and numbered sections are present; JSON examples parse; standalone schema/flow examples agree with the specification. This checks documentation consistency only.

## 3. Checkpoint 0 — contracts and toolchain freeze

**Owner:** Agent 1, with Agents 3, 5 and 7 agreeing transaction ownership.

1. Establish source control and isolated branches/worktrees before parallel implementation, with repository changes authorized through the normal workflow.
2. Select a supported .NET LTS SDK and exact compatible package versions for SQLite, Identity integration, YamlDotNet and testing. Verify availability rather than using floating or assumed versions.
3. Save SDK selection, target RIDs (`linux-x64`, `win-x64`), package versions, restore lockfiles and dependency/license manifest before the first integration merge.
4. Bootstrap the eight specification modules and a build/test entry point.
5. Freeze immutable value kinds, codecs, schema structures, DTOs, errors and canonicalization responsibilities.
6. Freeze unit-of-work and repository signatures; demonstrate how Identity enlists in the same transaction as BOS grants/audit/receipts.
7. Freeze the method/operation descriptor registry, dispatcher, authorization/capability context, AST and budget contract.
8. Resolve decision-register questions that block those interfaces. Shared changes must update consumers and contract tests together.

Exit evidence: solution builds, test runner executes, a real SQLite transaction can commit and rollback, frozen ports compile across module boundaries, and dependency versions/lockfiles are recorded. Commands are documented once the actual solution and runner exist.

## 4. Ten-agent ownership and handoffs

This is the planned organization from the supplied specification; it is not a record of agents launched.

| Agent | Owns | Required handoff | Integration obligations |
| --- | --- | --- | --- |
| 1 | Contracts and integration | Solution, frozen contracts/ports, integration branch and evidence report | Review every shared contract change; keep the main path runnable. |
| 2 | Values and types | Strict codecs, canonical JSON, schema compiler/evolution, drafts/publication | Shared comparator/codec behavior for queries, indexes, expressions and hashes; A01–A03, A17 support. |
| 3 | Storage and record writes | SQLite UoW, fixed schema, CRUD, references/unique/index/history/audit persistence | One transaction owner; no independent commits; A05–A08, A14, A20 support. |
| 4 | Queries | Typed filters, keyset paging, cursors, exact count and SQLite work/deadline enforcement | Match core ordering; bounded materialization; A09, A10, A18. |
| 5 | Identity and authorization | Identity stores, users/roles/grants, sessions/tokens, live authorization and guards | Same transaction as Agent 3/7 state; last-admin and secret-receipt behavior; A04, A13, A16, A19, A24. |
| 6 | YAML compiler | Event-safe parser, closed AST, publication validation, typed pure helpers | No arbitrary CLR deserialization; lexical scope/dependency/budget contract; A01, A11, A17, A18. |
| 7 | Executor and receipts | Atomic runner, shared budgets, idempotency/replay, run state, interruption/recovery | Same UoW as records/Identity; success coupled to commit; A05, A12–A16, A18, A20. |
| 8 | HTTP and operation registry | RPC dispatch/descriptors, OpenAPI, error mapping, auth/antiforgery routes | One application path for HTTP and flows; strict arguments and discovery; A01, A04, A13, A19. |
| 9 | Independent verification | Black-box acceptance suite, real races/crashes/faults and defect reproductions | Independent evidence; no fake integrated persistence or permissive authorization. |
| 10 | Packaging and reference app | Operator CLI, backup/restore, Material Request operations and per-RID artifacts | No offline action against running workspace; A21–A23 and packaging evidence. |

### Working rules

- Use isolated branches/worktrees once source control is established.
- Each specialist edits its owned module and tests after contract freeze.
- Agent 1 coordinates shared contract changes and consumer updates.
- Keep merges small, compiling and accompanied by executable tests.
- A blocked specialist prepares tests/docs against the frozen contract instead of creating a competing interface.
- Temporary isolated fakes are allowed for unit tests; integrated gates require actual adapters and authorization.
- One accountable human reviewer reviews scope, transaction/security boundaries and release evidence.

## 5. Checkpoint 1 — first durable data path

**Primary owners:** Agents 2, 3, 4, 5 and 8.

Implementation sequence:

1. Strict JSON/value binding, canonical serialization, literal defaults and complete schema validation.
2. Atomic type draft/update/publication, retained draft revisions and safe evolution.
3. Current principal/grant resolution and application permission checks.
4. Record create/get/update/delete with expected revisions, shallow patches and no-op semantics.
5. Reference/unique/index maintenance, full revision history and append-only mutation audit.
6. Typed bounded list/count, permitted indexed fields, stable sort, projection and authenticated keyset cursors.
7. Direct-command receipts in the same transaction, including reauthorized replay.
8. Actual HTTP mappings for this path with strict errors and request limits.

Exit evidence:

- Define/publish a type and create/read/query/update/delete a record through HTTP.
- Show old published definitions remain immutable and old records keep their typeVersion.
- Reject stale updates, duplicate unique values, inaccessible references and forbidden writes.
- Show commit persistence across restart and rollback agreement among state/history/indexes.
- Record A01–A04, A06, A08–A10 and A13 coverage; partial coverage is labeled partial.

## 6. Checkpoint 2 — first automation path

**Primary owners:** Agents 6, 7 and 8, with type/operation metadata supplied by Agents 1/2/5.

1. Parse the restricted YAML event profile with byte/depth limits before full materialization.
2. Compile closed expression/instruction nodes, unique step IDs and lexical scopes.
3. Resolve all pinned dependencies, input/output schemas and exact capability ceilings.
4. Implement the full helper vocabulary with exact values, Missing rules and short-circuit behavior.
5. Store original source and immutable canonical AST with sourceHash/astHash and versioned interpretation.
6. Interpret `let`, `call`, `if`, `forEach`, `require`, `log`, `return` sequentially with shared budgets.
7. Dispatch calls through the application registry and current UoW; never expose repositories to the interpreter.
8. Publish and run a small query/command flow through the real API.

Exit evidence: one published flow invokes a registered BOS operation and returns validated typed output; prohibited YAML and dependencies fail publication; query flow cannot mutate; output/return/scope/helper behavior has executable tests. Track A01, A11, A12, A17 and A18.

## 7. Checkpoint 3 — atomicity and permission boundary

**Primary owners:** Agents 3, 5, 7 and 9.

1. Persist a durable running reservation, then execute the command flow in one `BEGIN IMMEDIATE` UoW.
2. Recheck principal/token/grants/flow control at execution transaction entry.
3. Enlist all nested reads/writes, Identity changes, indexes, history, audit and success receipt under the root owner.
4. Poison the transaction on every nested failure, including output invalidity and host exceptions caught above the operation.
5. Persist failed run state separately after rollback and sanitize diagnostics.
6. Enforce cumulative capabilities, nearest business-operation guard ownership and managed-field rules.
7. Race separate connections for stale revisions, uniqueness and a last-unit business invariant.

Exit evidence: two staged updates followed by failure leave no record/history/index changes; direct generic calls cannot bypass operation-only types; only one stale-revision/last-unit competitor succeeds. Track A04–A08, A12, A16, A20 and A24.

## 8. Checkpoint 4 — duplicate, crash and recovery path

**Primary owners:** Agents 7, 9 and 10.

1. Test same-key/same-body replay, conflicting bodies, durable running reservations and failed/interrupted keys.
2. Reauthorize receipt/run reads, including redaction after loss of type/flow permissions.
3. Exercise one-time token disclosure and nonsecret password receipt hashing.
4. Kill actual child processes before and after commit; inspect state/receipts through a fresh connection/process.
5. Recover under the exclusive workspace lock; mark abandoned runs interrupted without running them again.
6. Inject disk-full/storage-busy and uncertain-commit failures at actual transaction boundaries.
7. Verify rate/concurrency admission, query work/deadline limits, cancellation and shutdown drain behavior.

Exit evidence: committed data and success survive restart, pre-commit mutations do not, replay never duplicates accepted work, unknown outcomes are resolved from durable state and host responsiveness survives bounded failures. Track A13–A20.

## 9. Checkpoint 5 — complete catalogue and reference application

**Primary owners:** Agents 5, 8 and 10; independently checked by Agent 9.

1. Complete every initial type, record, operation, flow, identity, authorization, audit and system method in Sections 18–22.
2. Apply Section 36 catalogue filtering/ordering/cursors and visibility before paging.
3. Complete authentication/antiforgery routes and Identity lockout/stamp/token/last-admin rules.
4. Ensure each enabled method is represented accurately in capabilities and OpenAPI; reserve Later methods without advertising them.
5. Register all five Material Request operations with explicit descriptors, versions and capability ceilings.
6. Publish the parent schema, line schema and reference flow; install explicit Requester/Approver/Admin grants through legitimate administration paths.
7. Verify `900.00` submission, repricing overflow rollback, stale-parent rollback, line-edit revision invalidation and guarded approval.
8. Ensure production artifacts expose no fault-injection controls.

Exit evidence: method/descriptor/OpenAPI coverage inventory is complete, reference fixture passes through actual HTTP/application/executor paths and no management path bypasses live guards. Track A04, A10, A13, A19, A23 and A24 plus catalogue completeness.

## 10. Checkpoint 6 — operations and distribution

**Primary owners:** Agent 10 with Agent 9 verification.

1. Complete `init`, `serve`, `doctor`, `backup`, `restore`, `reset-admin`, `version` and strict `bos.json` binding.
2. Protect secrets/Data Protection keys; enforce offline/exclusive-lock requirements and permitted listeners/proxies/origins.
3. Verify offline database-backup snapshots, entry hashes, temporary-write/atomic-rename behavior and empty-target restore.
4. Verify restored data and receipts, invalidated sessions/API tokens, regenerated cursors and local admin recovery.
5. Measure daily-backup RPO and restore RTO on deployment hardware.
6. Publish self-contained `linux-x64` and `win-x64` artifacts with OpenAPI, version, samples, hashes and dependency/license inventory.
7. Run each artifact on an appropriate clean host without a .NET runtime installed.
8. Measure assembly size, compressed/uncompressed distribution and installed footprint independently.

Exit evidence: A21 and A22 with platform-specific evidence. A cross-publish alone is not clean-host execution. Document unavailable targets as unverified.

## 11. First-day measurement

| Elapsed engineering time | Integration target | Required observation |
| --- | --- | --- |
| 0–1 hour | Contracts/toolchain | Build/test commands execute; UoW/auth/AST contracts agreed. |
| 1–3 hours | Data path | Type and CRUD/query path work; stale update fails. |
| 3–5 hours | Automation | Published YAML calls a registered method and returns typed output. |
| 5–6 hours | Atomic/permission path | Two-record rollback and lifecycle bypass denial are observed. |
| 6–7 hours | Restart/duplicates | Durable state and nonduplicating replay are observed. |
| 7–8 hours | Reference/artifact report | Reference scenario, executable evidence and named failing gates are recorded. |

Record actual elapsed time and failure evidence. If dependencies or platform access block a feature, keep it blocked and visible; do not substitute weaker persistence, authorization or recovery semantics.

## 12. Final integration gate

Use the [acceptance tracker](acceptance.md) and [release report](release-report-template.md).

- Every initial method and helper is implemented and accurately discoverable under caller permissions.
- A01–A24 have passing evidence, including real SQLite races, real process termination and both clean-host targets.
- The reference fixture and all lifecycle entry points pass.
- Locked toolchain/dependencies, source identity, exact commands and outputs are captured.
- Artifacts and backup/restore results are measured and reproducible from the recorded inputs.
- Human review has covered transaction ownership, authorization and outcome recovery.

Report **demo achieved**, **integrated release candidate with named failing gates**, or **1.0 release** according to the evidence. If implementation has not reached the minimum demonstration, report that directly. Prioritize authorization, atomicity, receipts and durable-state defects before convenience/performance work.
