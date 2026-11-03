# Material Request tutorial

The repository includes a small reference application showing versioned schemas, protected business operations, and an atomic submission workflow.

## Download the example files

- [MaterialRequest.json](../../docs/examples/MaterialRequest.json) — parent request type
- [MaterialRequestLine.json](../../docs/examples/MaterialRequestLine.json) — child line type
- [SubmitMaterialRequest.yaml](../../docs/examples/SubmitMaterialRequest.yaml) — submission workflow
- [CreateRequest.input.json](../../docs/examples/CreateRequest.input.json) — two-line input
- [roles.json](../../docs/examples/roles.json) — Requester and Approver grants

The host provides the version-1 `CreateRequest`, `ReviseRequest`, `RepriceRequestLine`, `FinalizeRequest`, and `ApproveRequest` operations used by this example.

## Publish the types

Publish the parent definition before the line definition:

```javascript
async function publishType(definition) {
  const draft = await call('types.create', {definition}, await mutationOptions());
  return call('types.publish', {
    name: draft.name,
    expectedRevision: draft.draftRevision
  }, await mutationOptions());
}

await publishType(materialRequestDefinition);
await publishType(materialRequestLineDefinition);
```

If the names already exist, retrieve and update their drafts instead of calling `types.create` again.

## Publish the workflow

```javascript
const flowDraft = await call('flows.create', {
  definition: {source: submitMaterialRequestYaml}
}, await mutationOptions());

await call('flows.publish', {
  name: flowDraft.name,
  expectedRevision: flowDraft.draftRevision
}, await mutationOptions());
```

The workflow pins both type versions and every operation contract it calls.

## Create a request

The example input contains two lines:

- `2.000 × 150.00`
- `3.000 × 200.00`

```javascript
const created = await call('operations.call', {
  name: 'CreateRequest',
  contractVersion: 1,
  input: {
    title: 'Materials',
    currency: 'LKR',
    lines: [
      {item: 'First item', quantity: '2.000', unitPrice: '150.00'},
      {item: 'Second item', quantity: '3.000', unitPrice: '200.00'}
    ]
  }
}, await mutationOptions());
```

The new request is a Draft. Calculated amounts and total start at `0.00`.

## Submit the request

```javascript
const receipt = await call('flows.run', {
  name: 'SubmitMaterialRequest',
  version: 1,
  input: {
    requestId: created.request.id,
    revision: String(created.request.revision)
  }
}, await mutationOptions());

if (receipt.status !== 'succeeded') {
  throw new Error(receipt.error?.code ?? receipt.status);
}
```

The workflow reprices both lines and finalizes the request in one transaction. The expected line amounts are `300.00` and `600.00`; the expected request total is `900.00` and its status becomes `Submitted`.

## Approve the current revision

Read the current request before approval because submission changed its revision:

```javascript
const submitted = await call('records.get', {
  type: 'MaterialRequest',
  id: created.request.id
});

const approved = await call('operations.call', {
  name: 'ApproveRequest',
  contractVersion: 1,
  input: {
    id: submitted.id,
    expectedRevision: submitted.revision
  }
}, await mutationOptions());
```

## Lifecycle rules

- Only Draft requests can be revised or submitted.
- Only Submitted requests can be approved.
- Every edited line must belong to the request and include its current revision.
- A line-only revision also advances the parent revision and invalidates the previous submission decision.
- Adding or removing lines and changing currency are not available in this preview.
- Generic record writes cannot bypass operation-managed fields and lifecycle rules.
