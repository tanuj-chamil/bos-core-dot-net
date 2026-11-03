# Dependency and license inventory

This inventory accompanies the `1.0.0-preview.1` review build. Exact transitive versions and package content hashes are recorded in each project's `packages.lock.json`; the host lock is shipped as `dependencies.lock.json`. Self-contained distributions also include the .NET / ASP.NET Core runtime and RID-specific native assets.

## Runtime

| Component | Selected version | License / upstream |
| --- | --- | --- |
| .NET SDK (build only) | 10.0.111 | [.NET MIT and third-party notices](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| .NET / ASP.NET Core runtime | 10.0.11 for the recorded build; verify the publish manifest for each rebuild | [Runtime MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), [runtime third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT), [ASP.NET Core notices](https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt) |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.11 | [MIT / EF Core](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.2 | Apache-2.0; Copyright 2014–2025 SourceGear, LLC |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.2 | [Apache-2.0 / SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) |
| SQLitePCLRaw.core | 3.0.2 | Apache-2.0; Copyright 2014–2025 SourceGear, LLC |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.2 | Apache-2.0; Copyright 2014–2025 SourceGear, LLC |
| SourceGear.sqlite3 | 3.50.4.2 | Native SQLite 3.50.4 build; package metadata identifies SourceGear copyright but contains no license expression; [SQLite public-domain dedication](https://sqlite.org/copyright.html) describes upstream engine licensing. |
| YamlDotNet | 16.3.0 | MIT; Copyright Antoine Aubry and contributors; [upstream](https://github.com/aaubry/YamlDotNet) |

ASP.NET Core Identity, authentication, antiforgery, Data Protection and rate limiting use the installed/published ASP.NET Core shared framework rather than an independent authentication server package.

The explicit SQLitePCLRaw 3.0.2 bundle supersedes the older native bundle pulled by the initial SQLite provider selection. The dependency vulnerability command for this build reported no known vulnerable packages from its configured NuGet sources; this is a dated dependency check, not a guarantee against future advisories.

## Test/build-only packages

| Package | Version | Purpose / license family |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.14.1 | Test runner; MIT |
| xunit | 2.9.3 | Tests; Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Test discovery/execution; Apache-2.0 |

Their transitive test dependencies are captured in the test-project lockfiles and are not part of the host publish artifact. The OpenAPI generator uses the host's existing schemas and adds no external package dependency.

## Rechecking

```sh
dotnet restore Bos.slnx --locked-mode
dotnet list Bos.slnx package --include-transitive
dotnet list Bos.slnx package --vulnerable --include-transitive
```

Save the runtime version, exact locks, RID assets, build manifest and artifact checksums for every delivered build. License identifiers above describe upstream components; they do not assign a license to BOS source or replace upstream license texts and notices.
