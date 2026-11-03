using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Bos.Identity;
using Microsoft.Data.Sqlite;

namespace Bos.Sqlite;

/// <summary>Offline, verified snapshots. The host and all operator commands share workspace.lock.</summary>
public sealed class SqliteBackupAdapter(string dataDirectory) : IBackupAdapter
{
    private const long MaximumBundleBytes = 3L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private const long MaximumArchiveBytes = MaximumBundleBytes + 16 * 1024 * 1024;
    private const int MaximumDirectoryBytes = 32 * 1024 * 1024;
    public const string RecoverySecretName = "admin-recovery.secret";
    private readonly string directory = Path.GetFullPath(dataDirectory);

    public static FileStream AcquireWorkspaceLock(string dataDirectory)
    {
        var path = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(path)) throw new BosException("INVALID_REQUEST", "Workspace directory does not exist.", 400);
        RejectLink(path);
        var lockPath = Path.Combine(path, "workspace.lock");
        RejectLink(lockPath);
        try
        {
            var stream = OpenPrivate(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Offline workspace locking is supported on Linux and Windows.");
                stream.Lock(0, 1);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException) { throw new BosException("STORAGE_BUSY", "Workspace is in use; stop the host first.", 409); }
    }

    public async Task SnapshotAsync(string destination, CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new BosException("INVALID_REQUEST", "Backup destination directory does not exist.", 400);
        RejectLink(parent);
        if (Within(directory, destination)) throw new BosException("INVALID_REQUEST", "Store backups outside the workspace.", 400);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new BosException("INVALID_REQUEST", "Backup destination already exists.", 409);
        using var workspace = AcquireWorkspaceLock(directory);
        var stage = Path.Combine(parent, ".bos-backup-" + Guid.NewGuid().ToString("N"));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        CreatePrivateDirectory(stage);
        try
        {
            var sourceDatabase = Path.Combine(directory, "bos.db");
            RequireRegularFile(sourceDatabase);
            RequireRegularFile(Path.Combine(directory, "bos.json"));
            RequireRegularFile(Path.Combine(directory, "cursor.key"));
            if (new FileInfo(Path.Combine(directory, "cursor.key")).Length != 32 || !Directory.Exists(Path.Combine(directory, "keys")))
                throw new BosException("INVALID_STATE", "Workspace protected keys are missing or invalid.");
            await CheckDatabaseAsync(sourceDatabase, ct);
            var snapshot = Path.Combine(stage, "bos.db");
            await using (var source = Connection(sourceDatabase, SqliteOpenMode.ReadOnly))
            await using (var target = Connection(snapshot, SqliteOpenMode.ReadWriteCreate))
            {
                await source.OpenAsync(ct);
                await target.OpenAsync(ct);
                ct.ThrowIfCancellationRequested();
                source.BackupDatabase(target);
            }
            PrivateFile(snapshot);
            var schemaVersion = await CheckDatabaseAsync(snapshot, ct);
            var payload = new List<string> { "bos.db" };
            var copiedBytes = new FileInfo(snapshot).Length;
            if (copiedBytes > MaximumBundleBytes) throw new BosException("CAPACITY_EXCEEDED", "Backup size limit exceeded.");
            foreach (var source in EnumerateProtectedFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(directory, source).Replace(Path.DirectorySeparatorChar, '/');
                if (relative is "bos.db" or "bos.db-wal" or "bos.db-shm" or "workspace.lock" || relative.EndsWith(".secret", StringComparison.OrdinalIgnoreCase)) continue;
                ValidateEntryPath(relative);
                if (relative == "manifest.json") throw new BosException("INVALID_REQUEST", "Workspace uses a reserved backup filename.", 400);
                if (payload.Count >= MaximumEntries) throw new BosException("CAPACITY_EXCEEDED", "Too many protected workspace files.");
                var target = SafePath(stage, relative);
                CreatePrivateDirectory(Path.GetDirectoryName(target)!);
                copiedBytes += await CopyPrivateAsync(source, target, MaximumBundleBytes - copiedBytes, ct);
                payload.Add(relative);
            }
            if (!payload.Contains("bos.json")) throw new BosException("INVALID_STATE", "Workspace configuration is missing.");
            var entries = new JsonArray();
            long total = 0;
            foreach (var relative in payload.Order(StringComparer.Ordinal))
            {
                var path = SafePath(stage, relative);
                var size = new FileInfo(path).Length;
                if ((total = checked(total + size)) > MaximumBundleBytes) throw new BosException("CAPACITY_EXCEEDED", "Backup size limit exceeded.");
                entries.Add(new JsonObject { ["path"] = relative, ["size"] = size, ["sha256"] = await HashAsync(path, ct) });
            }
            var manifest = new JsonObject
            {
                ["format"] = "bos.backup/1", ["engineVersion"] = 1, ["schemaVersion"] = schemaVersion,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["entries"] = entries
            };
            await using (var stream = OpenPrivate(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var relative in payload)
                    {
                        var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                        await using var input = File.OpenRead(SafePath(stage, relative));
                        await using var output = entry.Open();
                        await input.CopyToAsync(output, ct);
                    }
                    await using var metadata = zip.CreateEntry("manifest.json").Open();
                    await metadata.WriteAsync(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(manifest)), ct);
                }
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            await VerifyBundleAsync(temporary, null, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            await AppendOperatorAuditAsync(directory, "system.backup", ct);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            Directory.Delete(stage, recursive: true);
        }
    }

