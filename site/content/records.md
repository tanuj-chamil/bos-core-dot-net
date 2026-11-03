# Types and records

Types define validated business data. Records are instances of a published type and carry immutable identity plus revision metadata.

## Define a type

This direct-access type allows authorized clients to create and update records through the generic record methods:

```json
{
  "name": "Contact",
  "description": "A business contact",
  "accessMode": "direct",
  "allowedOperations": [],
  "fields": {
    "name": {
      "kind": "string",
      "required": true,
      "maxLength": 200
    },
    "email": {
      "kind": "string",
      "required": false,
      "maxLength": 320,
      "indexed": true
    }
  }
}
```

Create, validate, and publish the draft:

```javascript
const draft = await call('types.create', {definition}, await mutationOptions());
const report = await call('types.validate', {definition});
if (!report.valid) throw new Error('Type definition is invalid');

const published = await call('types.publish', {
  name: draft.name,
  expectedRevision: draft.draftRevision
}, await mutationOptions());
```

Published versions are immutable. Continue editing the retained draft with `types.update`, then publish another version. `types.get` uses the active published version when `version` is omitted.

## Type methods

| Method | Purpose |
| --- | --- |
| `types.create` | Create a new draft |
| `types.update` | Replace a draft using its expected revision |
| `types.validate` | Validate a definition without writing it |
| `types.publish` | Publish an immutable version |
| `types.getDraft` | Read the editable draft |
| `types.get` | Read a published version |
| `types.list` | List visible types |
| `types.listVersions` | List published versions |

## Create and update records

```javascript
const contact = await call('records.create', {
  type: 'Contact',
  data: {name: 'Asha', email: 'asha@example.test'}
}, await mutationOptions());

const updated = await call('records.update', {
  type: 'Contact',
  id: contact.id,
  expectedRevision: contact.revision,
  patch: {email: 'asha.new@example.test'}
}, await mutationOptions());
```

Every actual change increments `revision`. Sending a stale `expectedRevision` fails rather than overwriting another writer's change. Updates replace supplied top-level field values; they do not recursively merge nested objects.

`records.delete` performs a soft delete and also requires the current revision. Deletion fails while another live record references the target.

## Query records

Filters and sorting are available for `id` and declared indexed scalar fields:

```javascript
const page = await call('records.list', {
  type: 'Contact',
  query: {
    where: [
      {field: 'email', op: 'eq', value: 'asha.new@example.test'}
    ],
    limit: 50
  }
});
```

Use the returned `nextCursor` unchanged to fetch the next page. Cursors are bound to the caller and normalized query; changing filters, sort, projection, or permissions invalidates them.

`records.count` returns an exact string count:

```json
{"count": "42"}
```

## Protect business rules

Set `accessMode` to `operationsOnly` when generic writes must not bypass a business lifecycle. Declare the allowed operation names and call those operations instead of `records.create` or `records.update` directly. The [Material Request tutorial](material-request.md) demonstrates this model.
