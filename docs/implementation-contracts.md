# Implementation contract freeze — first API build

The C# ports in `src/Bos.Contracts/Contracts.cs` are the shared implementation boundary. They supplement, and do not relax, specification 1.0.0. All JSON returned across boundaries must be cloned or treated as immutable; interpreters never expose writable record objects.

## Shared conventions

- `JsonObject` transport keys use exact camelCase. Dates/instants serialize canonically. Business integers/decimals are strings; metadata revisions/versions are JSON numbers.
- `BosException` is the public typed failure. Repositories never commit. Root invocation owns cancellation, authorization snapshot, transaction and receipt.
- `IDocumentRepository` stores bounded documents in named collections, keyed by a canonical string; values contain their own public IDs/names. `ListAsync` MUST fail rather than silently truncate when its bound is exceeded.
- Collections: `typeDrafts` key name; `typeVersions` key `name:version`; `typeActive` key name with `{name,version}`; `flowDrafts`, `flowVersions`, `flowActive` similarly; `flowControl` key name with `{name,revision,disabled}`; `users`, `roles`, `tokens` key id; `runs` key runId; `receipts` key SHA256 of principal/method/target/key, value `{principalId,scope,key,inputHash,result,status}`; `runSteps` key runId with `{runId,items:[...]}`. Tokens store secret hashes, never plaintext.
- Type/flow drafts retain definition/source and draftRevision. Published envelopes match specification §§33–34; flowVersions also stores internal `ast` for execution, never returned publicly without filtering.
- Record repository uses `Records(id,type,typeVersion,revision,deleted,json)` with full canonical envelope in `json`; derived `IndexValues(recordId,type,field,state,kind,value,orderKey)`, `UniqueValues(type,field,value,recordId)` and `RecordReferences(sourceId,targetId,path)` are maintained atomically. Index states: missing=0, null=1, present=2. `orderKey` is an ordinal-sortable encoding supplied by `Bos.Core.TypedIndex.Key`. Full record history and audit are written by SaveAsync.
- `Schema` in Bos.Core exposes `ValidateDefinition(JsonObject,bool dataSchema=false)`, `Normalize(JsonObject schema,JsonObject data,bool applyDefaults=true)`, `ValidateData(JsonObject,JsonObject,bool=true)`, `CheckEvolution(JsonObject old,JsonObject next)`. Validation reports structural errors; Normalize throws a typed validation error. `CanonicalJson` exposes `Parse(string)`, `Serialize(JsonNode?)`, `Hash(JsonNode?)` and `Normalize(JsonNode?)`. Strict Parse rejects duplicates/invalid Unicode/oversize/deep input.
- `JsonArgs` in Bos.Core exposes strict argument helpers `Only(JsonObject,params string[])`, `String`, `Int`, `Object`, `Array`, `Bool`, `Name`, `Uuid`. Optional helpers accept a default argument; explicit null is never omission.
- `JsonArgs.Secret` preserves password code points. Strict request parsing opts into secret/source preservation; normal business codecs still enforce NFC. Document storage preserves original YAML source bytes separately from canonical AST hashes.
- `OperationDescriptor.MetadataPaths` explicitly identifies numeric metadata paths such as `input.expectedRevision` and `output.lines[].revision`. Other Integer fields remain canonical business strings even when named `version` or `revision`. Built-in record contracts cannot be replaced or assigned an unsupported implementation version by registration.
- Only the dispatcher charges SDK calls. Budget failures are inside its transaction-poisoning boundary; catching them in trusted code cannot permit a commit.
- `IQueryEngine.CatalogueValuesAsync` applies the authenticated keyset contract to bounded registry/step metadata. `InvocationContext.CursorScope` binds parent-specific catalogues; `IStorageDiagnostics` supplies actual active-unit-of-work counts.
- Explicit `log` instructions persist bounded scalar metadata as optional `StepResult.log` only with terminal run status. Current run-result permissions gate log disclosure along with other step data.
- Record service is `RecordService(IAuthorization,IQueryEngine,IClock,IIdGenerator)` with `InvokeAsync(method,args,context,uow,ct)`.
- Application API is `ApplicationApi(IUnitOfWorkFactory,IAuthorization,IIdentityService,IQueryEngine,IOperationRegistry,IFlowCompiler,IFlowRunner,IClock,IIdGenerator)` implementing IApplicationApi/IDispatcher. Operation registry is `OperationRegistry`; reference registration is `MaterialRequestOperations.Register(IOperationRegistry)`.
- Flow compiler/interpreter are `FlowCompiler(IOperationRegistry)` and `FlowInterpreter(IOperationRegistry)`; compiler syntax validation is pure. Application publication additionally resolves stored type dependencies. `FlowRunner(IUnitOfWorkFactory,IAuthorization,IFlowInterpreter,IOperationRegistry,IClock,IIdGenerator)` obtains its dispatcher via `Dispatcher` property set by composition after ApplicationApi creation. Runs retain sanitized receipts; success stored in business transaction.
- SQLite factory is `SqliteUnitOfWorkFactory(string databasePath)`; query adapter `SqliteQueryEngine(byte[] cursorKey)`; backup adapter `SqliteBackupAdapter(string dataDirectory)`.
- Identity implementation is `IdentityService(IUnitOfWorkFactory,IClock,IIdGenerator)` and `Authorization` implements IAuthorization. All identity mutations enlist in the supplied UoW using ASP.NET Core Identity managers/stores.

## Build coordination

The initial implementation uses disjoint file ownership in one working tree. No integration commits or separate worktrees are created automatically. Shared signatures are coordinated by the integrator. This is a workflow exception to isolated worktrees, not an exception to transaction, security or release behavior.

Material public/security ambiguities in decisions.md remain explicit; affected behavior must be settled before claiming conformance. The build is a review build until the required evidence exists.
