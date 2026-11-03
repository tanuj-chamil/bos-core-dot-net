# API and offline operator usage

This describes the current review build and its reference application. It is not a production release or a claim that the specification's release gates, Windows artifact, crash recovery, RPO, or RTO have been certified.

## Initialize and run

From a built host executable (or `dotnet run --project src/Bos.Host --` followed by the same arguments):

```text
bos init --data-dir ./bos-data --bootstrap-file ./bootstrap.secret
bos doctor --data-dir ./bos-data
bos serve --data-dir ./bos-data --listen https://localhost:8080
bos version
```

Initialization requires an empty workspace and an existing parent directory for the new bootstrap file. The file contains a cryptographically generated administrator password, followed by a newline. The command reports the selected administrator's user name and file path, never the password. Initial user name is `Admin`. It creates `bos.json`, `bos.db`, root-level `cursor.key`, and `keys/` for persisted ASP.NET Core Data Protection keys. Reference types and flows are published through application methods below.

Use an HTTPS certificate trusted by the client and configured for the ASP.NET Core host. Cookie authentication and antiforgery issuance require HTTPS, including on loopback. A loopback HTTP listener is useful for service-token development but cannot be used for human login. Non-loopback HTTP is rejected. Unknown `bos.json` properties fail startup; adapters are selected by the fixed names `sqlite` and `yamldotnet`. Default timezone is `Asia/Colombo`, origins are denied unless configured, and limits may only be lowered. Forwarded headers are not implicitly trusted.

All running hosts and offline commands hold `workspace.lock`. Stop `serve` before doctor, backup, restore, or password recovery. An in-use workspace fails admission.

## Human login, CSRF, and password change

The following JavaScript illustrates calls from a same-origin HTTPS browser client. Keep passwords in a password manager or protected local file; do not place them in URLs, command-line arguments, checked-in examples, or diagnostic logs.

```javascript
async function csrf() {
  const response = await fetch('/v1/auth/antiforgery', {credentials: 'same-origin'});
  const body = await response.json();
  if (!response.ok) throw new Error(body.error?.code ?? 'CSRF_FAILED');
  return body.result; // {requestToken, headerName: "X-CSRF-TOKEN"}
}

async function authPost(path, input) {
  const token = await csrf();
  const response = await fetch('/v1/auth/' + path, {
    method: 'POST', credentials: 'same-origin',
    headers: {'Content-Type': 'application/json', [token.headerName]: token.requestToken},
    body: JSON.stringify(input)
  });
  if (!response.ok) throw new Error((await response.json()).error.code);
  return response.status === 204 ? null : (await response.json()).result;
}

await authPost('login', {userName: 'Admin', password: prompt('Bootstrap file password')});
await authPost('change-password', {
  currentPassword: prompt('Current password'), newPassword: prompt('New password (12–128 characters)')
});
// Password change signs out and invalidates old sessions. Sign in with the new password.
await authPost('login', {userName: 'Admin', password: prompt('New password')});
```

Login itself requires antiforgery validation. Fetch a new token after login or identity changes; anonymous tokens should not be reused as authenticated tokens. Session cookies are HttpOnly, Secure, SameSite=Lax, fixed at eight hours without sliding extension. `authPost('logout', {})` signs out and returns 204. Password change also returns 204. Login and password-change errors do not disclose whether an account exists.

The bootstrap/reset file is a **password disclosure**, not a redeemable one-use token. Complete the password-change step, then securely remove the local disclosure file. Possession of the file remains sufficient to log in until its password is changed/reset. Do not claim one-use consumption is enforced by this build.

## Method requests and discovery

Application methods use `POST /v1/methods/<exact.method.name>` with a JSON argument object. Success is `{result,meta:{traceId}}`; errors are `{error:{code,message,details,traceId,retryable}}`. Send a fresh `Idempotency-Key` for a new mutation and reuse the same key and arguments when resolving a lost response. Cookie-authenticated mutations also require `X-CSRF-TOKEN`. Bearer-token calls use their token scopes and do not require cookie CSRF tokens.

