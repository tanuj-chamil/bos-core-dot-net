using System.Data;
using System.Data.Common;
using Bos.Contracts;
using Microsoft.Data.Sqlite;

namespace Bos.Sqlite;

public sealed class SqliteUnitOfWorkFactory : IUnitOfWorkFactory, IStorageDiagnostics
{
    private const int SchemaVersion = 1;
    public string DatabasePath { get; }
    private readonly string connectionString;
    private int activeReaders;
    private int activeWriters;
    public int ActiveReaders => Volatile.Read(ref activeReaders);
    public int ActiveWriters => Volatile.Read(ref activeWriters);

    public SqliteUnitOfWorkFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 1
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        // Inspect existing stores without enabling WAL or otherwise changing their contents.
        if (File.Exists(DatabasePath))
        {
            // A normal read-only WAL open can create sidecars. Inspect the standalone file without
            // locking first, then read any existing WAL normally so its committed version is not ignored.
            await PreflightAsync(immutable: true, ct);
            if (File.Exists(DatabasePath + "-wal")) await PreflightAsync(immutable: false, ct);
        }
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=OFF; PRAGMA busy_timeout=1000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA read_uncommitted=OFF;";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync();
            throw SqliteErrors.Translate(ex);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task PreflightAsync(bool immutable, CancellationToken ct)
    {
        await using var preflight = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = immutable ? new Uri(DatabasePath).AbsoluteUri + "?immutable=1" : DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            await preflight.OpenAsync(ct);
            await CheckSchemaVersionAsync(preflight, null, ct);
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    private static async Task CheckSchemaVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        if (version < 0 || version > SchemaVersion)
            throw new BosException("DEPENDENCY_UNAVAILABLE", "The database schema version is not supported by this engine.", 503);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        try
        {
            // The non-deferred provider transaction emits BEGIN IMMEDIATE.
            await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            await CheckSchemaVersionAsync(connection, transaction, ct);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Documents(
                    collection TEXT NOT NULL, key TEXT NOT NULL, json TEXT NOT NULL CHECK(json_valid(json)),
                    PRIMARY KEY(collection,key));
                CREATE TABLE IF NOT EXISTS TypeVersions(
                    name TEXT NOT NULL, version INTEGER NOT NULL CHECK(version>0),
                    PRIMARY KEY(name,version));
                CREATE TABLE IF NOT EXISTS Records(
                    id TEXT PRIMARY KEY NOT NULL, type TEXT NOT NULL, typeVersion INTEGER NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision>0), deleted INTEGER NOT NULL CHECK(deleted IN (0,1)),
                    json TEXT NOT NULL CHECK(json_valid(json)),
                    FOREIGN KEY(type,typeVersion) REFERENCES TypeVersions(name,version));
                CREATE INDEX IF NOT EXISTS IX_Records_Type ON Records(type,deleted,id);
                CREATE TABLE IF NOT EXISTS IndexValues(
                    recordId TEXT NOT NULL, type TEXT NOT NULL, field TEXT NOT NULL,
                    state INTEGER NOT NULL CHECK(state IN (0,1,2)), kind TEXT NOT NULL,
                    value TEXT, orderKey TEXT COLLATE BINARY,
                    PRIMARY KEY(recordId,field), FOREIGN KEY(recordId) REFERENCES Records(id));
                CREATE INDEX IF NOT EXISTS IX_IndexValues_Lookup ON IndexValues(type,field,state,orderKey,recordId);
                CREATE TABLE IF NOT EXISTS UniqueValues(
                    type TEXT NOT NULL, field TEXT NOT NULL, value TEXT NOT NULL, recordId TEXT NOT NULL,
                    PRIMARY KEY(type,field,value), FOREIGN KEY(recordId) REFERENCES Records(id));
                CREATE INDEX IF NOT EXISTS IX_UniqueValues_Record ON UniqueValues(recordId);
                CREATE TABLE IF NOT EXISTS RecordReferences(
                    sourceId TEXT NOT NULL, targetId TEXT NOT NULL, path TEXT NOT NULL,
                    PRIMARY KEY(sourceId,path), FOREIGN KEY(sourceId) REFERENCES Records(id),
                    FOREIGN KEY(targetId) REFERENCES Records(id));
                CREATE INDEX IF NOT EXISTS IX_RecordReferences_Target ON RecordReferences(targetId,sourceId);
                CREATE TABLE IF NOT EXISTS RecordHistory(
                    id TEXT NOT NULL, revision INTEGER NOT NULL, json TEXT NOT NULL CHECK(json_valid(json)),
                    commandId TEXT NOT NULL, runId TEXT,
                    PRIMARY KEY(id,revision), FOREIGN KEY(id) REFERENCES Records(id));
                CREATE TABLE IF NOT EXISTS Audit(
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, json TEXT NOT NULL CHECK(json_valid(json)));
                PRAGMA user_version=1;
                """;
            // Version 0 is the unreleased first-preview layout. Adoption into schema 1 is not a
            // historical released-schema migration; all DDL and the version stamp commit together.
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public Task<IUnitOfWork> BeginReadAsync(CancellationToken ct = default) => BeginAsync(false, ct);
    public Task<IUnitOfWork> BeginWriteAsync(CancellationToken ct = default) => BeginAsync(true, ct);

    private async Task<IUnitOfWork> BeginAsync(bool write, CancellationToken ct)
    {
        var connection = await OpenAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!write)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA query_only=ON;";
                await command.ExecuteNonQueryAsync(ct);
            }
            var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: !write);
            ct.ThrowIfCancellationRequested();
            var uow = new SqliteUnitOfWork(connection, transaction, write, () => Release(write));
            if (write) Interlocked.Increment(ref activeWriters);
            else Interlocked.Increment(ref activeReaders);
            return uow;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync();
            throw SqliteErrors.Translate(ex);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void Release(bool write)
    {
        if (write) Interlocked.Decrement(ref activeWriters);
        else Interlocked.Decrement(ref activeReaders);
    }
}

internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly Action release;
    private bool completed;
    private bool disposed;
    private int disposeStarted;
    internal bool Poisoned { get; set; }
    internal HashSet<InvocationContext> Contexts { get; } = [];
    public DbConnection Connection => connection;
    public DbTransaction Transaction => transaction;
    public bool IsWrite { get; }
    public IDocumentRepository Documents { get; }
    public IRecordRepository Records { get; }

    internal SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction, bool write, Action release)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.release = release;
        IsWrite = write;
        Documents = new SqliteDocumentRepository(this);
        Records = new SqliteRecordRepository(this);
    }

    internal SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("The transaction has completed.");
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 1;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal void RequireWrite()
    {
        if (!IsWrite) throw new BosException("POLICY_VIOLATION", "A read-only transaction cannot mutate state.", 403);
        if (Poisoned) throw new BosException("INVALID_STATE", "The transaction has failed and must be rolled back.", 409);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("The transaction has completed.");
        if (Poisoned || Contexts.Any(x => x.Poisoned))
            throw new BosException("INVALID_STATE", "The transaction has failed and must be rolled back.", 409);
        try { await transaction.CommitAsync(ct); completed = true; }
        catch (SqliteException ex) { Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { Poisoned = true; throw; }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return;
        await transaction.RollbackAsync(ct);
        completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        disposed = true;
        try
        {
            if (!completed) await transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                try { await transaction.DisposeAsync(); }
                finally { await connection.DisposeAsync(); }
            }
            finally { release(); }
        }
    }
}

internal static class SqliteErrors
{
    internal static Exception Translate(SqliteException ex) => ex.SqliteErrorCode switch
    {
        5 or 6 => new BosException("STORAGE_BUSY", "Storage is busy; retry with the original command key.", 503, retryable: true),
        13 => new BosException("CAPACITY_EXCEEDED", "Storage capacity has been exceeded.", 503),
        _ => new BosException("INTERNAL_ERROR", "The storage operation failed.", 500)
    };
}
