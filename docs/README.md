# BOS Core documentation

**Product contract:** BOS Core 1.0\
**Specification revision:** 1.0.0\
**Baseline date:** 3 November 2026\
**Current status:** documentation baseline; implementation and release evidence pending.

BOS Core is a small, single-tenant, headless business platform built around versioned record types, validated records and restricted YAML flows, with SQLite persistence and one application service boundary.

## Reading map

| Document | Purpose |
| --- | --- |
| [Complete implementation specification](specification-1.0.0.md) | Normative product requirements, method catalogue, shared DTOs, examples and release gates supplied for BOS Core 1.0. |
| [Architecture and invariants](architecture.md) | Module responsibilities, trust boundaries, transaction ownership, execution paths and durable-state invariants. |
| [Implementation plan](implementation-plan.md) | Documentation-first build sequence, ten-agent ownership, integration checkpoints and handoff requirements. |
| [Decision register](decisions.md) | Frozen product decisions, implementation choices to verify and material ambiguities to resolve before affected code is built. |
| [Acceptance and evidence tracker](acceptance.md) | A01–A24 test obligations, evidence expectations and current verification status. |
| [Release report template](release-report-template.md) | Build identity, locked dependencies, measured artifacts, test evidence, operational evidence and release decision. |
| [Reference application examples](examples/README.md) | Standalone Material Request schemas, complete YAML flow, two-line input and Requester/Approver role fixtures. |

## Authority and change control

The [1.0.0 specification](specification-1.0.0.md) is the normative source. The other documents organize its implementation; they do not relax it. The source document was reformatted into Markdown, with pagination removed and the two reference-flow blocks combined into one document. Requirements and examples retain their original product meaning.

Observable behavior changes require a numbered specification revision and corresponding changes to schemas, examples, capability descriptors and conformance tests. Open questions in the decision register are unresolved questions, not permission to choose weaker behavior.

## Status and evidence rules

- A requirement is not an implementation claim.
- A planned test is not a passed test.
- A working demonstration is not a complete 1.0 release.
- Every acceptance gate begins **Not run**; platform artifacts begin **Not built / unverified**.
- Only evidence from the actual implementation, HTTP/application paths and real SQLite transactions can advance release status.
- Record exact commands, source identity, output, platform and checksums when evidence becomes available.

The current workspace contains this documentation baseline. No software acceptance gate or deployment target is marked as passed.
