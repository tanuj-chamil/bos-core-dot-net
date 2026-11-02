# Decision register

**Authority:** [Specification 1.0.0](specification-1.0.0.md).\
**Status date:** 3 November 2026.

This register distinguishes product decisions already frozen by the specification from implementation choices that need verification and contract ambiguities that need resolution. An unresolved item does not change the release contract. Resolve affected items before freezing their C# interfaces or building their behavior.

## 1. Frozen product decisions

| ID | Decision | Source |
| --- | --- | --- |
| D01 | Single tenant, one process per data directory, local SQLite and exclusive OS workspace lock. | §§02, 16, 29 |
| D02 | ASP.NET Core on supported .NET LTS; headless HTTP JSON and matching internal SDK. | Purpose; §§02, 17 |
| D03 | One application command boundary for HTTP, trusted operations and YAML calls. | §§02, 10, 17, 33 |
| D04 | Versioned immutable definitions; retained mutable drafts; exact operation/type dependencies. | §§05, 18, 20, 34 |
| D05 | One root transaction owner; atomic command flow; query flow read snapshot; no independent repository/Identity commits. | §§14, 16, 33 |
| D06 | Restricted structured YAML/expressions, trusted registry only, no arbitrary user code or external effects. | §§10–13, 23 |
| D07 | Identity local human accounts and roles; fixed HTTPS cookie sessions; scoped opaque API tokens for active service principals. | §§08, 37 |
| D08 | Dynamic role grants, deny by default, no row/field policy layer in 1.0; capabilities are ceilings. | §§08–09 |
| D09 | Operation-only types and nearest-operation guard ownership protect business lifecycles. | §§04, 09, 38 |
| D10 | Expected revisions for every update/delete; checked exact arithmetic and explicit precision-discarding rounding. | §§03, 06, 14 |
| D11 | Durable run reservations; atomic success receipts; no automatic execution retries or background queue. | §§14–15 |
| D12 | Offline verified backup/restore; restored credentials invalidated; daily backups / 24-hour RPO and measured one-hour RTO. | §29 |
| D13 | Both `linux-x64` and `win-x64` self-contained artifacts require independent clean-host verification. | §§29–30, 35 |
| D14 | All initial methods/helpers and A01–A24 are release requirements; Later methods stay unavailable. | §§01, 18–23, 30, 35 |
| D15 | Eight-hour first integration is a measurement target; artifact-size preference is measured, not guaranteed. | §§01, 32 |

## 2. Implementation choices to verify

These are engineering selections within the existing contract. No dependency version or design option below is represented as selected, restored or conformant yet.

| ID | Choice | Required proof / owner | Status |
| --- | --- | --- | --- |
| E01 | Exact supported LTS SDK and target framework | Agent 1: pin SDK, confirm support and build both RIDs. Local SDK observation is `10.0.111`. | Open |
| E02 | SQLite provider and native engine package versions | Agents 3/4: restore lock, runtime engine version, backup/interrupt APIs and A05–A10/A18/A20 behavior. | Open |
| E03 | Identity SQLite store and shared transaction enlistment | Agents 3/5/7: one real transaction spans Identity/grants/audit/receipts; rollback and last-admin races. | Open |
| E04 | YamlDotNet version and event validation adapter | Agent 6: prohibited syntax rejected before expansion/materialization, bounded depth/bytes and exact scalar rules. | Open |
| E05 | Typed numeric index encoding/comparator | Agents 2/3/4: exact numeric order, canonical uniqueness, Missing/Null rank and indexed query access. | Open |
| E06 | Exact decimal intermediate representation | Agents 2/6/10: scaled BigInteger or equivalent exact behavior; no implicit decimal rounding. | Open |
| E07 | Cursor encryption/authentication format and workspace key persistence | Agents 4/10: opaque authenticated keyset tokens, complete context binding, tamper checks and key rotation on restore. | Open |
| E08 | Test runner, property-based framework and fault/crash harness | Agent 9: real HTTP/SQLite tests, independent connections/processes and isolated test-only injection. | Open |
| E09 | Cross-platform workspace lock and owner-only secret protection | Agent 10: second-owner rejection, stopped-host operations and Linux/Windows permission evidence. | Open |
| E10 | Packaging flags, native extraction and dependency/license inventory | Agent 10: independently tested self-contained artifacts and measured footprints. | Open |
| E11 | Human session revocation enforcement | Agents 5/8: inactive/stamp-invalid sessions cannot retain access beyond the specified request/transaction boundary. | Open |
| E12 | Windows availability of configured timezone IDs | Agents 1/10: `Asia/Colombo` and explicit timezone behavior on both supported targets; unknown IDs fail. | Open |

Select the first architecture-fitting standard/runtime/dependency mechanism that satisfies the required semantics. Record material tradeoffs when a custom implementation or extra dependency is necessary. No single-implementation extension framework is required.

## 3. Contract clarifications needed

