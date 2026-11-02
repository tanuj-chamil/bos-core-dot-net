# BOS Core release report — template

> Template only. Unfilled fields and tables are not evidence. Copy this template into an identified build report and link actual sanitized outputs/checksums. The current project status is documentation baseline only.

## 1. Decision summary

| Field | Value |
| --- | --- |
| Product contract / specification | BOS Core 1.0 / 1.0.0 |
| Report ID / UTC date | Pending |
| Decision | Pending: below demonstration / demo achieved / integrated release candidate / 1.0 release |
| Accountable reviewer | Pending |
| Scope or numbered specification changes | Pending |
| Verified gates / total | Pending / 24 |
| Failed / partial / blocked / not-run gates | Pending |
| Material correctness defects and next actions | Pending |

## 2. Source and build identity

| Field | Recorded value / evidence |
| --- | --- |
| Repository / source revision | Pending |
| Dirty, staged and untracked source status | Pending |
| UTC build timestamp | Pending |
| Build host OS / architecture | Pending |
| Exact .NET SDK and target framework | Pending |
| SDK pin and package lockfile locations / hashes | Pending |
| Dependency/license inventory | Pending |
| SQLite provider / native engine runtime version | Pending |
| Supported target RIDs | Required: linux-x64 and win-x64; evidence pending |
| Configuration and lowered limits used | Pending |
| Build-manifest location / hash | Pending |

Record exact restore/build/test/publish commands and exit codes here. Include relevant output paths. Do not claim reproducibility without the saved build inputs/manifest.

## 3. Artifacts and measured size

Record separate measurements for each target. The under-50-MB preference is a target, not a passed gate inferred from assembly size.

| Target RID | Executable / distribution path | SHA-256 | Application assembly bytes | Compressed distribution bytes | Uncompressed distribution bytes | Installed footprint bytes | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| linux-x64 | Pending | Pending | Not measured | Not measured | Not measured | Not measured | Not built / unverified |
| win-x64 | Pending | Pending | Not measured | Not measured | Not measured | Not measured | Not built / unverified |

Record included OpenAPI, sample definitions, version metadata and dependency/license inventory. Describe native extraction location/protection and any measured size-target miss.

## 4. Functional and catalogue evidence

Link the build-specific [A01–A24 tracker](acceptance.md), exact test names and sanitized outputs. Each result must correspond to this source/build.

| Evidence item | Link / result |
| --- | --- |
| A01–A24 per-gate results and scenarios | Pending |
| Initial method-to-test inventory | Pending |
| Method registry / OpenAPI / capability agreement | Pending |
| All pure helpers and versioned descriptors | Pending |
| Reserved methods unavailable and unadvertised | Pending |
| Type/flow version and publication compatibility | Pending |
| Material Request 900.00 fixture | Pending |
| Line-edit parent revision invalidation | Pending |
| Authorization/lifecycle bypass rejection | Pending |
| Rollback after first staged reference-line write | Pending |

## 5. Concurrency, crash and storage faults

| Scenario | Exact setup / separate connections or process boundaries | Observed durable outcome | Evidence |
| --- | --- | --- | --- |
| Same-revision update race | Pending | Pending | Pending |
| Last-unit issue race | Pending | Pending | Pending |
| Concurrent unique-value reservation | Pending | Pending | Pending |
| Two-record grouped rollback | Pending | Pending | Pending |
| Kill before commit | Pending | Pending | Pending |
| Kill after commit / before response | Pending | Pending | Pending |
| Abandoned running-run recovery | Pending | Pending | Pending |
| Disk full | Pending | Pending | Pending |
| Storage busy / lock admission | Pending | Pending | Pending |
| Uncertain commit resolution on fresh connection | Pending | Pending | Pending |
| Failed terminal-receipt persistence | Pending | Pending | Pending |

## 6. Security and resource boundaries

| Evidence item | Link / result |
| --- | --- |
| Identity and BOS share atomic transaction ownership | Pending |
| Last-active-admin protection across all mutation paths | Pending |
| Cookie settings, antiforgery, lockout and current authorization reload | Pending |
| Token scopes, expiry/revocation, constant-time verification and one-time disclosure | Pending |
| Secret-setting and replay redaction behavior | Pending |
| Safe audit/error/run-step disclosure; no credential payloads | Pending |
| YAML event rejection before expansion and dispatch | Pending |
| Exact numeric and Missing/Null semantics | Pending |
| Query VM/deadline and shared execution/allocation budgets | Pending |
| Rate/admission/overload and cancellation/shutdown | Pending |
| Workspace lock, strict configuration and protected keys | Pending |
| Production artifact has no test fault-injection controls | Pending |

## 7. Backup, restore and operations

| Field | Measurement / evidence |
| --- | --- |
| Backup source build / engine schema | Pending |
| Backup timestamp, snapshot path and SHA-256 | Pending |
| Manifest and every bundled entry verified | Pending |
| Database backup API / SQLite integrity result | Pending |
| Protected key/configuration inclusion | Pending |
| Empty-target / stopped-host / lock enforcement | Pending |
| Restored data and committed receipts verified | Pending |
| Sessions and API tokens invalidated | Pending |
| Cursor keys regenerated; old cursors rejected | Pending |
| Abandoned runs interrupted; no automatic replay | Pending |
| Fresh local administrator recovery verified | Pending |
| Daily backup schedule / actual backup age | Pending |
| Observed loss window vs 24-hour RPO | Not measured |
| Restore duration vs one-hour RTO | Not measured |
| Deployment hardware / storage used for measurement | Pending |
| Upgrade / newer-schema rejection / rollback evidence | Pending |

## 8. Clean-host distribution evidence

| Target | Host OS/version/architecture | No installed .NET runtime confirmed | Artifact checksum matched | Launch and fixture output | Result |
| --- | --- | --- | --- | --- | --- |
| linux-x64 | Pending | Pending | Pending | Pending | Unverified |
| win-x64 | Pending | Pending | Pending | Pending | Unverified |

Cross-compilation or launch on a development machine is not a substitute for the required clean-host result. Record unavailable platform access as blocked/unverified.

## 9. Known limits, defects and next actions

| ID | Severity / affected gate | Reproduction and durable-state impact | Owner / next action | Status |
| --- | --- | --- | --- | --- |
| Pending | Pending | Pending | Pending | Pending |

List material omissions, unresolved decision-register items, hard-limit observations and platform gaps. Do not silently reduce initial scope. Deliberately postponed requirements require a numbered specification revision.

## 10. Review and final disposition

- Transaction owner, business write gates and current authorization snapshot reviewed: pending.
- Run reservation, commit outcome, idempotency and restore behavior reviewed: pending.
- Every initial method/helper, reference scenario and acceptance gate accounted for: pending.
- Final source/build matches evidence and distributed checksums: pending.
- Accountable human reviewer and decision date: pending.

**Final disposition:** pending. A complete 1.0 release requires every initial method and all mandatory semantics plus A01–A24 to pass. If that evidence is incomplete, label the artifact according to demonstrated status and name the unpassed gates.
