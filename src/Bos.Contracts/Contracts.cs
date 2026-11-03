using System.Data.Common;
using System.Text.Json.Nodes;

namespace Bos.Contracts;

public sealed record FieldError(string Path, string Code, string Message);
public sealed record ValidationReport(bool Valid, IReadOnlyList<FieldError> Errors);
public sealed class BosException(string code, string message, int status = 422, string path = "", bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string Path { get; } = path;
    public bool Retryable { get; } = retryable;
    public IReadOnlyList<FieldError> Details { get; init; } = [];
}
public sealed record Grant(string Action, string Resource);
public sealed record Caller(string PrincipalId, string? CredentialId = null, IReadOnlyList<Grant>? TokenScopes = null, string? SecurityStamp = null);
public sealed class InvocationContext
{
    public required Caller Caller { get; init; }
    public string PrincipalId => Caller.PrincipalId;
    public string CommandId { get; init; } = Guid.NewGuid().ToString();
    public string? RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string WorkspaceTimezone { get; init; } = "Asia/Colombo";
    public IReadOnlyList<Grant> Grants { get; set; } = [];
    public List<IReadOnlyList<Grant>> Ceilings { get; } = [];
    public string? GuardOwner { get; set; }
    public bool ReadOnly { get; set; }
    public bool Poisoned { get; set; }
    public int OperationDepth { get; set; }
    public string? StepId { get; set; }
    public string IterationPath { get; set; } = "";
    public string? CursorScope { get; set; }
    public Dictionary<string, int[]> TypeVersions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ReadTypes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ChangedRecordIds { get; } = new(StringComparer.Ordinal);
    public HashSet<string> CreatedRecordIds { get; } = new(StringComparer.Ordinal);
    public Budget Budget { get; set; } = new();
    public List<JsonObject> Steps { get; } = [];
    public List<JsonObject> Logs { get; } = [];
}
public sealed class Budget
{
    public DateTimeOffset Deadline { get; set; } = DateTimeOffset.UtcNow.AddSeconds(2);
    public int Nodes { get; private set; }
    public int Calls { get; private set; }
    public int Mutations { get; private set; }
    public int Returned { get; private set; }
    public void Check(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow > Deadline) throw new BosException("DEADLINE_EXCEEDED", "Execution deadline exceeded.");
    }
    public void Node(int count = 1) { Check(); if ((Nodes += count) > 10000) throw new BosException("EXECUTION_LIMIT", "Expression/item budget exceeded."); }
    public void Call() { Check(); if (++Calls > 200) throw new BosException("EXECUTION_LIMIT", "SDK call budget exceeded."); }
    public void Mutation() { Check(); if (++Mutations > 250) throw new BosException("EXECUTION_LIMIT", "Mutation budget exceeded."); }
    public void ReturnRecords(int count) { Check(); if ((Returned += count) > 1000) throw new BosException("EXECUTION_LIMIT", "Returned record budget exceeded."); }
}
public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IIdGenerator { string NewId(); }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public sealed class UuidGenerator : IIdGenerator { public string NewId() => Guid.NewGuid().ToString(); }

