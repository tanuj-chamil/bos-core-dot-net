using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Bos.Contracts;

namespace Bos.Application;

/// <summary>
/// The explicit BOS 1.0 method vocabulary. Schemas are JSON Schema 2020-12 documents,
/// not BOS data schemas. Consumers must clone or treat descriptor JSON as immutable.
/// Authorization (including alternative and resource-dependent grants) remains application-owned.
/// </summary>
public static class MethodCatalogue
{
    public static IReadOnlyList<MethodDescriptor> All { get; } = Build();

    public static IReadOnlySet<string> ReservedMethods { get; } = new[]
    {
        "types.activate", "types.archive", "types.compare",
        "records.validateUpdate", "records.find", "records.replace", "records.restore",
        "records.exists", "records.first", "records.single", "records.getMany",
        "records.aggregate", "records.distinct", "records.getRelated", "records.link", "records.unlink",
        "records.createMany", "records.updateMany", "records.deleteMany", "records.migrate",
        "flows.call", "flows.cancel", "flows.enqueue", "policies.create", "policies.update", "policies.validate",
        "policies.publish", "events.enqueue", "triggers.create", "triggers.list", "triggers.enable", "triggers.disable"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static IReadOnlyList<MethodDescriptor> Build()
    {
        var definitions = Definitions();
        var methods = new List<MethodDescriptor>();

        JsonObject Document(JsonObject schema, string method, string direction)
        {
            var root = (JsonObject)schema.DeepClone();
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            // Each document remains its own schema resource when embedded in capabilities/OpenAPI.
            root["$id"] = "urn:bos:methods:v1:" + method + ":" + direction;
            var needed = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            void Visit(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                    {
                        var name = reference[8..];
                        if (needed.Add(name)) pending.Enqueue(name);
                    }
                    foreach (var pair in obj) Visit(pair.Value);
                }
                else if (node is JsonArray array) foreach (var item in array) Visit(item);
            }
            Visit(root);
            while (pending.TryDequeue(out var name)) Visit(definitions[name]);
            if (needed.Count != 0)
            {
                var selected = new JsonObject();
                foreach (var name in needed) selected[name] = definitions[name].DeepClone();
                root["$defs"] = selected;
            }
            return root;
        }

        void Add(string name, bool mutating, string grant, JsonObject input, JsonObject output, bool flowCallable = false, string transport = "http")
            => methods.Add(new(name, mutating, grant, Document(input, name, "input"), Document(output, name, "output"), flowCallable, transport));
        JsonObject QueryArgs(string query, params (string Name, JsonObject Schema)[] arguments)
        {
            var properties = arguments.Append(("query", Default(Ref(query), new JsonObject()))).ToArray();
            return Object(properties, arguments.Select(a => a.Name).ToArray());
        }
        JsonObject NamedVersion() => Object([("name", Ref("Name")), ("version", Ref("Revision"))], "name");
        JsonObject NamedRevision() => Object([("name", Ref("Name")), ("expectedRevision", Ref("Revision"))], "name", "expectedRevision");
        JsonObject IdRevision() => Object([("id", Ref("Uuid")), ("expectedRevision", Ref("Revision"))], "id", "expectedRevision");
        JsonObject Id() => Object([("id", Ref("Uuid"))], "id");

        Add("types.create", true, "types.manage", Object([("definition", Ref("TypeDefinition"))], "definition"), Ref("TypeDraft"));
        Add("types.update", true, "types.manage", Object([("name", Ref("Name")), ("definition", Ref("TypeDefinition")), ("expectedRevision", Ref("Revision"))], "name", "definition", "expectedRevision"), Ref("TypeDraft"));
        Add("types.validate", false, "types.manage", Object([("definition", Ref("TypeDefinition"))], "definition"), Ref("ValidationReport"));
        Add("types.publish", true, "types.manage", NamedRevision(), Ref("PublishedType"));
        Add("types.get", false, "types.read", NamedVersion(), Ref("PublishedType"));
        Add("types.list", false, "types.read", QueryArgs("TypeQuery"), Page(Ref("TypeSummary")));
        Add("types.listVersions", false, "types.read", QueryArgs("VersionQuery", ("name", Ref("Name"))), Page(Ref("TypeVersionSummary")));
        Add("types.getDraft", false, "types.manage", Object([("name", Ref("Name"))], "name"), Ref("TypeDraft"));

        Add("records.create", true, "record.create", Object([("type", Ref("Name")), ("data", Ref("DataObject"))], "type", "data"), Ref("Record"), true);
        Add("records.get", false, "record.read", Object([("type", Ref("Name")), ("id", Ref("Uuid"))], "type", "id"), Ref("Record"), true);
        Add("records.update", true, "record.update", Object([("type", Ref("Name")), ("id", Ref("Uuid")), ("patch", Ref("Patch")), ("expectedRevision", Ref("Revision"))], "type", "id", "patch", "expectedRevision"), Ref("Record"), true);
        Add("records.delete", true, "record.delete", Object([("type", Ref("Name")), ("id", Ref("Uuid")), ("expectedRevision", Ref("Revision"))], "type", "id", "expectedRevision"), Ref("TombstoneReceipt"), true);
        // Validation is read-only, although authorization additionally requires create OR update.
        Add("records.validate", false, "record.read", Object([("type", Ref("Name")), ("data", Ref("DataObject")), ("typeVersion", Ref("Revision"))], "type", "data"), Ref("ValidationReport"), true);
        Add("records.list", false, "record.read", QueryArgs("RecordQuery", ("type", Ref("Name"))), Page(Union(Ref("Record"), Ref("ProjectedRecord"))), true);
        Add("records.count", false, "record.read", Object([("type", Ref("Name")), ("filter", Default(Ref("RecordCountFilter"), new JsonObject()))], "type"), Object([("count", Ref("CountString"))], "count"), true);
        Add("records.getRevision", false, "record.read", Object([("type", Ref("Name")), ("id", Ref("Uuid")), ("revision", Ref("Revision"))], "type", "id", "revision"), Ref("Record"));
        Add("records.getHistory", false, "record.read", Object([("type", Ref("Name")), ("id", Ref("Uuid")), ("query", Default(Object([("limit", Default(Integer(1, 200), JsonValue.Create(50)!)), ("cursor", Nullable(Text(16384, 1)))]), new JsonObject()))], "type", "id"), Page(Ref("Record")));
        Add("attachments.list", false, "record.read", Object([("type", Ref("Name")), ("recordId", Ref("Uuid"))], "type", "recordId"), Object([("items", Array(Ref("Attachment"), 100))], "items"));
        Add("attachments.link", true, "record.update", Object([("type", Ref("Name")), ("recordId", Ref("Uuid")), ("id", Ref("Uuid")), ("expectedRevision", Ref("Revision"))], "type", "recordId", "id", "expectedRevision"), Ref("Attachment"));
        Add("attachments.remove", true, "record.update", Object([("type", Ref("Name")), ("recordId", Ref("Uuid")), ("id", Ref("Uuid")), ("expectedRevision", Ref("Revision"))], "type", "recordId", "id", "expectedRevision"), Ref("Attachment"));

        Add("operations.list", false, "operations.execute", QueryArgs("OperationQuery"), Page(Ref("OperationDescriptor")));
        var operationArgs = new[] { ("name", Ref("OperationName")), ("contractVersion", Ref("Revision")) };
        Add("operations.get", false, "operations.execute", Object(operationArgs, "name"), Ref("OperationDescriptor"));
        var operationInput = Object(operationArgs.Append(("input", Ref("JsonObject"))).ToArray(), "name", "input");
        Add("operations.validate", false, "operations.execute", operationInput, Ref("ValidationReport"));
        // Conservative HTTP metadata: the root resolves the selected descriptor. A read operation
        // must not be forced to reserve a key or open a business write merely by this aggregate flag.
        Add("operations.call", true, "operations.execute", operationInput, Ref("JsonValue"));

        Add("flows.create", true, "flows.manage", Object([("definition", Ref("FlowDefinition"))], "definition"), Ref("FlowDraft"));
        Add("flows.update", true, "flows.manage", Object([("name", Ref("Name")), ("definition", Ref("FlowDefinition")), ("expectedRevision", Ref("Revision"))], "name", "definition", "expectedRevision"), Ref("FlowDraft"));
        Add("flows.getDraft", false, "flows.manage", Object([("name", Ref("Name"))], "name"), Ref("FlowDraft"));
        Add("flows.validate", false, "flows.manage", Object([("definition", Ref("FlowDefinition"))], "definition"), Ref("CompileReport"));
        Add("flows.publish", true, "flows.manage", NamedRevision(), Ref("PublishedFlow"));
        Add("flows.get", false, "flows.read", NamedVersion(), Ref("PublishedFlow"));
        Add("flows.list", false, "flows.read", QueryArgs("FlowQuery"), Page(Ref("FlowSummary")));
        Add("flows.listVersions", false, "flows.read", QueryArgs("VersionQuery", ("name", Ref("Name"))), Page(Ref("FlowVersionSummary")));
        Add("flows.run", true, "flows.execute", Object([("name", Ref("Name")), ("version", Ref("Revision")), ("input", Ref("DataObject"))], "name", "version", "input"), Ref("RunReceipt"));
        Add("flows.getRun", false, "", Object([("runId", Ref("Uuid"))], "runId"), Ref("RunReceipt"));
        Add("flows.listRuns", false, "", QueryArgs("RunQuery"), Page(Ref("RunSummary")));
        Add("flows.getRunSteps", false, "", QueryArgs("StepQuery", ("runId", Ref("Uuid"))), Page(Ref("StepResult"), 200));
        Add("flows.disable", true, "flows.manage", NamedRevision(), Ref("FlowControl"));
        Add("flows.enable", true, "flows.manage", NamedRevision(), Ref("FlowControl"));

        Add("identity.getCurrent", false, "", Object([]), Ref("CurrentIdentity"));
        Add("authorization.can", false, "", Object([("action", Ref("Action")), ("resource", Ref("Resource"))], "action", "resource"), Boolean());
        Add("users.create", true, "identity.manage", Object([("user", Ref("UserCreate"))], "user"), Ref("UserSummary"));
        Add("users.get", false, "identity.manage", Id(), Ref("UserSummary"));
        Add("users.list", false, "identity.manage", QueryArgs("UserQuery"), Page(Ref("UserSummary")));
        Add("users.update", true, "identity.manage", Object([("id", Ref("Uuid")), ("changes", Ref("UserChanges")), ("expectedRevision", Ref("Revision"))], "id", "changes", "expectedRevision"), Ref("UserSummary"));
        Add("users.disable", true, "identity.manage", IdRevision(), Ref("UserSummary"));
        Add("users.setPassword", true, "identity.manage", Object([("id", Ref("Uuid")), ("newPassword", Ref("Password")), ("expectedRevision", Ref("Revision"))], "id", "newPassword", "expectedRevision"),
            Object([("userId", Ref("Uuid")), ("revision", Ref("Revision")), ("passwordChanged", Constant(true))], "userId", "revision", "passwordChanged"));
        Add("roles.create", true, "identity.manage", Object([("role", Ref("RoleCreate"))], "role"), Ref("Role"));
        Add("roles.update", true, "identity.manage", Object([("id", Ref("Uuid")), ("changes", Ref("RoleChanges")), ("expectedRevision", Ref("Revision"))], "id", "changes", "expectedRevision"), Ref("Role"));
        Add("roles.get", false, "identity.manage", Id(), Ref("Role"));
        Add("roles.list", false, "identity.manage", QueryArgs("RoleQuery"), Page(Ref("Role")));
        var membership = Object([("userId", Ref("Uuid")), ("roleId", Ref("Uuid")), ("expectedUserRevision", Ref("Revision"))], "userId", "roleId", "expectedUserRevision");
        Add("roles.assign", true, "identity.manage", membership, Ref("UserSummary"));
        Add("roles.remove", true, "identity.manage", membership, Ref("UserSummary"));
        Add("tokens.create", true, "identity.manage", Object([("principalId", Ref("Uuid")), ("scopes", Array(Ref("Grant"), 1000, 1)), ("expiresAt", Ref("InstantInput"))], "principalId", "scopes"), Ref("TokenIssue"));
        Add("tokens.list", false, "identity.manage", QueryArgs("TokenQuery", ("principalId", Ref("Uuid"))), Page(Ref("TokenMetadata")));
        Add("tokens.revoke", true, "identity.manage", Id(), Ref("TokenMetadata"));
        Add("audit.list", false, "audit.read", QueryArgs("AuditQuery"), Page(Ref("AuditEntry")));
        Add("system.getCapabilities", false, "", Object([]), Ref("Capabilities"));
        Add("system.getHealth", false, "system.manage", Object([]), Ref("Health"));
        Add("system.backup", true, "system.manage", Object([("destination", Text(4096, 1))], "destination"), Ref("BackupManifest"), transport: "cli");
        Add("system.restore", true, "system.manage", Object([("source", Text(4096, 1))], "source"), Ref("BackupManifest"), transport: "cli");

        // Helpers are expression vocabulary, not RPC routes or flow activities. Missing is
        // represented by omission only in these tooling schemas; it has no JSON wire value.
        void Helper(string name, JsonObject input, JsonObject output) => Add(name, false, "", input, output, transport: "expression");
        JsonObject Binary(JsonObject operand) => Object([("a", operand), ("b", operand)], "a", "b");
        foreach (var name in new[] { "equal", "notEqual", "greaterThan", "greaterOrEqual", "lessThan", "lessOrEqual" })
            Helper(name, Binary(Ref("Scalar")), Boolean());
        foreach (var name in new[] { "and", "or" }) Helper(name, Object([("values", Array(Boolean(), 200))], "values"), Boolean());
        Helper("not", Object([("value", Boolean())], "value"), Boolean());
        foreach (var name in new[] { "isNull", "isMissing", "isEmpty" }) Helper(name, Object([("value", Ref("DataValue"))]), Boolean());
        Helper("coalesce", Object([("values", Array(Ref("DataValue"), 200))], "values"), Ref("DataValue"));
        foreach (var name in new[] { "add", "subtract", "multiply" }) Helper(name, Binary(Ref("NumericString")), Ref("NumericString"));
        Helper("divide", Object([("a", Ref("DecimalString")), ("b", Ref("DecimalString")), ("scale", Integer(0, 12)), ("rounding", Ref("Rounding"))], "a", "b", "scale", "rounding"), Ref("DecimalString"));
        Helper("round", Object([("value", Ref("DecimalString")), ("scale", Integer(0, 12)), ("rounding", Ref("Rounding"))], "value", "scale", "rounding"), Ref("DecimalString"));
        Helper("abs", Object([("value", Ref("NumericString"))], "value"), Ref("NumericString"));
        Helper("length", Object([("list", Array(Ref("DataValue"), 200))], "list"), Ref("IntegerString"));
        Helper("contains", Object([("list", Array(Ref("Scalar"), 200)), ("value", Ref("Scalar"))], "list", "value"), Boolean());
        foreach (var name in new[] { "sum", "min", "max" }) Helper(name, Object([("list", Array(Ref("NumericString"), 200, name == "sum" ? 0 : 1))], "list"), Ref("NumericString"));
        Helper("pluck", Object([("list", Array(Ref("DataObject"), 200)), ("path", Text(512, 1))], "list", "path"), Array(Ref("Scalar"), 200));
        foreach (var name in new[] { "trim", "lower", "upper" }) Helper(name, Object([("value", Text())], "value"), Text());
        Helper("concat", Object([("values", Array(Text(), 200))], "values"), Text());
        Helper("startsWith", Object([("text", Text()), ("prefix", Text())], "text", "prefix"), Boolean());
        Helper("endsWith", Object([("text", Text()), ("suffix", Text())], "text", "suffix"), Boolean());
        Helper("today", Object([("timezone", Text(200, 1))]), new JsonObject { ["type"] = "string", ["format"] = "date" });
        Helper("now", Object([]), Ref("Instant"));
        Helper("addDays", Object([("date", new JsonObject { ["type"] = "string", ["format"] = "date" }), ("days", Ref("IntegerString"))], "date", "days"), new JsonObject { ["type"] = "string", ["format"] = "date" });
        Helper("daysBetween", Object([("start", new JsonObject { ["type"] = "string", ["format"] = "date" }), ("end", new JsonObject { ["type"] = "string", ["format"] = "date" })], "start", "end"), Ref("IntegerString"));
        Helper("toString", Object([("value", Ref("Scalar"))], "value"), Text());
        Helper("parseInteger", Object([("text", Text())], "text"), Ref("IntegerString"));
        Helper("parseDecimal", Object([("text", Text())], "text"), Ref("DecimalString"));
        Helper("parseDate", Object([("text", Text())], "text"), new JsonObject { ["type"] = "string", ["format"] = "date" });
        return methods.AsReadOnly();
    }