    public async Task RestoreAsync(string source, string destination, CancellationToken ct = default)
        => _ = await RestoreWithRecoveryAsync(source, destination, ct);

    /// <summary>Same complete restore as the interface method, additionally returning the recovered administrator summary for the CLI.</summary>
    public async Task<JsonObject> RestoreWithRecoveryAsync(string source, string destination, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        RequireRegularFile(source);
        RejectLink(destination);
        if (File.Exists(destination)) throw new BosException("INVALID_REQUEST", "Restore target must be an empty directory.", 400);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new BosException("INVALID_REQUEST", "Restore target must be empty.", 409);
        CreatePrivateDirectory(destination);
        using var workspace = AcquireWorkspaceLock(destination);
        // Recheck under the lock in case another compliant process won admission.
        if (Directory.EnumerateFileSystemEntries(destination).Any(x => Path.GetFileName(x) != "workspace.lock"))
            throw new BosException("INVALID_REQUEST", "Restore target must be empty.", 409);
        var stage = Path.Combine(destination, ".restore-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(stage);
        var published = new List<string>();
        try
        {
            await VerifyBundleAsync(source, stage, ct);
            await InvalidateRestoredIdentityAsync(Path.Combine(stage, "bos.db"), ct);
            CreatePrivateDirectory(Path.Combine(stage, "keys"));
            // Cursor MACs are deliberately not portable across a restore. Host uses this path.
            var cursor = Path.Combine(stage, "cursor.key");
            if (File.Exists(cursor)) File.Delete(cursor);
            await WritePrivateAsync(cursor, RandomNumberGenerator.GetBytes(32), ct);
            var secret = NewAdminSecret();
            await WritePrivateAsync(Path.Combine(stage, RecoverySecretName), Encoding.UTF8.GetBytes(secret + "\n"), ct);
            var factory = new SqliteUnitOfWorkFactory(Path.Combine(stage, "bos.db"));
            JsonObject recovered;
            await using (var uow = await factory.BeginWriteAsync(ct))
            {
                var identity = new IdentityService(factory, new SystemClock(), new UuidGenerator());
                recovered = await identity.BootstrapAsync(uow, secret, ct);
                await uow.CommitAsync(ct);
            }
            await AppendOperatorAuditAsync(stage, "system.restore", ct);
            // The UoW factory pools connections. Release its idle staging handles before
            // moving the database (required on Windows, and prevents orphaned WAL handles).
            SqliteConnection.ClearAllPools();
            await CheckDatabaseAsync(Path.Combine(stage, "bos.db"), ct);
            // Strict HostOptions rejects this unknown setting. If publication is interrupted,
            // serve cannot mistake a partially restored workspace for a ready workspace.
            var configuration = Path.Combine(destination, "bos.json");
            await WritePrivateAsync(configuration, Encoding.UTF8.GetBytes("{\"restoreInProgress\":true}"), ct);
            published.Add(configuration);
            CreatePrivateDirectory(Path.Combine(destination, "keys"));
            foreach (var file in EnumerateProtectedFiles(stage).ToArray())
            {
                var relative = Path.GetRelativePath(stage, file);
                if (relative == "bos.json") continue;
                var target = Path.Combine(destination, relative);
                CreatePrivateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: false);
                published.Add(target);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(Path.Combine(stage, "bos.json"), configuration, overwrite: true);
            return recovered;
        }
        catch
        {
            // Keep the invalid configuration marker until every published payload is gone.
            foreach (var path in published.AsEnumerable().Reverse()) File.Delete(path);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(stage, recursive: true);
        }
    }

    public static string NewAdminSecret() => "Bos1!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        RejectLink(path);
        await using var stream = OpenPrivate(path, FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        stream.Flush(flushToDisk: true);
    }

    private static async Task VerifyBundleAsync(string source, string? extraction, CancellationToken ct)
    {
        try { await VerifyBundleCoreAsync(source, extraction, ct); }
        catch (BosException ex) when (ex.Code is not ("DEPENDENCY_UNAVAILABLE" or "INVALID_BACKUP")) { throw InvalidBackup(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException or System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidBackup(); }
    }

    private static async Task VerifyBundleCoreAsync(string source, string? extraction, CancellationToken ct)
    {
        await using var archive = File.OpenRead(source);
        await PreflightArchiveAsync(archive, ct);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is < 3 or > MaximumEntries + 1) throw InvalidBackup();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ValidateEntryPath(entry.FullName);
            if (!paths.Add(entry.FullName) || entry.Length > MaximumBundleBytes || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw InvalidBackup();
        }
        var metadata = zip.GetEntry("manifest.json") ?? throw InvalidBackup();
        if (metadata.Length > 256 * 1024) throw InvalidBackup();
        JsonObject manifest;
        await using (var input = metadata.Open())
        {
            // Entry.Length is attacker-controlled. Read at most its bounded length plus one,
            // rather than allowing a dishonest compressed manifest to expand in ReadToEnd.
            var bytes = new byte[(int)metadata.Length + 1];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await input.ReadAsync(bytes.AsMemory(read), ct);
                if (count == 0) break;
                read += count;
            }
            if (read != metadata.Length) throw InvalidBackup();
            manifest = CanonicalJson.Parse(new UTF8Encoding(false, true).GetString(bytes, 0, read)) as JsonObject ?? throw InvalidBackup();
        }
        JsonArgs.Only(manifest, "format", "engineVersion", "schemaVersion", "createdAt", "entries");
        if (JsonArgs.String(manifest, "format") != "bos.backup/1" || JsonArgs.Int(manifest, "engineVersion") != 1)
            throw new BosException("DEPENDENCY_UNAVAILABLE", "Backup engine version is not supported.");
        var schemaVersion = JsonArgs.Int(manifest, "schemaVersion");
        if (schemaVersion is < 0 or > 2) throw new BosException("DEPENDENCY_UNAVAILABLE", "Backup database schema is not supported.");
        var entries = manifest["entries"] as JsonArray ?? throw InvalidBackup();
        if (entries.Count != zip.Entries.Count - 1) throw InvalidBackup();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var node in entries)
        {
            ct.ThrowIfCancellationRequested();
            var item = node as JsonObject ?? throw InvalidBackup();
            JsonArgs.Only(item, "path", "size", "sha256");
            var path = JsonArgs.String(item, "path");
            ValidateEntryPath(path);
            if (new[] { "manifest.json", "workspace.lock", "bos.db-wal", "bos.db-shm" }.Contains(path, StringComparer.OrdinalIgnoreCase)
                || path.EndsWith(".secret", StringComparison.OrdinalIgnoreCase) || !declared.Add(path)) throw InvalidBackup();
            var entry = zip.GetEntry(path) ?? throw InvalidBackup();
            var size = item["size"]?.GetValue<long>() ?? throw InvalidBackup();
            if (size < 0 || entry.Length != size || (total = checked(total + size)) > MaximumBundleBytes) throw InvalidBackup();
            await using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = extraction is null ? null : OpenExtraction(extraction, path);
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                if ((read += count) > size) throw InvalidBackup();
                hash.AppendData(buffer, 0, count);
                if (output is not null) await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (read != size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), JsonArgs.String(item, "sha256"), StringComparison.Ordinal)) throw InvalidBackup();
        }
        if (!declared.Contains("bos.db") || !declared.Contains("bos.json") || !declared.Contains("cursor.key")) throw InvalidBackup();
        if (extraction is not null && await CheckDatabaseAsync(Path.Combine(extraction, "bos.db"), ct) != schemaVersion) throw InvalidBackup();
    }

    private static async Task PreflightArchiveAsync(Stream archive, CancellationToken ct)
    {
        // ZipArchive materializes the central directory before exposing Entries.Count.
        // Bound its actual records first, using the same handle later used for extraction.
        // This format's <4 GiB / <65535-entry limits need neither ZIP64 nor split archives.
        if (archive.Length is < 22 or > MaximumArchiveBytes) throw InvalidBackup();
        var tail = new byte[(int)Math.Min(archive.Length, 22 + ushort.MaxValue)];
        archive.Seek(-tail.Length, SeekOrigin.End);
        await archive.ReadExactlyAsync(tail, ct);
        var end = tail.Length - 22;
        while (end >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end, 4)) != 0x06054b50) end--;
        if (end < 0 || end + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 20, 2)) != tail.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4, 2)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6, 2)) != 0) throw InvalidBackup();
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12, 4));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16, 4));
        if (count is < 3 or > MaximumEntries + 1 || count != BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8, 2))
            || size > MaximumDirectoryBytes || (long)offset + size != archive.Length - tail.Length + end) throw InvalidBackup();
        archive.Position = offset;
        var header = new byte[46];
        for (var i = 0; i < count; i++)
        {
            if (archive.Position + header.Length > (long)offset + size) throw InvalidBackup();
            await archive.ReadExactlyAsync(header, ct);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50 || nameLength is 0 or > 4096
                || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2)) != 0
                || archive.Position + nameLength + extraLength + commentLength > (long)offset + size) throw InvalidBackup();
            archive.Seek(nameLength + extraLength + commentLength, SeekOrigin.Current);
        }
        if (archive.Position != (long)offset + size) throw InvalidBackup();
        archive.Position = 0;
    }

    public static async Task<int> CheckDatabaseAsync(string path, CancellationToken ct = default)
    {
        try { return await CheckDatabaseCoreAsync(path, ct); }
        catch (SqliteException) { throw InvalidBackup(); }
    }

    private static async Task<int> CheckDatabaseCoreAsync(string path, CancellationToken ct)
    {
        RequireRegularFile(path);
        await using var db = Connection(path, SqliteOpenMode.ReadOnly);
        await db.OpenAsync(ct);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (version is < 0 or > 2) throw new BosException("DEPENDENCY_UNAVAILABLE", "Database schema is newer than this engine supports.");
        command.CommandText = "PRAGMA integrity_check";
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct)) throw InvalidBackup();
        }
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) throw InvalidBackup();
        command.CommandText = "SELECT collection,key,json FROM Documents LIMIT 0; SELECT id,type,typeVersion,revision,deleted,json FROM Records LIMIT 0";
        await command.ExecuteNonQueryAsync(ct);
        if (version >= 2)
        {
            command.CommandText = "SELECT id,recordId,type,revision,deleted,state,expiresAt,ownerId,commandKey,inputHash,json,content FROM Attachments LIMIT 0";
            await command.ExecuteNonQueryAsync(ct);
        }
        return version;
    }

    private static async Task InvalidateRestoredIdentityAsync(string database, CancellationToken ct)
    {
        await using var db = Connection(database, SqliteOpenMode.ReadWrite);
        await db.OpenAsync(ct);
        await using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Documents(collection,key,json) VALUES('runtimeMetadata','cursorKey',$cursorKey)
              ON CONFLICT(collection,key) DO UPDATE SET json=excluded.json;
            UPDATE Documents SET json=json_set(json,'$.revokedAt',$now) WHERE collection='tokens';
            UPDATE Documents SET json=json_set(json,'$.securityStamp',lower(hex(randomblob(32))),'$.concurrencyStamp',lower(hex(randomblob(32)))) WHERE collection='users';
            DELETE FROM Documents WHERE collection='sessions';
            UPDATE Documents SET json=json_set(json,'$.status','interrupted','$.completedAt',$now,'$.error',json($error))
              WHERE collection='runs' AND lower(json_extract(json,'$.status'))='running';
            UPDATE Documents AS receipt SET json=json_set(receipt.json,'$.result',json((
              SELECT json_remove(run.json,'$.receiptKey') FROM Documents AS run
              WHERE run.collection='runs' AND run.key=json_extract(receipt.json,'$.result.runId'))),'$.status',200)
              WHERE receipt.collection='receipts' AND json_extract(receipt.json,'$.result.status')='running'
              AND EXISTS(SELECT 1 FROM Documents AS run WHERE run.collection='runs'
                AND run.key=json_extract(receipt.json,'$.result.runId') AND json_extract(run.json,'$.status')='interrupted');
            """;
        // Identity bootstrap preserves existing material. Replace the snapshot's catalogue
        // key before recovery, alongside revocations, rather than preserving old cursors.
        command.Parameters.AddWithValue("$cursorKey", CanonicalJson.Serialize(new JsonObject
        {
            ["id"] = "cursorKey", ["value"] = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))
        }));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        command.Parameters.AddWithValue("$error", "{\"code\":\"RUN_INTERRUPTED\",\"message\":\"Run was interrupted before restore.\",\"retryable\":false}");
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static SqliteConnection Connection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 1, ForeignKeys = true }.ToString());
    private static async Task AppendOperatorAuditAsync(string directory, string method, CancellationToken ct)
    {
        var factory = new SqliteUnitOfWorkFactory(Path.Combine(directory, "bos.db"));
        await using var uow = await factory.BeginWriteAsync(ct);
        await uow.Documents.AppendAuditAsync(new JsonObject
        {
            ["actor"] = "operator", ["method"] = method, ["at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["outcome"] = "success"
        }, ct);
        await uow.CommitAsync(ct);
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
    private static async Task<long> CopyPrivateAsync(string source, string destination, long maximumBytes, CancellationToken ct)
    {
        if (new FileInfo(source).Length > maximumBytes) throw new BosException("CAPACITY_EXCEEDED", "Backup size limit exceeded.");
        await using var input = File.OpenRead(source);
        await using var output = OpenPrivate(destination, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if ((copied += count) > maximumBytes) throw new BosException("CAPACITY_EXCEEDED", "Backup size limit exceeded.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return copied;
    }
    private static FileStream OpenExtraction(string root, string relative)
    {
        var path = SafePath(root, relative);
        CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        return OpenPrivate(path, FileMode.CreateNew, FileAccess.Write);
    }
    private static FileStream OpenPrivate(string path, FileMode mode, FileAccess access, FileShare share = FileShare.None)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Missing local owner identity.");
            var acl = new FileSecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(mode, FileSystemRights.FullControl, share, 4096, FileOptions.None, acl);
        }
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        PrivateFile(path);
        return stream;
    }
    public static void CreatePrivateDirectory(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Missing local owner identity.");
            var acl = new DirectorySecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        else
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, mode);
            File.SetUnixFileMode(path, mode);
        }
    }
    private static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Missing local owner identity.");
            var acl = new FileSecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
    }
    private static IEnumerable<string> EnumerateProtectedFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            RejectLink(entry);
            if (Directory.Exists(entry))
                foreach (var child in EnumerateProtectedFiles(entry)) yield return child;
            else { RequireRegularFile(entry); yield return entry; }
        }
    }
    private static void RejectLink(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || (File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new BosException("INVALID_REQUEST", "Symlinks are not permitted in protected workspace paths.", 400);
    }
    private static void RequireRegularFile(string path)
    {
        RejectLink(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new BosException("INVALID_REQUEST", "Required workspace file is missing.", 400);
    }
    private static void ValidateEntryPath(string path)
    {
        if (path.Length is 0 or > 1024 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Any(char.IsControl)
            || path.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ') || x.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || ReservedDevice(x))) throw InvalidBackup();
    }
    private static bool ReservedDevice(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && (name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
    private static string SafePath(string root, string relative)
    {
        ValidateEntryPath(relative);
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(root, result)) throw InvalidBackup();
        return result;
    }
    private static bool Within(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static BosException InvalidBackup() => new("INVALID_BACKUP", "Backup manifest, path, hash or database integrity is invalid.", 400);
}