public interface IUnitOfWork : IAsyncDisposable
{
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
    bool IsWrite { get; }
    IDocumentRepository Documents { get; }
    IRecordRepository Records { get; }
    IAttachmentRepository Attachments => throw new BosException("FEATURE_NOT_AVAILABLE", "Attachments are unavailable.", 404);
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}
public interface IAttachmentRepository
{
    Task<JsonObject?> GetAsync(string id, CancellationToken ct = default);
    Task<JsonObject?> GetByCommandAsync(string ownerId, string commandKey, CancellationToken ct = default);
    Task<IReadOnlyList<JsonObject>> ListAsync(string recordId, int limit = 100, CancellationToken ct = default);
    Task<byte[]?> GetContentAsync(string id, CancellationToken ct = default);
    Task InsertAsync(JsonObject metadata, byte[] content, string commandKey, string inputHash, CancellationToken ct = default);
    Task UpdateAsync(JsonObject metadata, int expectedRevision, CancellationToken ct = default);
    Task<int> CountPendingAsync(string ownerId, CancellationToken ct = default);
    Task CleanupExpiredPendingAsync(string expiresBefore, CancellationToken ct = default);
    Task<bool> HasLinkedAsync(string recordId, CancellationToken ct = default);
    Task<bool> BelongsToRecordAsync(string id, string recordId, CancellationToken ct = default);
}
public interface IUnitOfWorkFactory
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IUnitOfWork> BeginReadAsync(CancellationToken ct = default);
    Task<IUnitOfWork> BeginWriteAsync(CancellationToken ct = default);
    string DatabasePath { get; }
}
public interface IStorageDiagnostics
{
    int ActiveReaders { get; }
    int ActiveWriters { get; }
}
public interface IDocumentRepository
{
    Task<JsonObject?> GetAsync(string collection, string key, CancellationToken ct = default);
    Task PutAsync(string collection, string key, JsonObject value, CancellationToken ct = default);
    Task DeleteAsync(string collection, string key, CancellationToken ct = default);
    Task<IReadOnlyList<JsonObject>> ListAsync(string collection, int limit = 1000, CancellationToken ct = default);
    Task AppendAuditAsync(JsonObject entry, CancellationToken ct = default);
}
public interface IRecordRepository
{
    Task<JsonObject?> GetAsync(string id, CancellationToken ct = default);
    Task<JsonObject?> GetRevisionAsync(string id, int revision, CancellationToken ct = default) => Task.FromResult<JsonObject?>(null);
    Task SaveAsync(JsonObject record, JsonObject definition, InvocationContext context, CancellationToken ct = default);
    Task<bool> HasIncomingReferencesAsync(string id, CancellationToken ct = default);
}
public interface IQueryEngine
{
    Task<JsonObject> ListAsync(IUnitOfWork uow, InvocationContext context, string type, JsonObject definition, JsonObject query, CancellationToken ct = default);
    Task<JsonObject> CountAsync(IUnitOfWork uow, InvocationContext context, string type, JsonObject definition, JsonObject filter, CancellationToken ct = default);
    Task<JsonObject> CatalogueAsync(IUnitOfWork uow, InvocationContext context, string collection, JsonObject query, string[] allowedFilters, string[] orderFields, Func<JsonObject, bool> visible, CancellationToken ct = default);
    Task<JsonObject> CatalogueValuesAsync(InvocationContext context, string scope, IReadOnlyList<JsonObject> values, JsonObject query, string[] allowedFilters, string[] orderFields, Func<JsonObject, bool> visible, CancellationToken ct = default);
    Task<JsonObject> RecordHistoryAsync(IUnitOfWork uow, InvocationContext context, string type, string id, JsonObject query, CancellationToken ct = default) =>
        throw new BosException("FEATURE_NOT_AVAILABLE", "Record history is unavailable.", 404);
}
public interface IAuthorization
{
    Task ReloadAsync(IUnitOfWork uow, InvocationContext context, CancellationToken ct = default);
    bool Can(InvocationContext context, string action, string resource);
    void Require(InvocationContext context, string action, string resource);
}
public sealed record MethodDescriptor(string Name, bool Mutating, string Grant, JsonObject InputSchema, JsonObject OutputSchema, bool FlowCallable = false, string Transport = "http");
public sealed record OperationDescriptor(string Name, int ContractVersion, string Description, JsonObject InputSchema, JsonObject OutputSchema, string Mode, bool FlowCallable, IReadOnlyList<Grant> RequiredGrants, IReadOnlyList<Grant> Capabilities, IReadOnlyList<string>? MetadataPaths = null);
public interface IOperation
{
    OperationDescriptor Descriptor { get; }
    Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct);
}
public interface IOperationRegistry
{
    IReadOnlyList<OperationDescriptor> Descriptors { get; }
    void Register(IOperation operation);
    IOperation Resolve(string name, int? version = null);
}
public interface IDispatcher
{
    Task<JsonNode?> CallAsync(string name, int? version, JsonObject input, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default);
}
public interface IApplicationApi
{
    IReadOnlyList<MethodDescriptor> Methods { get; }
    Task<ApiResult> InvokeAsync(string method, JsonObject args, Caller caller, string? key, CancellationToken ct = default);
}
public sealed record AttachmentDownload(JsonObject Metadata, byte[] Content);
public interface IAttachmentApi
{
    Task<JsonObject> UploadAttachmentAsync(string fileName, string contentType, byte[] content, Caller caller, string key, CancellationToken ct = default);
    Task<AttachmentDownload> DownloadAttachmentAsync(string id, Caller caller, CancellationToken ct = default);
}
public sealed record ApiResult(JsonNode? Result, int Status = 200);
public sealed record CompiledFlow(JsonObject Ast, string SourceHash, string AstHash);
public interface IFlowCompiler
{
    CompiledFlow Compile(string source);
}
public interface IFlowInterpreter
{
    Task<JsonNode?> ExecuteAsync(JsonObject ast, JsonObject input, InvocationContext context, IUnitOfWork uow, IDispatcher dispatcher, CancellationToken ct = default);
}
public interface IFlowRunner
{
    Task<ApiResult> RunAsync(JsonObject args, Caller caller, string key, CancellationToken ct = default);
    Task RecoverAsync(CancellationToken ct = default);
    Task<JsonObject> VisibleReceiptAsync(JsonObject receipt, InvocationContext context, CancellationToken ct = default);
}
public interface IIdentityService
{
    Task<JsonNode?> InvokeAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default);
    Task<Caller?> AuthenticateTokenAsync(string token, CancellationToken ct = default);
    Task<(Caller Caller, JsonObject User)> LoginAsync(string userName, string password, CancellationToken ct = default);
    Task ChangePasswordAsync(Caller caller, string currentPassword, string newPassword, CancellationToken ct = default);
    Task<JsonObject> BootstrapAsync(IUnitOfWork uow, string password, CancellationToken ct = default);
}
public interface IBackupAdapter
{
    Task SnapshotAsync(string destination, CancellationToken ct = default);
    Task RestoreAsync(string source, string destination, CancellationToken ct = default);
}
