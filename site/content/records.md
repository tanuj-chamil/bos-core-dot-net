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

## Field kinds

BOS Core uses a closed field grammar. Unknown kinds and properties are rejected when a type is validated or published.

| Family | Kinds |
| --- | --- |
| Text and choice | `string`, `enum`, `multiEnum` |
| Exact numeric | `integer`, `decimal`, `percentage` |
| Date and time | `date`, `time`, `localDateTime`, `offsetDateTime`, `instant`, `duration`, `calendarPeriod` |
| Business values | `money`, `quantity`, `geopoint`, `dateRange`, `instantRange` |
| Composite | `object`, `array`, `map`, `taggedUnion` |
| References | `reference`, `polymorphicReference`, `versionedReference`, `userReference`, `attachment` |
| Other | `boolean` |

String fields support `minLength`, `maxLength`, bounded regular-expression `pattern`, and recognized formats such as email, E.164 phone, HTTP/HTTPS URL, UUID, URI, slug, IP address, hostname, country code, currency code, locale, and time zone. Numeric fields support inclusive or exclusive bounds and exact `multipleOf`. Arrays support bounded item counts and `uniqueItems`; maps require a bounded `maxProperties`.

Business integers and decimals use canonical strings. Composite business values use closed objects, for example:

```json
{
  "price": {"amount": "125.50", "currency": "USD"},
  "location": {"latitude": "6.9271", "longitude": "79.8612"},
  "window": {"start": "2026-11-03T09:00:00.000Z", "end": "2026-11-03T10:00:00.000Z"}
}
```

`versionedReference` stores both a record ID and an immutable record revision. `polymorphicReference` stores an allowed type name with the ID. BOS Core verifies all reference targets, including references nested in arrays, maps, objects, and tagged unions.

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

## Read record history

Every committed create, update, and delete stores an immutable record revision. Read one revision with `records.getRevision`:

```javascript
const original = await call('records.getRevision', {
  type: 'Contact',
  id: contact.id,
  revision: 1
});
```

Use `records.getHistory` for newest-first cursor pagination. History remains readable after a soft delete to callers who retain `record.read` permission. History methods are not callable from workflows.

## Manage attachments

Upload one private pending file with `POST /v1/attachments`. The request is `multipart/form-data` with exactly one file part named `file`, plus an `Idempotency-Key` header. Files are limited to 25 MiB. Cookie-authenticated uploads also require the antiforgery header.

```javascript
const body = new FormData();
body.append('file', selectedFile);
const response = await fetch('/v1/attachments', {
  method: 'POST',
  credentials: 'same-origin',
  headers: {
    'Idempotency-Key': crypto.randomUUID(),
    [csrfToken.headerName]: csrfToken.requestToken
  },
  body
});
const attachment = (await response.json()).result;
```

A pending upload is visible only to its uploader and expires after 24 hours. Link it in either of these ways:

- Put its ID in an `attachment` field during `records.create` or `records.update`; linking commits atomically with the record change.
- Call `attachments.link` with `type`, `recordId`, attachment `id`, and `expectedRevision` for a file that is not represented by a field.

After linking, `record.read` on the owning record controls download through `GET /v1/attachments/<id>/content`. `attachments.list` lists linked metadata. `attachments.remove` requires record update permission and clears the stored bytes; a file still used by an attachment field cannot be removed. Remove all linked attachments before deleting their owning record.

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
