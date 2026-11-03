using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Bos.Identity;
using Bos.Sqlite;

namespace Bos.Host;

/// <summary>Stopped-host operator commands. Passwords are generated into protected files, never arguments or stdout.</summary>
public static class OperatorCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Expected init, doctor, backup, restore, reset-admin, or version.");
            var command = args[0];
            var allowed = command switch
            {
                "init" => new[] { "--data-dir", "--bootstrap-file" },
                "doctor" => ["--data-dir"],
                "backup" => ["--data-dir", "--output"],
                "restore" => ["--data-dir", "--input"],
                "reset-admin" => ["--data-dir", "--secret-file"],
                "version" => [],
                _ => throw new ArgumentException("Unknown operator command.")
            };
            var options = Parse(args[1..], allowed);
            if (command == "version")
            {
                Console.WriteLine($"BOS {typeof(OperatorCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0-preview.1"}; specification 1.0.0; backup engine 1");
                return 0;
            }
            var directory = Path.GetFullPath(options.GetValueOrDefault("--data-dir") ?? "bos-data");
            switch (command)
            {
                case "init": await InitializeAsync(directory, RequiredPath(options, "--bootstrap-file")); break;
                case "doctor": await DoctorAsync(directory); break;
                case "backup":
                    await new SqliteBackupAdapter(directory).SnapshotAsync(RequiredPath(options, "--output"));
                    Console.WriteLine("Verified offline backup created.");
                    break;
                case "restore":
                {
                    var recovered = await new SqliteBackupAdapter(directory).RestoreWithRecoveryAsync(RequiredPath(options, "--input"), directory);
                    _ = HostOptions.ParseServe([], directory);
                    Console.WriteLine($"Workspace restored; administrator {JsonArgs.String(recovered, "userName")} recovered. Secret file: {Path.Combine(directory, SqliteBackupAdapter.RecoverySecretName)}");
                    break;
                }
                case "reset-admin":
                    await ResetAdministratorAsync(directory, RequiredPath(options, "--secret-file"));
                    break;
            }
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: bos init --data-dir DIR --bootstrap-file FILE | doctor --data-dir DIR | backup --data-dir DIR --output FILE | restore --input FILE --data-dir EMPTY_DIR | reset-admin --data-dir DIR --secret-file FILE | version");
            return 2;
        }
        catch (BosException ex)
        {
            Console.Error.WriteLine($"{ex.Code}: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Data.Common.DbException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OverflowException or TypeInitializationException or DllNotFoundException or BadImageFormatException)
        {
            // Provider and filesystem diagnostics are not a credential disclosure channel.
            Console.Error.WriteLine("Operator command failed. Check the workspace, file permissions, backup integrity, and stopped-host ownership.");
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || i + 1 == args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, duplicate, or incomplete option. Credentials are not CLI options.");
        }
        return result;
    }
    private static string RequiredPath(Dictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) ? Path.GetFullPath(value) : throw new ArgumentException($"Missing {key}.");

    private static async Task InitializeAsync(string directory, string secretFile)
    {
        CheckNewSecretPath(secretFile);
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new BosException("INVALID_STATE", "Initialization requires an empty workspace.");
        // Create the workspace before taking the common lease, but inspect it again while locked.
        SqliteBackupAdapter.CreatePrivateDirectory(directory);
        HostOptions.ProtectDirectory(directory);
        using var workspace = SqliteBackupAdapter.AcquireWorkspaceLock(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any(x => Path.GetFileName(x) != "workspace.lock"))
            throw new BosException("INVALID_STATE", "Initialization requires an empty workspace.");
        var config = new JsonObject
        {
            ["storageAdapter"] = "sqlite", ["parserAdapter"] = "yamldotnet", ["listen"] = "https://localhost:8080",
            ["workspaceTimezone"] = "Asia/Colombo", ["allowedOrigins"] = new JsonArray(), ["limits"] = new JsonObject()
        };
        await SqliteBackupAdapter.WritePrivateAsync(Path.Combine(directory, "bos.json"), Encoding.UTF8.GetBytes(CanonicalJson.Serialize(config) + "\n"));
        _ = HostOptions.ParseServe([], directory);
        SqliteBackupAdapter.CreatePrivateDirectory(Path.Combine(directory, "keys"));
        HostOptions.ProtectDirectory(Path.Combine(directory, "keys"));
        _ = HostOptions.CursorKey(directory);
        var factory = Factory(directory);
        await factory.InitializeAsync();
        var user = await SetAdministratorAsync(factory, secretFile, "system.init");
        Console.WriteLine($"Workspace initialized; administrator {JsonArgs.String(user, "userName")}. Bootstrap secret file: {secretFile}");
    }

    private static async Task ResetAdministratorAsync(string directory, string secretFile)
    {
        CheckNewSecretPath(secretFile);
        RequireWorkspace(directory);
        using var workspace = SqliteBackupAdapter.AcquireWorkspaceLock(directory);
        _ = HostOptions.ParseServe([], directory);
        await SqliteBackupAdapter.CheckDatabaseAsync(Path.Combine(directory, "bos.db"));
        var user = await SetAdministratorAsync(Factory(directory), secretFile, "system.resetAdmin");
        Console.WriteLine($"Administrator {JsonArgs.String(user, "userName")} recovered. Secret file: {secretFile}");
    }

    private static async Task<JsonObject> SetAdministratorAsync(SqliteUnitOfWorkFactory factory, string secretFile, string method)
    {
        var secret = SqliteBackupAdapter.NewAdminSecret();
        await SqliteBackupAdapter.WritePrivateAsync(secretFile, Encoding.UTF8.GetBytes(secret + "\n"));
        var commitAttempted = false;
        try
        {
            await using var uow = await factory.BeginWriteAsync();
            var user = await Identity(factory).BootstrapAsync(uow, secret);
            await OperatorAuditAsync(uow, method);
            commitAttempted = true;
            await uow.CommitAsync();
            return user;
        }
        catch
        {
            // An uncertain commit may have installed this password. Preserve its only disclosure.
            if (!commitAttempted) File.Delete(secretFile);
            throw;
        }
    }

    private static async Task DoctorAsync(string directory)
    {
        RequireWorkspace(directory);
        using var workspace = SqliteBackupAdapter.AcquireWorkspaceLock(directory);
        _ = HostOptions.ParseServe([], directory);
        var schema = await SqliteBackupAdapter.CheckDatabaseAsync(Path.Combine(directory, "bos.db"));
        var cursorPath = Path.Combine(directory, "cursor.key");
        if (!File.Exists(cursorPath) || new FileInfo(cursorPath).Length != 32) throw new BosException("INVALID_STATE", "Cursor key is missing or invalid.");
        CheckPrivate(cursorPath);
        if (!Directory.Exists(Path.Combine(directory, "keys"))) throw new BosException("INVALID_STATE", "Data Protection key directory is missing.");
        CheckPrivate(Path.Combine(directory, "keys"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "keys"))) CheckPrivate(file);
        var bytes = new[] { "bos.db", "bos.db-wal", "bos.db-shm" }.Select(x => Path.Combine(directory, x)).Where(File.Exists).Sum(x => new FileInfo(x).Length);
        var free = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
        if (bytes > 2L * 1024 * 1024 * 1024 || free < 512L * 1024 * 1024)
            throw new BosException("CAPACITY_EXCEEDED", "Workspace exceeds the pilot storage budget or free-space reserve.");
        Console.WriteLine($"Workspace checks passed; SQLite integrity ok; schema {schema}; database/WAL bytes {bytes}; free bytes {free}.");
    }

    private static SqliteUnitOfWorkFactory Factory(string directory) => new(Path.Combine(directory, "bos.db"));
    private static IdentityService Identity(IUnitOfWorkFactory factory) => new(factory, new Bos.Contracts.SystemClock(), new UuidGenerator());
    private static Task OperatorAuditAsync(IUnitOfWork uow, string method, CancellationToken ct = default)
        => uow.Documents.AppendAuditAsync(new JsonObject { ["actor"] = "operator", ["method"] = method, ["at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["outcome"] = "success" }, ct);
    private static void RequireWorkspace(string directory)
    {
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "bos.db")) || !File.Exists(Path.Combine(directory, "bos.json")))
            throw new BosException("INVALID_STATE", "Workspace has not been initialized.");
    }
    private static void CheckNewSecretPath(string path)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Secret file parent directory must already exist.");
        if (File.Exists(path) || Directory.Exists(path)) throw new BosException("INVALID_REQUEST", "Secret file already exists; choose a new path.", 409);
    }
    private static void CheckPrivate(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BosException("INVALID_STATE", "Protected paths must not be symlinks.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new BosException("INVALID_STATE", "Protected keys must have owner-only permissions.");
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var owner = acl.GetOwner(typeof(SecurityIdentifier));
            if (owner is null || owner != WindowsIdentity.GetCurrent().User)
                throw new BosException("INVALID_STATE", "Protected keys must have owner-only permissions.");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference != owner)
                    throw new BosException("INVALID_STATE", "Protected keys must have owner-only permissions.");
        }
    }
}
