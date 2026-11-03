# Secure deployment

Run BOS Core with an HTTPS certificate trusted by every client. Human login and antiforgery issuance do not work over plaintext HTTP.

## Listener rules

The default listener is `https://localhost:8080`. Override it with `--listen` or the `listen` property in `bos.json`.

```sh
bos serve --data-dir /srv/bos-data --listen https://0.0.0.0:8080
```

Non-loopback HTTP listeners are rejected. Forwarded proxy headers are not implicitly trusted, so do not assume a plaintext backend behind an unconfigured TLS proxy is supported.

## Configure a certificate

Self-contained deployments can load an operator-provided PKCS#12 certificate without an installed .NET SDK or development certificate:

```json
{
  "listen": "https://0.0.0.0:8080",
  "https": {
    "certificatePath": "tls/server.pfx",
    "passwordFile": "tls/server-password.txt"
  }
}
```

`certificatePath` is required when `https` is present. Omit `passwordFile` only when the PKCS#12 file has no passphrase. Paths are relative to the data directory unless absolute.

The certificate must contain its private key and include the client-visible hostnames or IP addresses in its subject alternative names. Clients must trust its issuing certificate authority.

## Protect certificate files

Provision certificate and password files as the operating-system account that runs BOS Core:

```sh
install -d -m 700 /srv/bos-data/tls
install -m 600 /secure-export/server.pfx /srv/bos-data/tls/server.pfx
install -m 600 /secure-export/server-password.txt /srv/bos-data/tls/server-password.txt
```

Do not place the passphrase in `bos.json`, an environment variable, a command argument, or logs. The password file contains one UTF-8 line. BOS Core rejects symlinks and reparse points in protected paths and tightens file permissions before reading them.

Stop the service, replace the protected files, and restart to apply certificate renewal.

## Browser origins

Cross-origin browser requests are denied unless their exact origins appear in `allowedOrigins` in `bos.json`. Configure only origins you control and serve non-loopback origins over HTTPS.

```json
{
  "allowedOrigins": [
    "https://app.example.com"
  ]
}
```

Same-origin clients do not require a CORS entry.

## Workspace ownership

Run one BOS Core process per workspace. The server and offline commands hold an exclusive workspace lock. Stop the server before running `doctor`, `backup`, `restore`, or `reset-admin`.

Protect the entire data directory. It contains business data, identity data, cursor secrets, and Data Protection keys. Use operating-system permissions and encrypted storage appropriate for your environment.

## Single-file native extraction

Self-contained packages extract the SQLite native library at runtime. The extraction directory must be owner-only and located on an executable filesystem. A `noexec` temporary mount can prevent startup.

When the default temporary location is unsuitable, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a protected directory on an executable local volume. Do not share it with untrusted users.

## Preview platform status

Linux has the strongest current execution evidence. Windows packages are available, but Windows runtime, permission, and recovery behavior has not received equivalent validation. Complete your own deployment and recovery testing before using this preview for important data.
