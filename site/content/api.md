# API conventions

BOS Core exposes business methods through a compact JSON API. Method names and contracts are discoverable for the current caller.

## Method requests

Every application method uses the same route shape:

```text
POST /v1/methods/<exact.method.name>
Content-Type: application/json
```

The request body is the method's argument object. Read-only methods also use POST.

```javascript
async function call(method, args, options = {}) {
  const headers = {'Content-Type': 'application/json'};
  if (options.idempotencyKey) {
    headers['Idempotency-Key'] = options.idempotencyKey;
  }
  if (options.csrf) {
    headers[options.csrf.headerName] = options.csrf.requestToken;
  }

  const response = await fetch('/v1/methods/' + method, {
    method: 'POST',
    credentials: 'same-origin',
    headers,
    body: JSON.stringify(args)
  });
  const body = await response.json();
  if (!response.ok) throw new Error(body.error?.code ?? 'REQUEST_FAILED');
  return body.result;
}

async function mutationOptions() {
  return {
    idempotencyKey: crypto.randomUUID(),
    csrf: await csrf()
  };
}
```

The `csrf()` helper is defined in the [Quickstart](../quickstart.md). Call `mutationOptions()` separately for every new command. Retain the returned key only when retrying the same method and arguments after an uncertain response.

## Responses

A successful request contains the result and a trace identifier:

```json
{
  "result": {},
  "meta": {
    "traceId": "..."
  }
}
```

An error contains a stable code, a safe message, optional details, and whether the condition may be temporary:

```json
{
  "error": {
    "code": "REVISION_MISMATCH",
    "message": "The record revision has changed.",
    "details": [],
    "traceId": "...",
    "retryable": false
  }
}
```

Handle `error.code` in application logic. Do not depend on the wording of `message`.

## Mutations and idempotency

Authenticated mutations require an `Idempotency-Key` containing 16–128 printable ASCII characters. Use a new key for a new command. If a response is lost, repeat the same method and arguments with the same key.

```javascript
const created = await call('records.create', {
  type: 'Contact',
  data: {name: 'Asha'}
}, await mutationOptions());
```

Reusing a key with different arguments fails with `IDEMPOTENCY_CONFLICT`. Read-only methods do not require a key. For `operations.call`, the selected operation's mode determines whether the request is a mutation.

Cookie-authenticated mutations also require an antiforgery header. Bearer-token requests do not use cookie antiforgery tokens.

## Discovery

- `GET /v1/openapi.json` returns the caller-visible OpenAPI document.
- `system.getCapabilities` returns enabled methods and contracts visible to the caller.
- `operations.list` and `operations.get` discover registered business operations.

Discovery is authorization-filtered. An absent method may be unavailable or simply not visible to the current principal.

## Health endpoints

- `GET /health/live` reports process liveness.
- `GET /health/ready` reports whether the service is ready to accept work.

Public health responses intentionally contain little diagnostic detail.

## Values and revisions

Business integers and decimals are canonical JSON strings so clients do not lose precision. Metadata such as record revisions, draft revisions, and published versions are JSON numbers.

```json
{
  "quantity": "2.000",
  "unitPrice": "150.00",
  "expectedRevision": 3
}
```

Dates use `YYYY-MM-DD`. Instants use canonical RFC 3339 timestamps. Unknown arguments and properties fail instead of being ignored.

## Run receipts

A completed `flows.run` request can return HTTP 200 with either a `succeeded` or `failed` run receipt. Inspect `result.status` and `result.error`; HTTP success means the run request was accepted and resolved, not necessarily that its business workflow succeeded.