```javascript
async function call(method, args, key = crypto.randomUUID()) {
  const token = await csrf();
  const response = await fetch('/v1/methods/' + method, {
    method: 'POST', credentials: 'same-origin',
    headers: {'Content-Type': 'application/json', 'Idempotency-Key': key,
              [token.headerName]: token.requestToken},
    body: JSON.stringify(args)
  });
  const body = await response.json();
  if (!response.ok) throw new Error(body.error?.code ?? 'REQUEST_FAILED');
  return body.result;
}

const capabilities = await call('system.getCapabilities', {});
const current = await call('identity.getCurrent', {});
const createContract = await call('operations.get', {name: 'CreateRequest', contractVersion: 1});
```

Authenticated `GET /v1/openapi.json` provides discovery. Health endpoints are `/health/live` and `/health/ready`. Discovery and returned run results are authorization-filtered. A flow may return a `failed` RunReceipt in a successful HTTP response: inspect `result.status` and its sanitized `error`, not HTTP status alone. Running replays can return 202. Interrupted runs are not automatically retried.

Business integers and decimals are canonical JSON strings. Metadata revisions and published/contract versions are JSON numbers. For example, operation `expectedRevision: 1` is a number, record `contentVersion: "1"` is a business integer, and the published flow's input `revision: "1"` is schema-encoded as an integer string. Full record results retain `id`, `type`, `typeVersion`, `revision`, `deleted`, `data`, `createdAt`, `createdBy`, `updatedAt`, and `updatedBy`.

## Publish and exercise the reference application

Publish the files in `docs/examples` through the real application API in this order: `MaterialRequest.json`, `MaterialRequestLine.json`, then `SubmitMaterialRequest.yaml`. The five business operations are registered by host startup at contract version 1. One way to select local example files in the same-origin browser client is:

```javascript
function selectText() {
  return new Promise(resolve => {
    const picker = document.createElement('input'); picker.type = 'file';
    picker.onchange = async () => resolve(await picker.files[0].text());
    picker.click();
  });
}

// Run twice, selecting the parent definition first, then the line definition.
const definition = JSON.parse(await selectText());
const draft = await call('types.create', {definition});
await call('types.publish', {name: draft.name, expectedRevision: draft.draftRevision});

// Select SubmitMaterialRequest.yaml.
const flowDraft = await call('flows.create', {definition: {source: await selectText()}});
await call('flows.publish', {name: flowDraft.name, expectedRevision: flowDraft.draftRevision});
```

Use separate block scopes or different variable names when repeating the definition snippet in the same JavaScript scope. Existing names are edited through `types.getDraft` / `types.update` or their `flows.*` equivalents; creation is not an upsert.

```javascript
const created = await call('operations.call', {
  name: 'CreateRequest', contractVersion: 1,
  input: {title: 'Materials', currency: 'LKR', lines: [
    {item: 'First item', quantity: '2.000', unitPrice: '150.00'},
    {item: 'Second item', quantity: '3.000', unitPrice: '200.00'}
  ]}
});
// Draft, contentVersion "1", total and both amounts "0.00".

const receipt = await call('flows.run', {
  name: 'SubmitMaterialRequest', version: 1,
  input: {requestId: created.request.id, revision: String(created.request.revision)}
});
if (receipt.status !== 'succeeded') throw new Error(receipt.error?.code ?? receipt.status);
// receipt.result.total === "900.00"; line amounts are "300.00" and "600.00".

const submitted = await call('records.get', {type: 'MaterialRequest', id: created.request.id});
const approved = await call('operations.call', {
  name: 'ApproveRequest', contractVersion: 1,
  input: {id: submitted.id, expectedRevision: submitted.revision}
});
```

