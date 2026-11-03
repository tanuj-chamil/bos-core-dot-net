using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Bos.Core;
using Bos.Sqlite;

namespace Bos.Host;

/// <summary>Strict host configuration. Rate windows are fixed, one minute, with no queue.</summary>
public sealed class HostOptions
{
    public string DataDirectory { get; private set; } = Path.GetFullPath("bos-data");
    public string Listen { get; private set; } = "https://localhost:8080";
    public string WorkspaceTimezone { get; private set; } = "Asia/Colombo";
    public string[] AllowedOrigins { get; private set; } = [];
    public string? HttpsCertificatePath { get; private set; }
    public string? HttpsPasswordFile { get; private set; }
    public int RequestBodyBytes { get; private set; } = 256 * 1024;
    public int ExecutionMilliseconds { get; private set; } = 2000;
    public int ReadRequestsPerMinute { get; private set; } = 120;
    public int WriteRequestsPerMinute { get; private set; } = 60;
    // Q07: ten attempts per socket IP per fixed minute, irrespective of username/outcome.
    // Forwarded headers are deliberately not enabled: no proxy is implicitly trusted.
    public int LoginAttemptsPerMinute { get; private set; } = 10;

    public static HostOptions ParseServe(string[] args, string? dataDirectory = null)
    {
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--data-dir" or "--listen") || i + 1 == args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !supplied.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("serve accepts --data-dir and --listen, each once with a value.");
        }
        var result = new HostOptions();
        result.DataDirectory = Path.GetFullPath(dataDirectory ?? supplied.GetValueOrDefault("--data-dir") ?? "bos-data");
        if (dataDirectory is not null && supplied.TryGetValue("--data-dir", out var requested) &&
            !string.Equals(result.DataDirectory, Path.GetFullPath(requested), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Conflicting data directories.");

        var configPath = Path.Combine(result.DataDirectory, "bos.json");
        if (File.Exists(configPath))
        {
            if (new FileInfo(configPath).Length > 256 * 1024) throw new ArgumentException("Configuration is too large.");
            var config = CanonicalJson.Parse(File.ReadAllText(configPath, new System.Text.UTF8Encoding(false, true))) as JsonObject ?? throw new ArgumentException("Configuration must be an object.");
            JsonArgs.Only(config, "storageAdapter", "parserAdapter", "listen", "workspaceTimezone", "limits", "allowedOrigins", "https");
            if (config.ContainsKey("storageAdapter") && JsonArgs.String(config, "storageAdapter") != "sqlite")
                throw new ArgumentException("Unsupported storage adapter.");
            if (config.ContainsKey("parserAdapter") && JsonArgs.String(config, "parserAdapter") != "yamldotnet")
                throw new ArgumentException("Unsupported parser adapter.");
            if (config.ContainsKey("listen")) result.Listen = JsonArgs.String(config, "listen");
            if (config.ContainsKey("workspaceTimezone")) result.WorkspaceTimezone = JsonArgs.String(config, "workspaceTimezone");
            if (config.ContainsKey("https"))
            {
                var https = JsonArgs.Object(config, "https");
                JsonArgs.Only(https, "certificatePath", "passwordFile");
                result.HttpsCertificatePath = TlsPath(JsonArgs.String(https, "certificatePath"), result.DataDirectory);
                if (https.ContainsKey("passwordFile")) result.HttpsPasswordFile = TlsPath(JsonArgs.String(https, "passwordFile"), result.DataDirectory);
            }
            if (config.ContainsKey("allowedOrigins"))
            {
                var origins = config["allowedOrigins"] as JsonArray ?? throw new ArgumentException("Origins must be an array.");
                if (origins.Count > 32) throw new ArgumentException("Too many allowed origins.");
                result.AllowedOrigins = origins.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? ValidateOrigin(s) : throw new ArgumentException("Invalid origin.")).Distinct(StringComparer.Ordinal).ToArray();
            }
            if (config.ContainsKey("limits"))
            {
                var limits = config["limits"] as JsonObject ?? throw new ArgumentException("Limits must be an object.");
                JsonArgs.Only(limits, "requestBodyBytes", "executionMilliseconds", "readRequestsPerMinute", "writeRequestsPerMinute", "loginAttemptsPerMinute");
                result.RequestBodyBytes = Lower(limits, "requestBodyBytes", result.RequestBodyBytes);
                result.ExecutionMilliseconds = Lower(limits, "executionMilliseconds", result.ExecutionMilliseconds);
                result.ReadRequestsPerMinute = Lower(limits, "readRequestsPerMinute", result.ReadRequestsPerMinute);
                result.WriteRequestsPerMinute = Lower(limits, "writeRequestsPerMinute", result.WriteRequestsPerMinute);
                result.LoginAttemptsPerMinute = Lower(limits, "loginAttemptsPerMinute", result.LoginAttemptsPerMinute);
            }
        }
        if (supplied.TryGetValue("--listen", out var listen)) result.Listen = listen;
        ValidateListen(result.Listen);
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(result.WorkspaceTimezone); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Unknown workspace timezone."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("Invalid workspace timezone."); }
        return result;
    }

