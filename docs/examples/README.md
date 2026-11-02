# Reference application definitions

These are standalone documentation fixtures from [specification Sections 24–26 and 38](../specification-1.0.0.md). They are definitions and example inputs, not installed seed data or evidence of a working runtime.

| File | Use |
| --- | --- |
| [MaterialRequest.json](MaterialRequest.json) | Parent type definition; publish before the line type. |
| [MaterialRequestLine.json](MaterialRequestLine.json) | Line type with an indexed live reference to its parent. |
| [SubmitMaterialRequest.yaml](SubmitMaterialRequest.yaml) | Complete combined reference command flow; pinned to type/operation contract version 1. |
| [CreateRequest.input.json](CreateRequest.input.json) | Two-line business input producing the normative 900.00 submission fixture. |
| [roles.json](roles.json) | Requester and Approver RoleCreate examples; provision through the authorized role methods. |

## Intended publication and invocation order

1. Register all five trusted version-one business operations (`CreateRequest`, `ReviseRequest`, `RepriceRequestLine`, `FinalizeRequest`, `ApproveRequest`) and required built-in record descriptors in host code.
2. Create/publish `MaterialRequest`, then `MaterialRequestLine` using the type methods and current draft revisions.
3. Create/publish `SubmitMaterialRequest` using `definition: {source: <YAML text>}`; publication requires all pinned dependencies.
4. Provision users/roles and service-token scopes, where applicable, through the real administration/activation paths.
5. Call `operations.call` with `name: "CreateRequest"`, `contractVersion: 1`, and the create input. The returned request has a server-generated UUID and current metadata revision.
6. Call `flows.run` with `name: "SubmitMaterialRequest"`, `version: 1`, and `input: {requestId: <returned UUID>, revision: "1"}` for a newly created revision-one parent. Use its actual current revision encoded as a business Integer string if it has changed.
7. Expect line amounts `300.00` and `600.00`, parent total `900.00` and status `Submitted` after successful commit.
8. Invoke `ApproveRequest` separately as an authorized approver with the returned parent revision as a metadata JSON number.

All authenticated mutating methods require their own valid root idempotency key. A flow's nested calls reuse its root context/key/transaction. Management and business grants are separate; these role examples do not define the bootstrap administrator.

## Rollback and stale-intent scenarios

- Use a test-host-only finalization failure after staged pricing to prove no line/parent/history/index effects survive. No production flow input enables that failure.
- Use individually valid quantity/unitPrice on the second line whose product exceeds the amount field precision; the earlier line write must roll back.
- Revise a line through `ReviseRequest`; the parent's contentVersion and revision advance so a previously captured submission revision is stale.
- Direct generic record writes cannot bypass operation-only or managed-field guards, even for an administrator.

The runtime, conformance harness and publication commands remain to be implemented. The embedded examples in the specification and these standalone files must be updated together under specification change control.
