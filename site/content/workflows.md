# Workflow engine

BOS workflows compose registered operations into a versioned business process. They are written in restricted YAML, validated before publication, and executed with explicit permissions and dependencies.

## When to use a workflow

Use a workflow when one request must coordinate several existing operations with conditions, bounded iteration, or a typed result.

A workflow is not general-purpose application code. It cannot access the network, inspect the host, invoke arbitrary .NET methods, call another workflow, or perform identity administration.

## Flow structure

Every definition declares:

- `format: bos.flow/1.0`
- A unique name
- `command` with an `atomic` transaction, or `query` with a `readOnly` transaction
- Input and output schemas
- Exact type and operation versions
- Concrete capabilities
- A sequence of typed steps

The available instructions are `let`, `call`, `if`, `forEach`, `require`, `log`, and `return`.

The included [SubmitMaterialRequest.yaml](../../docs/examples/SubmitMaterialRequest.yaml) flow demonstrates the complete format. It loads a request, checks its state, loads at most 100 lines, reprices each line, finalizes the request, and returns the calculated total.

## Validate and publish

Create a draft from YAML source:

```javascript
// Load the linked SubmitMaterialRequest.yaml file as UTF-8 text.
const source = await selectText();

const draft = await call('flows.create', {
  definition: {source}
}, await mutationOptions());
```

Validate without executing business work:

```javascript
const report = await call('flows.validate', {
  definition: {source}
});

if (!report.valid) console.table(report.errors);
```

Publish using the current draft revision:

```javascript
const published = await call('flows.publish', {
  name: draft.name,
  expectedRevision: draft.draftRevision
}, await mutationOptions());
```

Published flows are immutable. Update the retained draft and publish another version when behavior changes. Operation and type dependencies remain pinned to the declared versions.

## Run a published version

`flows.run` always requires an explicit version and an idempotency key:

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

A command workflow executes its business calls in one transaction. If a step fails, its business changes are rolled back and the run records a safe failure. Query workflows cannot write records.

## Execution receipts

Run receipts provide the run ID, flow version and hash, status, timing, step metadata, affected record IDs, and either the typed result or a sanitized error. Access is filtered using the caller's current permissions.

Use these methods to inspect runs:

| Method | Purpose |
| --- | --- |
| `flows.getRun` | Read one visible run |
| `flows.listRuns` | List visible runs |
| `flows.getRunSteps` | Read bounded step metadata and logs |
| `flows.disable` | Reject new runs for a flow name |
| `flows.enable` | Allow new runs again |

Disabling a flow does not reverse committed work. An interrupted run is marked `interrupted` during recovery and is not automatically executed again.

## Safety limits

The preview limits YAML source to 64 KiB, compiled flow data to 256 KiB, static and recorded steps to 500, loop sources to 100 items, output to 64 KiB, and execution to approximately two seconds. Expressions are typed and use a fixed set of pure helpers.

Design workflows for short, deterministic business orchestration. Use application code for long-running jobs, network integration, or unbounded processing.