### Q01 — Flow create-version selection

**Sources:** §§05, 11, 18, 34.

The manifest example uses `dependencies.types: {TypeName: [1]}`. The prose permits multiple exact versions for reads and requires a specific version for creates, but does not define how a manifest distinguishes the create version when the accepted read-version set contains multiple entries.

**Decision needed:** an unambiguous create-version representation/rule and its compiled AST form. Do not infer `latest`, choose a version implicitly, or expose obsolete-version selection to ordinary record clients.

**Affected owners:** Agents 1, 2, 6, 7. The single-version reference flow does not exercise this ambiguity.

### Q02 — Material Request `NO_CHANGE` handling

**Sources:** §§06, 26, 38.

Section 26 explicitly lets `RepriceRequestLine` return the current line when its calculated amount is unchanged, without calling generic update. Generic identical normalized updates return `NO_CHANGE`. `ReviseRequest` requires at least one allowed field per line change and a wholly unchanged request returns `NO_CHANGE`, but mixed changed/unchanged line entries and the extent of a title-only amount reset are not fully specified.

**Decision needed:** specify whether unchanged entries in an otherwise changed revision are ignored or rejected, and which line amounts are “affected” by title-only edits. Preserve one parent contentVersion increment for a real revision and the existing all-or-nothing transaction rule.

**Affected owners:** Agents 1, 10 and 9.

### Q03 — ApprovalTask mention versus normative reference scenario

**Sources:** §§23–26, 38.

Section 23 describes a saved `ApprovalTask` record for pilot approval, but supplies no schema, operation contract or grant fixture for it. Sections 24 and 38 define only the two Material Request types and direct manual `ApproveRequest` behavior.

**Decision needed:** confirm whether `ApprovalTask` is explanatory future-pattern context or required initial application state. If required, define its lifecycle/schema/permissions and update the example and acceptance coverage through change control.

**Affected owners:** Agent 1 and the business owner before approval application code is finalized.

### Q04 — Secret-file activation lifecycle

**Sources:** §§08, 21, 29, 37.

CLI initialization/reset uses one-use secret files. Account creation makes inactive users; human activation requires a password set. The specification does not define the exact bootstrap-secret file format, consumption protocol or how a restored inactive/disabled administrator is selected for recovery.

**Decision needed:** define the offline bootstrap/reset exchange, owner-only file handling, one-use consumption and resulting account revision/activity/stamp changes. Keep secrets out of CLI arguments, receipts and logs; do not invent public registration or a network reset route.

**Affected owners:** Agents 1, 5 and 10.

### Q05 — Administrator identity and token-issuer authority

**Sources:** §§08–09, 21, 37.

The grant model lists explicit management actions, while last-active-administrator protection refers to administrator status without defining a fixed role identifier or sufficient-grant predicate. Token scopes must be within both target grants and issuer authority; the exact authority relationship for an `identity.manage` caller also needs to be explicit.

**Decision needed:** define the administrator predicate across role/grant/user mutations, and whether token issuance requires the issuer to hold each issued business grant in addition to `identity.manage`. Test attempted last-admin loss through role edits as well as membership/disablement.

**Affected owners:** Agents 1, 5 and 9. This is a security contract decision, not an implicit framework convention.

### Q06 — Incomplete secondary DTOs and discovery schemas

**Sources:** §§10, 17, 20–21, 28, 33–37.

The specification fully defines primary DTOs and describes fields for StepResult/AuditEntry, but does not completely freeze each secondary wire field/type. It also requires method schemas for arbitrary typed records and built-in `records.*` arguments/results without defining their descriptor schema representation in full.

**Decision needed:** freeze exact StepResult, AuditEntry, capability/limit and polymorphic record/method-schema shapes before API/SDK implementation. Preserve existing required fields, strict request binding, redaction and discovery visibility. Do not make each consumer invent a different representation.

**Affected owners:** Agents 1, 6, 7 and 8.

### Q07 — Rate windows and login IP admission policy

**Sources:** §§08, 27.

Per-principal read/write rates and Identity lockout thresholds are fixed. The rate-window algorithm/burst behavior and IP-based login admission threshold are not specified.

**Decision needed:** document/test the window and burst semantics, trusted client-IP resolution and login admission limit. Configuration must not raise specified hard maxima. Avoid proxy headers from untrusted addresses affecting admission identity.

**Affected owners:** Agents 1, 5, 8 and 9.

## 4. Decision recording format

For each resolved implementation choice or clarification, record:

```text
ID / status / date / accountable reviewer
Question and affected specification sections
Decision and rationale
Alternatives considered only where material
Observable compatibility/security/data impact
Affected contracts, examples and acceptance tests
Specification revision, if observable behavior changes
Implementation and test evidence, once available
```

An engineering choice can close with proof under the current contract. A new or changed observable requirement must follow the specification's numbered revision process; a decision note alone cannot override 1.0.0.