    private static int Lower(JsonObject obj, string name, int maximum)
    {
        if (!obj.TryGetPropertyValue(name, out var value)) return maximum;
        if (value is not JsonValue v || !v.TryGetValue<int>(out var n) || n < 1 || n > maximum)
            throw new ArgumentException($"Invalid limit: {name}.");
        return n;
    }

    private static string TlsPath(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("HTTPS file paths must not be empty.");
        return Path.GetFullPath(path, directory);
    }

    internal X509Certificate2 LoadHttpsCertificate()
    {
        X509Certificate2? certificate = null;
        try
        {
            var path = HttpsCertificatePath ?? throw new InvalidOperationException();
            ProtectTlsFile(path, 1024 * 1024, allowEmpty: false);
            var password = HttpsPasswordFile is { } passwordFile ? ReadTlsPassword(passwordFile) : null;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) throw new InvalidOperationException();
            return certificate;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException or InvalidOperationException or NotSupportedException)
        {
            certificate?.Dispose();
            // Native loader exceptions and password-decoding diagnostics must not enter logs.
            throw new InvalidOperationException("Configured HTTPS certificate could not be loaded. Check its files and permissions.");
        }
    }

    private static void ProtectTlsFile(string path, int maximumBytes, bool allowEmpty)
    {
        // Check every component, including ancestors of an absolute path outside the workspace.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException();
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0 || info.Length > maximumBytes || (!allowEmpty && info.Length == 0))
            throw new InvalidOperationException();
        ProtectFile(path);
    }

    private static string ReadTlsPassword(string path)
    {
        const int maximumBytes = 4096;
        ProtectTlsFile(path, maximumBytes, allowEmpty: true);
        var bytes = new byte[maximumBytes + 1];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes.AsSpan(count));
                if (read == 0) break;
                count += read;
            }
            if (count > maximumBytes) throw new InvalidOperationException();
            var offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            var password = new System.Text.UTF8Encoding(false, true).GetString(bytes, offset, count - offset);
            if (password.EndsWith("\r\n", StringComparison.Ordinal)) password = password[..^2];
            else if (password.EndsWith('\n')) password = password[..^1];
            if (password.Contains('\r') || password.Contains('\n') || password.Contains('\0')) throw new InvalidOperationException();
            return password; // Preserve spaces and Unicode exactly; no trimming or normalization.
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool Loopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    private static void ValidateListen(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.Port < 1 || uri.Host.Contains('*') || uri.Host.Contains('+'))
            throw new ArgumentException("Invalid listener URL.");
        if (uri.Scheme == "http" && !Loopback(uri.Host))
            throw new ArgumentException("A non-loopback listener requires HTTPS.");
    }

    private static string ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || value != uri.GetLeftPart(UriPartial.Authority) ||
            uri.Scheme == "http" && !Loopback(uri.Host)) throw new ArgumentException("Invalid allowed origin.");
        return value;
    }

    internal static void ProtectDirectory(string path)
    {
        RejectLink(path);
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Workspace owner is unavailable.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void ProtectFile(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Workspace owner is unavailable.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Protected workspace paths must not be symbolic links.");
    }

    internal static byte[] CursorKey(string directory)
    {
        var path = Path.Combine(directory, "cursor.key");
        if (!File.Exists(path))
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            stream.Write(key);
            stream.Flush(true);
        }
        ProtectFile(path);
        if (new FileInfo(path).Length != 32) throw new InvalidOperationException("Invalid cursor key.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32) throw new InvalidOperationException("Invalid cursor key.");
        return bytes;
    }
}

/// <summary>Shared stopped-host/serve ownership: hold this OS lock until shutdown completes.</summary>
public sealed class WorkspaceLease : IDisposable
{
    private readonly FileStream stream;
    private bool disposed;

    public WorkspaceLease(string dataDirectory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Workspace locking requires Linux or Windows.");
        HostOptions.ProtectDirectory(dataDirectory);
        stream = SqliteBackupAdapter.AcquireWorkspaceLock(dataDirectory);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }
}
