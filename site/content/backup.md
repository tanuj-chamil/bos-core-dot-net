# Backup and recovery

BOS Core provides stopped-host commands for verified workspace backup, empty-target restore, and administrator recovery.

## Create a backup

Stop the server before taking a backup:

```sh
bos backup --data-dir ./bos-data --output ./snapshot.bosbak
```

The destination must be a new file outside the workspace, and its parent directory must already exist. BOS Core never overwrites an existing archive.

The backup contains a verified SQLite snapshot, workspace configuration, and required protected key files. Every payload is hashed in the archive manifest before the temporary output is renamed into place.

Do not copy only `bos.db` from an active workspace. SQLite may have committed data in its WAL files.

## Protect backup files

A `.bosbak` archive contains sensitive business and identity data. BOS Core verifies integrity but does not claim the archive is encrypted. Store it in protected, encrypted backup storage with access controls appropriate for your data.

Test backup creation and restore on the actual deployment environment. A successful small restore is not an RPO or RTO guarantee.

## Restore into an empty directory

```sh
bos restore --input ./snapshot.bosbak --data-dir ./restored
bos doctor --data-dir ./restored
```

Restore rejects a nonempty destination, altered or missing payloads, unsafe archive paths, unsupported storage versions, and failing SQLite integrity checks.

After restore, BOS Core:

- Invalidates restored browser sessions and API tokens
- Generates a new cursor key
- Preserves committed business data and completed replay receipts
- Marks abandoned runs as interrupted without executing them again
- Writes a fresh administrator password to `restored/admin-recovery.secret`

Sign in over HTTPS, change the recovered password, and remove the disclosure file securely. Issue new service tokens for restored service principals.

Restoring loses changes made after the snapshot. If publication into the target directory is interrupted, discard the incomplete target and restore again; restore is not a crash-atomic directory swap.

## Recover administrator access

With the server stopped:

```sh
bos reset-admin --data-dir ./bos-data --secret-file ./reset.secret
```

The secret destination must be new. The command generates a password, resets administrator lockout, and invalidates that administrator's existing sessions. It does not accept a password on the command line.

Sign in, replace the generated password, and remove the reset file securely.

## Diagnose a workspace

```sh
bos doctor --data-dir ./bos-data
```

`doctor` checks configuration, database integrity, supported storage schema, protected keys, and storage headroom. It is a diagnostic command, not a release certificate or a measurement of recovery objectives.