    private static Dictionary<string, JsonObject> Definitions()
    {
        var d = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["Name"] = new() { ["type"] = "string", ["pattern"] = "^[A-Za-z][A-Za-z0-9_]{0,63}$", ["maxLength"] = 64 },
            ["OperationName"] = Text(200, 1),
            ["Uuid"] = new() { ["type"] = "string", ["format"] = "uuid", ["pattern"] = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" },
            ["Revision"] = Integer(1, int.MaxValue),
            ["Instant"] = new() { ["type"] = "string", ["format"] = "date-time", ["pattern"] = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$" },
            ["InstantInput"] = new() { ["type"] = "string", ["format"] = "date-time", ["maxLength"] = 35, ["pattern"] = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,3})?(Z|[+-][0-9]{2}:[0-9]{2})$" },
            ["Hash"] = new() { ["type"] = "string", ["pattern"] = "^[0-9a-f]{64}$" },
            ["IntegerString"] = new() { ["type"] = "string", ["pattern"] = "^(0|-?[1-9][0-9]*)$", ["maxLength"] = 20, ["description"] = "Canonical signed Int64 business integer, encoded as a string; range checked by the typed codec." },
            ["CountString"] = new() { ["type"] = "string", ["pattern"] = "^(0|[1-9][0-9]*)$", ["maxLength"] = 20 },
            ["DecimalString"] = new() { ["type"] = "string", ["pattern"] = "^-?(0|[1-9][0-9]*)(\\.[0-9]{1,12})?$", ["maxLength"] = 42, ["description"] = "Exact decimal text, precision at most 28 and scale at most 12; no exponent or implicit rounding." },
            ["NumericString"] = Union(Ref("IntegerString"), Ref("DecimalString")),
            ["Scalar"] = Union(Text(), Boolean(), Null()),
            ["DataValue"] = Union(Text(), Boolean(), Null(), Array(Ref("DataValue"), 200), Map(Ref("DataValue"))),
            ["DataObject"] = Map(Ref("DataValue")),
            ["JsonValue"] = Union(Text(256 * 1024), Boolean(), Null(), new JsonObject { ["type"] = "number" }, Array(Ref("JsonValue"), 1000), Map(Ref("JsonValue"), 1000)),
            ["JsonObject"] = Map(Ref("JsonValue"), 1000),
            ["Password"] = new() { ["type"] = "string", ["minLength"] = 12, ["maxLength"] = 128, ["format"] = "password", ["writeOnly"] = true, ["description"] = "Exact Unicode secret; never normalized or stored in a command receipt." },
            ["Rounding"] = Enum("halfEven", "halfAwayFromZero", "towardZero"),
            ["Action"] = Enum("types.read", "types.manage", "record.read", "record.create", "record.update", "record.delete", "operations.execute", "flows.read", "flows.manage", "flows.execute", "runs.readAll", "identity.manage", "audit.read", "system.manage"),
            ["Resource"] = Text(200, 1),
            ["FieldError"] = Object([("path", Text(1024)), ("code", Text(128, 1)), ("message", Text())], "path", "code", "message"),
            ["ValidationReport"] = Object([("valid", Boolean()), ("errors", Array(Ref("FieldError"), 100))], "valid", "errors"),
            ["Grant"] = Object([("action", Ref("Action")), ("resource", Ref("Resource"))], "action", "resource"),
            ["Patch"] = Map(Ref("DataValue"), minimum: 1),
            ["FlowDefinition"] = Object([("source", Text(64 * 1024, 1))], "source"),
            ["UserCreate"] = Object([("userName", Ref("Name")), ("displayName", Text(200, 1)), ("kind", Enum("human", "service"))], "userName", "displayName", "kind"),
            ["UserChanges"] = Object([("displayName", Text(200, 1)), ("active", Boolean())]),
            ["RoleCreate"] = Object([("name", Ref("Name")), ("grants", Array(Ref("Grant"), 1000))], "name", "grants"),
            ["RoleChanges"] = Object([("grants", Array(Ref("Grant"), 1000))], "grants")
        };
        d["DataObject"]["propertyNames"] = Ref("Name");
        d["Patch"]["propertyNames"] = Ref("Name");
        d["UserChanges"]["minProperties"] = 1;
        d["Record"] = Object([
            ("id", Ref("Uuid")), ("type", Ref("Name")), ("typeVersion", Ref("Revision")), ("revision", Ref("Revision")),
            ("deleted", Boolean()), ("data", Ref("DataObject")), ("createdAt", Ref("Instant")), ("createdBy", Ref("Uuid")), ("updatedAt", Ref("Instant")), ("updatedBy", Ref("Uuid"))
        ], "id", "type", "typeVersion", "revision", "deleted", "data", "createdAt", "createdBy", "updatedAt", "updatedBy");
        d["Attachment"] = Object([
            ("id", Ref("Uuid")), ("type", Nullable(Ref("Name"))), ("recordId", Nullable(Ref("Uuid"))), ("state", Enum("pending", "linked", "deleted")), ("revision", Ref("Revision")),
            ("deleted", Boolean()), ("fileName", Text(255, 1)), ("contentType", Text(200, 3)),
            ("sizeBytes", Integer(0, AttachmentService.MaximumBytes)), ("sha256", Ref("Hash")),
            ("createdAt", Ref("Instant")), ("createdBy", Ref("Uuid")), ("updatedAt", Ref("Instant")),
            ("updatedBy", Ref("Uuid")), ("expiresAt", Nullable(Ref("Instant"))), ("downloadUrl", Text(512, 1))
        ], "id", "type", "recordId", "state", "revision", "deleted", "fileName", "contentType", "sizeBytes", "sha256", "createdAt", "createdBy", "updatedAt", "updatedBy", "expiresAt", "downloadUrl");
        d["ProjectedRecord"] = Object([("id", Ref("Uuid")), ("type", Ref("Name")), ("typeVersion", Ref("Revision")), ("revision", Ref("Revision")), ("data", Ref("DataObject"))], "id", "type", "typeVersion", "revision", "data");
        d["TombstoneReceipt"] = Object([("id", Ref("Uuid")), ("type", Ref("Name")), ("revision", Ref("Revision")), ("deleted", Constant(true))], "id", "type", "revision", "deleted");

        d["DataField"] = FieldDefinition(persistent: false, top: false);
        d["NestedField"] = FieldDefinition(persistent: true, top: false);
        d["FieldDefinition"] = FieldDefinition(persistent: true, top: true);
        d["DataSchema"] = Object([("fields", NameMap(Ref("DataField")))], "fields");
        d["TypeDefinition"] = Object([
            ("name", Ref("Name")), ("description", Text()), ("fields", NameMap(Ref("FieldDefinition"))),
            ("accessMode", Default(Enum("direct", "operationsOnly"), JsonValue.Create("direct")!)),
            ("allowedOperations", Default(Array(Ref("OperationName"), 200), new JsonArray()))
        ], "name", "fields");
        d["TypeDraft"] = Object([("name", Ref("Name")), ("draftRevision", Ref("Revision")), ("definition", Ref("TypeDefinition")), ("updatedAt", Ref("Instant")), ("updatedBy", Ref("Uuid"))], "name", "draftRevision", "definition", "updatedAt", "updatedBy");
        d["PublishedType"] = Object([("name", Ref("Name")), ("version", Ref("Revision")), ("hash", Ref("Hash")), ("definition", Ref("TypeDefinition")), ("publishedAt", Ref("Instant")), ("publishedBy", Ref("Uuid"))], "name", "version", "hash", "definition", "publishedAt", "publishedBy");
        d["TypeSummary"] = Object([("name", Ref("Name")), ("activeVersion", Ref("Revision")), ("hash", Ref("Hash"))], "name", "activeVersion", "hash");
        d["TypeVersionSummary"] = Object([("name", Ref("Name")), ("version", Ref("Revision")), ("hash", Ref("Hash")), ("publishedAt", Ref("Instant"))], "name", "version", "hash", "publishedAt");
        d["FlowDraft"] = Object([("name", Ref("Name")), ("draftRevision", Ref("Revision")), ("source", Text(64 * 1024)), ("updatedAt", Ref("Instant")), ("updatedBy", Ref("Uuid"))], "name", "draftRevision", "source", "updatedAt", "updatedBy");
        d["Dependencies"] = Object([("types", NameMap(Array(Ref("Revision"), 200, 1))), ("operations", Map(Ref("Revision")))], "types", "operations");
        d["PublishedFlow"] = Object([
            ("name", Ref("Name")), ("version", Ref("Revision")), ("hash", Ref("Hash")), ("sourceHash", Ref("Hash")), ("astHash", Ref("Hash")),
            ("format", Constant("bos.flow/1.0")), ("mode", Enum("command", "query")), ("transaction", Enum("atomic", "readOnly")),
            ("inputSchema", Ref("DataSchema")), ("outputSchema", Ref("DataSchema")), ("dependencies", Ref("Dependencies")),
            ("capabilities", Array(Text(512, 1), 200)), ("publishedAt", Ref("Instant")), ("publishedBy", Ref("Uuid")),
            ("controlRevision", Ref("Revision")), ("disabled", Boolean()), ("source", Text(64 * 1024))
        ], "name", "version", "hash", "sourceHash", "astHash", "format", "inputSchema", "outputSchema", "dependencies", "capabilities", "publishedAt", "publishedBy", "controlRevision", "disabled");
        d["CompileReport"] = Object([("valid", Boolean()), ("errors", Array(Ref("FieldError"), 100)), ("warnings", Array(Ref("FieldError"), 100)), ("dependencies", Nullable(Ref("Dependencies")))], "valid", "errors", "warnings", "dependencies");
        d["FlowSummary"] = Object([("name", Ref("Name")), ("activeVersion", Ref("Revision")), ("disabled", Boolean()), ("controlRevision", Ref("Revision"))], "name", "activeVersion", "disabled", "controlRevision");
        d["FlowVersionSummary"] = d["TypeVersionSummary"].DeepClone().AsObject();
        d["FlowControl"] = Object([("name", Ref("Name")), ("disabled", Boolean()), ("revision", Ref("Revision"))], "disabled", "revision");
        d["OperationDescriptor"] = Object([
            ("name", Ref("OperationName")), ("contractVersion", Ref("Revision")), ("description", Text()), ("inputSchema", Ref("DataSchema")), ("outputSchema", Ref("DataSchema")),
            ("mode", Enum("read", "write")), ("flowCallable", Boolean()), ("requiredGrants", Array(Ref("Grant"), 1000)), ("capabilities", Array(Ref("Grant"), 1000)),
            ("metadataPaths", Array(Text(512, 1), 1000))
        ], "name", "contractVersion", "description", "inputSchema", "outputSchema", "mode", "flowCallable", "requiredGrants", "capabilities");
        d["RunError"] = Object([
            ("code", Text(128, 1)), ("message", Text()), ("path", Text(1024)), ("details", Array(Ref("FieldError"), 100)), ("traceId", Ref("Uuid")), ("retryable", Boolean()),
            ("flowVersion", Ref("Revision")), ("stepId", Nullable(Text(64))), ("iterationPath", Text(1024))
        ], "code", "message", "details", "traceId", "retryable");
        d["StepResult"] = Object([
            ("stepId", Text(64, 1)), ("iterationPath", Text(1024)), ("ordinal", Integer(1, 500)), ("startedAt", Ref("Instant")), ("durationMs", new JsonObject { ["type"] = "number", ["minimum"] = 0 }),
            ("outcome", Enum("running", "succeeded", "failed", "interrupted")), ("errorCode", Text(64, 1)),
            ("log", Object([("level", Enum("info", "warn")), ("message", Text(16384)),
                ("fields", new JsonObject { ["type"] = "object", ["additionalProperties"] = Union(Text(16384), Boolean(), new JsonObject { ["type"] = new JsonArray("number", "null") }) })], "level", "message", "fields"))
        ], "stepId", "iterationPath", "ordinal", "startedAt", "durationMs", "outcome");
        d["RunReceipt"] = Object([
            ("runId", Ref("Uuid")), ("flowName", Ref("Name")), ("flowVersion", Ref("Revision")), ("hash", Ref("Hash")), ("principalId", Ref("Uuid")),
            ("status", Enum("running", "succeeded", "failed", "interrupted")), ("startedAt", Ref("Instant")), ("completedAt", Nullable(Ref("Instant"))), ("inputHash", Ref("Hash")),
            ("readTypes", Array(Ref("Name"), 200)), ("changedRecordIds", Array(Ref("Uuid"), 250)), ("result", Ref("JsonValue")), ("error", Ref("RunError")),
            ("resultRedacted", Boolean()), ("steps", Array(Ref("StepResult"), 500))
        ], "runId", "flowName", "flowVersion", "hash", "principalId", "status", "startedAt", "inputHash", "readTypes", "changedRecordIds", "resultRedacted");
        d["RunSummary"] = Object([("runId", Ref("Uuid")), ("flowName", Ref("Name")), ("flowVersion", Ref("Revision")), ("principalId", Ref("Uuid")), ("status", Enum("running", "succeeded", "failed", "interrupted")), ("startedAt", Ref("Instant")), ("completedAt", Nullable(Ref("Instant")))], "runId", "flowName", "flowVersion", "principalId", "status", "startedAt");
        d["UserSummary"] = Object([("id", Ref("Uuid")), ("userName", Ref("Name")), ("displayName", Text(200, 1)), ("kind", Enum("human", "service")), ("active", Boolean()), ("roles", Array(Ref("Uuid"), 1000)), ("revision", Ref("Revision"))], "id", "userName", "displayName", "kind", "active", "roles", "revision");
        d["Role"] = Object([("id", Ref("Uuid")), ("name", Ref("Name")), ("grants", Array(Ref("Grant"), 1000)), ("revision", Ref("Revision"))], "id", "name", "grants", "revision");
        d["CurrentIdentity"] = Object([("id", Ref("Uuid")), ("displayName", Text(200, 1)), ("roles", Array(Ref("Uuid"), 1000)), ("effectiveGrants", Array(Ref("Grant"), 1000))], "id", "displayName", "roles", "effectiveGrants");
        d["TokenMetadata"] = Object([("id", Ref("Uuid")), ("principalId", Ref("Uuid")), ("scopes", Array(Ref("Grant"), 1000)), ("createdAt", Ref("Instant")), ("expiresAt", Ref("Instant")), ("revokedAt", Nullable(Ref("Instant")))], "id", "principalId", "scopes", "createdAt", "expiresAt");
        d["TokenIssue"] = Object([("token", Nullable(new JsonObject { ["type"] = "string", ["pattern"] = "^bos_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.[A-Za-z0-9_-]{43}$", ["readOnly"] = true, ["description"] = "One-time secret disclosure; null on replay." })), ("metadata", Ref("TokenMetadata")), ("replayCode", Constant("SECRET_ALREADY_DISCLOSED"))], "token", "metadata");
        d["AuditEntry"] = Object([
            ("sequence", Integer(1, long.MaxValue)), ("timestamp", Ref("Instant")), ("at", Ref("Instant")), ("actor", Text(200, 1)), ("credentialId", Nullable(Ref("Uuid"))),
            ("method", Text(200, 1)), ("commandId", Nullable(Ref("Uuid"))), ("runId", Nullable(Ref("Uuid"))), ("objectId", Nullable(Text(200))), ("type", Ref("Name")),
            ("typeVersion", Ref("Revision")), ("version", Ref("Revision")), ("revision", Ref("Revision")), ("oldRevision", Nullable(Ref("Revision"))), ("newRevision", Ref("Revision")),
            ("guardOwner", Nullable(Ref("OperationName"))), ("stepId", Nullable(Text(64))), ("iterationPath", Text(1024)), ("outcome", Text(64, 1))
        ], "sequence", "actor", "method");

        d["RecordComparison"] = Union(
            Object([("field", Text(64, 1)), ("op", Enum("eq", "ne", "gt", "gte", "lt", "lte")), ("value", Union(Text(), Boolean()))], "field", "op", "value"),
            Object([("field", Text(64, 1)), ("op", Enum("in", "notIn")), ("value", Array(Union(Text(), Boolean()), 50))], "field", "op", "value"),
            Object([("field", Text(64, 1)), ("op", Enum("isNull", "isMissing")), ("value", Boolean())], "field", "op", "value"));
        d["RecordCountFilter"] = Object([("where", Array(Ref("RecordComparison"), 10))]);
        d["RecordQuery"] = Object([
            ("where", Array(Ref("RecordComparison"), 10)), ("orderBy", Array(Object([("field", Text(64, 1)), ("direction", Default(Enum("asc", "desc"), JsonValue.Create("asc")!))], "field"), 2, 1)),
            ("select", Array(Ref("Name"), 200)), ("limit", Default(Integer(1, 200), JsonValue.Create(50)!)), ("cursor", Nullable(Text(16384, 1)))
        ]);
        JsonObject Query(params (string Name, JsonObject Schema)[] fields) => CatalogueQuery(fields);
        d["TypeQuery"] = Query(("name", Ref("Name")), ("status", Constant("published")));
        d["VersionQuery"] = Query();
        d["FlowQuery"] = Query(("name", Ref("Name")), ("disabled", Boolean()));
        d["OperationQuery"] = Query(("name", Ref("OperationName")), ("mode", Enum("read", "write")), ("flowCallable", Boolean()));
        d["RunQuery"] = Query(("flowName", Ref("Name")), ("flowVersion", Ref("Revision")), ("status", Enum("running", "succeeded", "failed", "interrupted")), ("principalId", Ref("Uuid")));
        d["StepQuery"] = Query(("stepId", Text(64, 1)), ("outcome", Enum("running", "succeeded", "failed", "interrupted")));
        d["UserQuery"] = Query(("userName", Ref("Name")), ("kind", Enum("human", "service")), ("active", Boolean()));
        d["RoleQuery"] = Query(("name", Ref("Name")));
        d["TokenQuery"] = Query(("revoked", Boolean()));
        d["AuditQuery"] = Query(("actor", Text(200, 1)), ("method", Text(200, 1)), ("commandId", Ref("Uuid")), ("runId", Ref("Uuid")), ("type", Ref("Name")), ("objectId", Text(200, 1)));
        d["MethodDescriptor"] = Object([
            ("name", Text(200, 1)), ("contractVersion", Ref("Revision")), ("mutating", Boolean()), ("readOnly", Boolean()), ("mode", Enum("read", "write")), ("grant", Text(200)),
            ("inputSchema", Ref("JsonObject")), ("outputSchema", Ref("JsonObject")), ("flowCallable", Boolean()), ("transport", Constant("http"))
        ], "name", "contractVersion", "mutating", "readOnly", "mode", "grant", "inputSchema", "outputSchema", "flowCallable", "transport");
        d["ExpressionDescriptor"] = Object([
            ("name", Text(200, 1)), ("contractVersion", Ref("Revision")), ("inputSchema", Ref("JsonObject")), ("outputSchema", Ref("JsonObject"))
        ], "name", "contractVersion", "inputSchema", "outputSchema");
        d["Limits"] = Object([
            ("requestBytes", Integer(1, 262144)), ("yamlBytes", Integer(1, 65536)), ("astBytes", Integer(1, 262144)),
            ("staticSteps", Integer(1, 500)), ("schemaFields", Integer(1, 200)), ("schemaDepth", Integer(1, 8)), ("arrayItems", Integer(1, 200)), ("recordScalars", Integer(1, 2000)),
            ("expressionDepth", Integer(1, 16)), ("evaluatedNodes", Integer(1, 10000)), ("loopItems", Integer(1, 100)), ("sdkCalls", Integer(1, 200)),
            ("mutations", Integer(1, 250)), ("returnedRecords", Integer(1, 1000)), ("queryPage", Integer(1, 200)), ("queryFilters", Integer(1, 10)), ("queryInItems", Integer(1, 50)),
            ("queryVmInstructions", Integer(1, 1000000)), ("executionMilliseconds", Integer(1, 2000)), ("queryMilliseconds", Integer(1, 1000)),
            ("outputBytes", Integer(1, 65536)), ("diagnosticBytes", Integer(1, 16384)),
            ("attachmentBytes", Integer(1, AttachmentService.MaximumBytes)), ("recordAttachments", Integer(1, 100))
        ], "requestBytes", "yamlBytes", "astBytes", "staticSteps", "schemaFields", "schemaDepth", "arrayItems", "recordScalars",
            "expressionDepth", "evaluatedNodes", "loopItems", "sdkCalls", "mutations", "returnedRecords", "queryPage", "queryFilters",
            "queryInItems", "queryVmInstructions", "executionMilliseconds", "queryMilliseconds", "outputBytes", "diagnosticBytes", "attachmentBytes", "recordAttachments");
        d["Capabilities"] = Object([
            ("productVersion", Text(100, 1)), ("specificationVersion", Constant("1.0.0")), ("releaseStatus", Text(100, 1)),
            ("flowFormats", Array(Text(100, 1), 10, 1)), ("astFormats", Array(Text(100, 1), 10, 1)),
            ("methods", Array(Ref("MethodDescriptor"), 200)), ("helpers", Array(Ref("ExpressionDescriptor"), 200)), ("limits", Ref("Limits"))
        ], "productVersion", "specificationVersion", "releaseStatus", "flowFormats", "astFormats", "methods", "helpers", "limits");
        d["Health"] = Object([
            ("status", Enum("ready", "notReady")), ("checks", Array(Object([("name", Text(200, 1)), ("status", Text(64, 1))], "name", "status"), 100)),
            ("metrics", Object([("activeReaders", Integer(0, 4)), ("activeWriters", Integer(0, 1)), ("databaseBytes", Integer(0, long.MaxValue)), ("walBytes", Integer(0, long.MaxValue))], "activeReaders", "activeWriters", "databaseBytes", "walBytes"))
        ], "status", "checks", "metrics");
        d["BackupManifest"] = Object([
            ("format", Constant("bos.backup/1")), ("engineVersion", Ref("Revision")), ("schemaVersion", Ref("Revision")), ("createdAt", new JsonObject { ["type"] = "string", ["format"] = "date-time" }),
            ("entries", Array(Object([("path", Text(4096, 1)), ("size", Integer(0, long.MaxValue)), ("sha256", Ref("Hash"))], "path", "size", "sha256"), 4096))
        ], "format", "engineVersion", "schemaVersion", "createdAt", "entries");
        return d;
    }

