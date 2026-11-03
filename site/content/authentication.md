# Authentication

BOS Core supports secure browser sessions for people and opaque bearer tokens for service clients. Authorization comes from current roles, grants, and token scopes.

## Browser sessions

Human login requires HTTPS and an antiforgery token:

1. Call `GET /v1/auth/antiforgery` with same-origin credentials.
2. Read `result.requestToken` and `result.headerName`.
3. Send that header with `POST /v1/auth/login` and `{userName,password}`.
4. Fetch a new antiforgery token after login or any identity change.
5. Include the token on every cookie-authenticated mutation.

Session cookies are `HttpOnly`, `Secure`, `SameSite=Lax`, and fixed at eight hours. They do not extend while active.

`POST /v1/auth/logout` and `POST /v1/auth/change-password` both require the session cookie and antiforgery header. Password change signs the user out.

## Roles and grants

Roles contain concrete grants such as:

```json
[
  {"action": "record.read", "resource": "MaterialRequest"},
  {"action": "record.read", "resource": "MaterialRequestLine"}
]
```

Use `roles.create` or `roles.update` to manage grants, then `roles.assign` and `roles.remove` to change membership. Administrative updates require the current revision returned by the API.

BOS Core reloads the principal's active state and grants at authorization boundaries. Disabling a user, removing a role, or revoking a token therefore takes effect on subsequent work.

## Service clients

Create a service principal without a password, assign its role, activate it, and issue a scoped token:

```javascript
let service = await call('users.create', {
  user: {
    userName: 'ReaderService',
    displayName: 'Reader service',
    kind: 'service'
  }
}, await mutationOptions());

const role = await call('roles.create', {
  role: {name: 'ReaderServiceRole', grants}
}, await mutationOptions());

service = await call('roles.assign', {
  userId: service.id,
  roleId: role.id,
  expectedUserRevision: service.revision
}, await mutationOptions());

service = await call('users.update', {
  id: service.id,
  expectedRevision: service.revision,
  changes: {active: true}
}, await mutationOptions());

const issued = await call('tokens.create', {
  principalId: service.id,
  scopes: grants
}, await mutationOptions());
```

The `mutationOptions()` helper creates a fresh idempotency key and reads the current browser antiforgery token, as described in [API conventions](api.md).

Use the resulting value as an opaque credential:

```text
Authorization: Bearer <token>
```

The token is not a JWT or OAuth token. Its plaintext is disclosed once; store it immediately in a protected credential store and never log it. Default expiry is 30 days and the maximum is 365 days.

Token scopes cannot exceed the target principal's grants or the issuing administrator's authority. If the issuance response is lost, revoke the token metadata and issue a replacement with a new idempotency key.

## Security rules

- Do not put passwords or tokens in URLs, command arguments, checked-in examples, or logs.
- Prefer bearer authentication without also attaching browser cookies.
- Treat bootstrap, reset, and restored administrator files as password disclosures.
- Use `tokens.revoke` when a service credential is retired or may have been exposed.
- Public registration, email recovery, external identity providers, and MFA are not included in this preview.
