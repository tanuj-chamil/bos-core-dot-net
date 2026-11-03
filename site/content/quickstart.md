# Quickstart

Initialize a local workspace, start BOS Core over HTTPS, sign in as the generated administrator, and inspect the available API contracts.

## Before you start

Use either a published `bos` executable or the .NET 10 SDK pinned by the repository. Human login requires HTTPS, including on localhost. For local development, provision and trust an ASP.NET Core development certificate or configure your own certificate.

If you are building from source:

```sh
dotnet restore Bos.slnx --locked-mode
dotnet build Bos.slnx --no-restore
```

The examples below use the published executable name. Replace `bos` with `dotnet run --project src/Bos.Host --` when running from source.

## Initialize a workspace

Create the parent directory for the secret file, then initialize an empty workspace:

```sh
bos init --data-dir ./bos-data --bootstrap-file ./bootstrap.secret
bos doctor --data-dir ./bos-data
```

Initialization creates the SQLite workspace and an administrator named `Admin`. The generated password is written to `bootstrap.secret`; it is never printed. Protect that file as a credential.

## Start the server

```sh
bos serve --data-dir ./bos-data --listen https://localhost:8080
```

The default listener is `https://localhost:8080`. Keep the process running while you complete the next steps.

## Sign in

Browser sessions use a secure cookie and require an antiforgery token for login and other mutations:

```javascript
async function csrf() {
  const response = await fetch('/v1/auth/antiforgery', {
    credentials: 'same-origin'
  });
  const body = await response.json();
  if (!response.ok) throw new Error(body.error?.code ?? 'CSRF_FAILED');
  return body.result;
}

async function authPost(path, input) {
  const token = await csrf();
  const response = await fetch('/v1/auth/' + path, {
    method: 'POST',
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
      [token.headerName]: token.requestToken
    },
    body: JSON.stringify(input)
  });
  if (!response.ok) throw new Error((await response.json()).error.code);
  return response.status === 204 ? null : (await response.json()).result;
}

await authPost('login', {
  userName: 'Admin',
  password: prompt('Password from bootstrap.secret')
});
```

Change the generated password immediately. Password change signs the current session out, so sign in again afterward:

```javascript
await authPost('change-password', {
  currentPassword: prompt('Current password'),
  newPassword: prompt('New password (12–128 characters)')
});
```

Remove the bootstrap file securely after the password has been changed.

## Make your first API request

All application methods use `POST /v1/methods/<method-name>`, including read-only methods. The capability call is read-only, so it does not need an idempotency key or CSRF header:

```javascript
const response = await fetch('/v1/methods/system.getCapabilities', {
  method: 'POST',
  credentials: 'same-origin',
  headers: {'Content-Type': 'application/json'},
  body: '{}'
});

const body = await response.json();
if (!response.ok) throw new Error(body.error.code);
console.log(body.result);
```

Authenticated OpenAPI is available at `GET /v1/openapi.json`.

## Next steps

- Read the [API conventions](api.md), especially idempotency and error handling.
- Choose between [browser sessions and service tokens](authentication.md).
- Publish your first [type and records](records.md).
- Learn how to create and publish a [workflow](workflows.md).
