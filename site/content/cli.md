# CLI reference

The BOS Core command line manages one local workspace. Business data, identities, roles, tokens, types, and flows are managed through the HTTP API rather than CLI subcommands.

## Commands

| Command | Purpose |
| --- | --- |
| `bos init --data-dir DIR --bootstrap-file FILE` | Create an empty workspace and initial administrator |
| `bos serve --data-dir DIR --listen URL` | Start the HTTPS API host |
| `bos doctor --data-dir DIR` | Check workspace configuration, integrity, keys, and storage headroom |
| `bos backup --data-dir DIR --output FILE` | Create a verified offline backup |
| `bos restore --input FILE --data-dir EMPTY_DIR` | Restore a backup and recover administrator access |
| `bos reset-admin --data-dir DIR --secret-file FILE` | Generate a new administrator password disclosure |
| `bos version` | Print product, specification, and backup-engine versions |

The default data directory is `./bos-data`. The default listener is `https://localhost:8080`.

## Initialization

```sh
bos init --data-dir ./bos-data --bootstrap-file ./bootstrap.secret
```

Initialization requires an empty workspace and a new secret-file path whose parent directory already exists. The command writes the generated administrator password to the protected file and prints only the administrator name and file location.

## Serving

```sh
bos serve --data-dir ./bos-data --listen https://localhost:8080
```

`serve` accepts only `--data-dir` and `--listen`. Configuration from `bos.json` is loaded from the selected data directory. Command-line `--listen` overrides its configured listener.

## Offline commands

Stop `serve` before running:

```sh
bos doctor --data-dir ./bos-data
bos backup --data-dir ./bos-data --output ./snapshot.bosbak
bos restore --input ./snapshot.bosbak --data-dir ./restored
bos reset-admin --data-dir ./bos-data --secret-file ./reset.secret
```

These commands acquire the same exclusive workspace lock as the server and fail when the workspace is in use.

## Exit status

| Status | Meaning |
| ---: | --- |
| `0` | Command completed successfully |
| `1` | Operational failure, such as workspace, permission, integrity, or storage failure |
| `2` | Unknown command, missing option, duplicate option, or invalid command syntax |

Credentials are never accepted as CLI option values. Bootstrap and recovery passwords are generated into protected files.