`ReviseRequest` takes parent `id`/numeric `expectedRevision`, optional `title`, and optional `lineChanges`, each with line `id`, numeric `expectedRevision`, and changed `item`, `quantity`, or `unitPrice`. Every edited line must belong to the parent and actually change an allowed field; duplicate IDs fail. Revisions are checked and a line-only edit increments the parent's revision and `contentVersion` once, resets affected line amounts and parent total, and invalidates an earlier submit decision. Adding/removing lines and changing currency are unavailable. Edits require Draft, submission requires a nonempty complete page of at most 100 lines with independently verified pricing, and approval requires Submitted. Direct generic writes cannot bypass operation-only guards.

## Service principals and opaque tokens

An administrator can provision a service account without a password. Build an exact grant list from the reference `roles.json` or from the minimum permissions the client needs:

```javascript
const grants = [
  {action: 'record.read', resource: 'MaterialRequest'},
  {action: 'record.read', resource: 'MaterialRequestLine'}
];
let service = await call('users.create', {
  user: {userName: 'ReaderService', displayName: 'Reader service', kind: 'service'}
});
const role = await call('roles.create', {role: {name: 'ReaderServiceRole', grants}});
service = await call('roles.assign', {
  userId: service.id, roleId: role.id, expectedUserRevision: service.revision
});
service = await call('users.update', {
  id: service.id, expectedRevision: service.revision, changes: {active: true}
});
const issued = await call('tokens.create', {principalId: service.id, scopes: grants});
// Move issued.token directly into a protected credential store; never log it.
```

Use `Authorization: Bearer <opaque token>` for service requests, preferably without also attaching session cookies. The stored token is a hash-backed BOS credential, not a JWT or OAuth token. Scopes cannot exceed either the target principal's grants or the issuer's authority. Default expiry is 30 days, maximum 365. Revoking a token or disabling the principal takes effect at the next authorization boundary.

Token plaintext is disclosed once. Issuance replay returns `token:null` with `SECRET_ALREADY_DISCLOSED`; revoke the old token through `tokens.revoke({id: issued.metadata.id})` and issue again under a new command key if the first response was lost. Administrative `users.setPassword` has a redacted receipt and does not change the password on replay, even if another secret is supplied with the same key. Business flows cannot call identity administration.

## Offline backup, restore, and administrator recovery

```text
bos backup --data-dir ./bos-data --output ./snapshot.bosbak
bos restore --input ./snapshot.bosbak --data-dir ./restored
bos doctor --data-dir ./restored
bos reset-admin --data-dir ./restored --secret-file ./reset.secret
```

Run while stopped. Backup destinations must be new files outside the workspace, with an existing parent directory. The adapter uses SQLite's database backup API, checks integrity and foreign keys, snapshots configuration and protected key files, hashes every payload entry into a versioned ZIP manifest, verifies the archive, then atomically renames the temporary archive. Existing archives are never overwritten. Do not copy only `bos.db` from an active WAL workspace. Keep backups encrypted/protected as sensitive business and identity data.

Restore requires an empty target. It rejects unsupported engine/schema versions, missing or altered payloads, duplicate/unsafe archive paths, symlinks, and failing SQLite integrity checks. CLI restore invalidates all restored user security stamps and API tokens, regenerates root-level `cursor.key`, marks abandoned runs and their replay receipts interrupted, retains committed replay results, and runs Identity administrator recovery in staging before publishing the restored workspace. The new password is written to `restored/admin-recovery.secret`; the command reports the selected administrator's name. Sign in over HTTPS, change this password, then remove the disclosure file. Restoring loses changes made after the snapshot.

`reset-admin` also generates a new password into a new protected file, resets lockout through Identity, and invalidates that administrator's sessions. Recovery prefers an active human administrator (name `Admin` first, then stable ID order), falls back to the `Admin` human account, or creates `Admin`; it ensures the Administrators role has the bootstrap management grants. A service principal occupying the `Admin` name blocks fallback recovery. No password/token CLI options are accepted.