    private static JsonObject FieldDefinition(bool persistent, bool top)
    {
        var variants = new List<JsonObject>();
        foreach (var kind in new[]
        {
            "string", "integer", "decimal", "boolean", "date", "instant", "enum", "reference", "object", "array",
            "time", "localDateTime", "duration", "calendarPeriod", "offsetDateTime", "map", "taggedUnion", "money",
            "percentage", "quantity", "geopoint", "dateRange", "instantRange", "multiEnum", "polymorphicReference",
            "versionedReference", "userReference", "attachment"
        })
        {
            var properties = new List<(string, JsonObject)>
            {
                ("kind", Constant(kind)), ("required", Default(Boolean(), JsonValue.Create(false)!)),
                ("nullable", Default(Boolean(), JsonValue.Create(false)!)), ("default", Ref("JsonValue"))
            };
            var required = new List<string> { "kind" };
            if (persistent)
            {
                properties.Add(("managedBy", Array(Ref("OperationName"), 200)));
                if (top && kind is "string" or "integer" or "decimal" or "boolean" or "date" or "instant" or "enum" or "reference"
                    or "time" or "localDateTime" or "duration" or "calendarPeriod" or "offsetDateTime" or "percentage")
                    properties.Add(("indexed", Boolean()));
                if (top && kind is "string" or "integer" or "decimal" or "boolean" or "date" or "instant" or "enum"
                    or "time" or "localDateTime" or "duration" or "calendarPeriod" or "offsetDateTime" or "percentage")
                    properties.Add(("unique", Boolean()));
            }
            if (kind == "string")
            {
                properties.Add(("minLength", Integer(0, 16384)));
                properties.Add(("maxLength", Integer(0, 16384)));
                properties.Add(("format", Enum("email", "phone", "url", "uuid", "uri", "slug", "ipAddress", "hostname", "countryCode", "currencyCode", "locale", "timeZone")));
                properties.Add(("pattern", Text(256)));
            }
            if (kind is "integer" or "decimal")
            {
                var number = Ref(kind == "integer" ? "IntegerString" : "DecimalString");
                foreach (var name in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf" })
                    properties.Add((name, number));
            }
            if (kind is "percentage" or "money" or "quantity")
                foreach (var name in new[] { "minimum", "maximum" }) properties.Add((name, Ref("DecimalString")));
            switch (kind)
            {
                case "decimal":
                case "percentage":
                    properties.Add(("precision", Integer(1, 28))); properties.Add(("scale", Integer(0, 12))); required.AddRange(["precision", "scale"]); break;
                case "enum": properties.Add(("values", Array(Text(), 200, 1, unique: true))); required.Add("values"); break;
                case "reference": properties.Add(("targetType", Ref("Name"))); required.Add("targetType"); break;
                case "object": properties.Add(("fields", NameMap(Ref(persistent ? "NestedField" : "DataField")))); required.Add("fields"); break;
                case "array":
                    properties.Add(("items", Ref(persistent ? "NestedField" : "DataField"))); properties.Add(("minItems", Integer(0, 200)));
                    properties.Add(("maxItems", Integer(0, 200))); properties.Add(("uniqueItems", Boolean())); required.AddRange(["items", "maxItems"]); break;
                case "map":
                    properties.Add(("values", Ref(persistent ? "NestedField" : "DataField"))); properties.Add(("minProperties", Integer(0, 200)));
                    properties.Add(("maxProperties", Integer(0, 200))); properties.Add(("maxKeyLength", Integer(1, 200))); properties.Add(("keyPattern", Text(256)));
                    required.AddRange(["values", "maxProperties"]); break;
                case "taggedUnion":
                    var union = NameMap(Ref(persistent ? "NestedField" : "DataField")); union["minProperties"] = 1; union["maxProperties"] = 20;
                    properties.Add(("variants", union)); required.Add("variants"); break;
                case "money":
                    properties.Add(("precision", Integer(1, 28))); properties.Add(("scale", Integer(0, 12)));
                    properties.Add(("currencies", Array(new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Z]{3}$" }, 200, 1, unique: true)));
                    required.AddRange(["precision", "scale"]); break;
                case "quantity":
                    properties.Add(("precision", Integer(1, 28))); properties.Add(("scale", Integer(0, 12)));
                    properties.Add(("units", Array(Text(), 200, 1, unique: true))); required.AddRange(["precision", "scale", "units"]); break;
                case "multiEnum":
                    properties.Add(("values", Array(Text(), 200, 1, unique: true))); properties.Add(("minItems", Integer(0, 200)));
                    properties.Add(("maxItems", Integer(0, 200))); required.AddRange(["values", "maxItems"]); break;
                case "polymorphicReference": properties.Add(("targetTypes", Array(Ref("Name"), 200, 1, unique: true))); required.Add("targetTypes"); break;
                case "versionedReference": properties.Add(("targetType", Ref("Name"))); required.Add("targetType"); break;
            }
            variants.Add(Object(properties.ToArray(), required.ToArray()));
        }
        return new JsonObject { ["oneOf"] = Nodes(variants) };
    }

    private static JsonObject CatalogueQuery((string Name, JsonObject Schema)[] filters)
    {
        var comparisons = filters.Select(f => Object([("field", Constant(f.Name)), ("op", Constant("eq")), ("value", f.Schema)], "field", "op", "value")).ToArray();
        JsonObject where = comparisons.Length == 0 ? Array(new JsonObject { ["not"] = new JsonObject() }, 0)
            : Array(Union(comparisons), 10);
        return Object([("where", where), ("limit", Default(Integer(1, 200), JsonValue.Create(50)!)), ("cursor", Nullable(Text(16384, 1)))]);
    }

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    private static JsonObject Text(int maximum = 16384, int minimum = 0) => new() { ["type"] = "string", ["minLength"] = minimum, ["maxLength"] = maximum };
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Null() => new() { ["type"] = "null" };
    private static JsonObject Integer(long minimum, long maximum) => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
    private static JsonObject Constant(string value) => new() { ["type"] = "string", ["const"] = value };
    private static JsonObject Constant(bool value) => new() { ["type"] = "boolean", ["const"] = value };
    private static JsonObject Constant(int value) => new() { ["type"] = "integer", ["const"] = value };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) };
    private static JsonObject Nullable(JsonObject schema) => Union(schema, Null());
    private static JsonObject Union(params JsonObject[] schemas) => new() { ["anyOf"] = Nodes(schemas) };
    private static JsonArray Nodes(IEnumerable<JsonObject> schemas) => new(schemas.Select(s => (JsonNode)s.DeepClone()).ToArray());
    private static JsonObject Default(JsonObject schema, JsonNode value) { var result = (JsonObject)schema.DeepClone(); result["default"] = value.DeepClone(); return result; }
    private static JsonObject Array(JsonObject item, int maximum = 200, int minimum = 0, bool unique = false)
    {
        var result = new JsonObject { ["type"] = "array", ["items"] = item.DeepClone(), ["minItems"] = minimum, ["maxItems"] = maximum };
        if (unique) result["uniqueItems"] = true;
        return result;
    }
    private static JsonObject Map(JsonObject value, int maximum = 200, int minimum = 0) => new()
    { ["type"] = "object", ["additionalProperties"] = value.DeepClone(), ["minProperties"] = minimum, ["maxProperties"] = maximum };
    private static JsonObject NameMap(JsonObject value)
    {
        var result = Map(value);
        result["propertyNames"] = Ref("Name");
        return result;
    }
    private static JsonObject Object((string Name, JsonObject Schema)[] properties, params string[] required)
    {
        var fields = new JsonObject();
        foreach (var (name, schema) in properties) fields[name] = schema.DeepClone();
        return new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["properties"] = fields,
            ["required"] = new JsonArray(required.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
    }

    private static JsonObject Page(JsonObject item, int maximum = 200) => Object([("items", Array(item, maximum)), ("nextCursor", Nullable(Text(16384, 1)))], "items", "nextCursor");
}