Both `IBackupAdapter.RestoreAsync` and CLI restore install the fresh recovery password through the same `IdentityService.BootstrapAsync` manager path in the verified staging database. Neither can report success if administrator recovery fails. The CLI uses `RestoreWithRecoveryAsync` only to also obtain the selected administrator summary; there is no recovery callback or incomplete storage-only restore. Unix files use mode 0600 and private directories 0700; Windows backup/secret files use an owner-only ACL. Windows behavior still requires independent execution evidence. ZIP directory records, entry counts, compressed file size, expanded payload totals, and actual manifest expansion are bounded before native extraction. Split/ZIP64 archives are not accepted by this under-4-GiB format. Publication installs a deliberately invalid `bos.json` marker before moving payloads and atomically replaces it with the verified configuration last. Strict host configuration rejects an interrupted publication. This is not a crash-atomic directory swap: discard an incomplete target and restore again.

`OperatorCli.RunAsync(string[] args)` returns `Task<int>`: 0 on success, 2 for command/option usage errors, and 1 for operational failures. It recognizes only `init`, `doctor`, `backup`, `restore`, `reset-admin`, and `version`; `Program` owns `serve`. Directory defaults to `./bos-data`. Doctor performs offline configuration, integrity, supported-schema, key and storage-headroom checks; it does not measure backup RPO/RTO or certify a release.

## Provision HTTPS on a clean deployment

The self-contained host can load an operator-provisioned PKCS#12 (`.pfx`/`.p12`) certificate directly. The deployment machine does **not** need the .NET SDK or a development certificate. Obtain/export a certificate and its private key through your CA or operating-system certificate tools, with the DNS names/IP addresses clients will use in its subject alternative names. Clients must trust its issuing CA.

Add the optional `https` object to the initialized workspace's `bos.json`. This complete minimal example uses files beneath the data directory; existing adapter, timezone, origin, and lowered-limit settings may remain alongside it:

```json
{
  "listen": "https://0.0.0.0:8080",
  "https": {
    "certificatePath": "tls/server.pfx",
    "passwordFile": "tls/server-password.txt"
  }
}
```

`certificatePath` is required when `https` is present. `passwordFile` is optional: omit it for a PKCS#12 file that does not require a passphrase. Unknown properties, including a literal `password` setting, are rejected. Both paths resolve relative to `--data-dir`, or may be explicit absolute paths. Files and every ancestor path component must not be symbolic links or Windows reparse points.

Provision both files as the OS account that runs BOS, or have an administrator assign that account ownership and permission to secure them. On Linux, an example using already protected exports is:

```sh
install -d -m 700 /srv/bos-data/tls
install -m 600 /secure-export/server.pfx /srv/bos-data/tls/server.pfx
install -m 600 /secure-export/server-password.txt /srv/bos-data/tls/server-password.txt
```

Create the password file through a protected editor or credential-provisioning tool; never put its actual contents in command arguments, environment variables, `bos.json`, or logs. It contains one UTF-8 line, optionally followed by LF or CRLF; a UTF-8 BOM is accepted. Spaces and Unicode are preserved exactly, without trimming or normalization. Embedded newlines and NUL characters are rejected. Certificate files are bounded to **1 MiB** and password files to **4 KiB**. Before reading, the host tightens file permissions to owner-only (0600 on Linux, an owner-only ACL on Windows).

Start the published executable normally:

```text
bos serve --data-dir /srv/bos-data
```

The certificate must contain its private key. The host loads it with an ephemeral key set rather than installing private keys in the machine/user certificate store. An explicit certificate configuration that cannot be loaded fails startup with a generic diagnostic; it does not silently substitute a development certificate. Stop the host, replace the protected files, then restart to apply certificate renewal or passphrase changes.

If `https` is omitted, ASP.NET Core's normal development-certificate fallback remains available for local development. A clean deployment should use the explicit configuration above. Non-loopback plaintext HTTP remains rejected, and forwarded/proxy headers are not implicitly trusted.
# Native extraction for self-contained distributions

Single-file distributions include the SQLite native library. The extraction directory must be owner-only **and on an executable filesystem**. A `noexec` temporary mount prevents loading native libraries even though `bos version` can still run. Set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a protected directory on an executable local volume when the default location is unsuitable; do not share it with untrusted users.
