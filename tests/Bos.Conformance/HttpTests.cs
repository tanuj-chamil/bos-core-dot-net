using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Application;
using Bos.Contracts;
using Bos.Host;
using Bos.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bos.Conformance;

/// <summary>Real HTTPS/Kestrel, persisted Identity, and SQLite; no alternate authentication path.</summary>
public sealed class HttpTests
{
    [Fact]
    public async Task Anonymous_failures_have_complete_safe_envelopes_and_uuid_traces()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        using var response = await client.PostAsJsonAsync("/v1/methods/system.getCapabilities", new { });
        await AssertError(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        using var missing = await client.GetAsync("/not-an-endpoint");
        await AssertError(missing, HttpStatusCode.NotFound, "NOT_FOUND");
        using var wrongVerb = await client.GetAsync("/v1/auth/login");
        await AssertError(wrongVerb, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
        var health = await client.GetFromJsonAsync<JsonObject>("/health/ready");
        Assert.Equal("ready", health!["status"]!.GetValue<string>());
        Assert.Single(health);
    }

    [Fact]
    public async Task Body_and_media_limits_are_enforced_before_authentication()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        using var wrongMedia = await client.PostAsync("/v1/methods/identity.getCurrent", new StringContent("{}"));
        await AssertError(wrongMedia, HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE");
        using var large = new ByteArrayContent(new byte[256 * 1024 + 1]);
        large.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var tooLarge = await client.PostAsync("/v1/methods/identity.getCurrent", large);
        await AssertError(tooLarge, HttpStatusCode.RequestEntityTooLarge, "REQUEST_TOO_LARGE");
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"x\":\"\\ud800\"}")]
    public async Task Rpc_uses_strict_object_json(string body)
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        await host.Login(client);
        using var response = await client.PostAsync("/v1/methods/identity.getCurrent", new StringContent(body, Encoding.UTF8, "application/json"));
        await AssertError(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
    }

    [Fact]
    public async Task Invalid_utf8_and_oversized_chunked_bodies_are_rejected()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        await host.Login(client);
        using var invalidUtf8 = new ByteArrayContent([123, 34, 120, 34, 58, 34, 255, 34, 125]);
        invalidUtf8.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var invalid = await client.PostAsync("/v1/methods/identity.getCurrent", invalidUtf8);
        await AssertError(invalid, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        using var chunked = new ChunkedContent(Encoding.UTF8.GetBytes("{}" + new string(' ', 256 * 1024)));
        using var tooLarge = await client.PostAsync("/v1/methods/identity.getCurrent", chunked);
        await AssertError(tooLarge, HttpStatusCode.RequestEntityTooLarge, "REQUEST_TOO_LARGE");
    }

    [Fact]
    public async Task Cookie_mutations_require_antiforgery_and_cookie_flags_are_fixed()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        using var loginWithoutCsrf = await client.PostAsJsonAsync("/v1/auth/login", new { userName = "Admin", password = RunningHost.Password });
        await AssertError(loginWithoutCsrf, HttpStatusCode.Forbidden, "FORBIDDEN");
        using var login = await host.Login(client);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-Bos.Session=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());

        using var noCsrf = await host.Rpc(client, "users.create", new JsonObject { ["user"] = User("NoCsrf") }, csrf: false);
        await AssertError(noCsrf, HttpStatusCode.Forbidden, "FORBIDDEN");
        using var withCsrf = await host.Rpc(client, "users.create", new JsonObject { ["user"] = User("WithCsrf") }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, withCsrf.StatusCode);
        var value = await withCsrf.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("WithCsrf", value!["result"]!["userName"]!.GetValue<string>());
        Assert.True(Guid.TryParseExact(value["meta"]!["traceId"]!.GetValue<string>(), "D", out _));
    }

    [Fact]
    public async Task Service_token_mutation_requires_no_cookie_csrf_and_invalid_bearer_never_falls_back_to_cookie()
    {
        await using var host = await RunningHost.Start();
        var token = await host.ServiceToken();
        using var client = host.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var created = await host.Rpc(client, "users.create", new JsonObject { ["user"] = User("ByToken") }, false);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var human = host.Client();
        await host.Login(human);
        human.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var rejected = await host.Rpc(human, "identity.getCurrent", new JsonObject(), false);
        await AssertError(rejected, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Operations_call_uses_resolved_mode_for_cookie_antiforgery()
    {
        await using var host = await RunningHost.Start();
        var api = host.App.Services.GetRequiredService<IApplicationApi>();
        var draft = await api.InvokeAsync("types.create", new JsonObject { ["definition"] = new JsonObject
        {
            ["name"] = "HttpCsrfNote", ["fields"] = new JsonObject { ["title"] = new JsonObject { ["kind"] = "string", ["required"] = true } }
        } }, host.Admin, Guid.NewGuid().ToString("N"));
        await api.InvokeAsync("types.publish", new JsonObject
        {
            ["name"] = "HttpCsrfNote", ["expectedRevision"] = draft.Result!["draftRevision"]!.GetValue<int>()
        }, host.Admin, Guid.NewGuid().ToString("N"));
        using var client = host.Client();
        using var login = await host.Login(client);

        // A real read operation needs neither a CSRF header nor an idempotency key.
        using var read = await client.PostAsJsonAsync("/v1/methods/operations.call", new
        {
            name = "records.count", input = new { type = "HttpCsrfNote" }
        });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var count = await read.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("0", count!["result"]!["count"]!.GetValue<string>());

        var write = new JsonObject
        {
            ["name"] = "records.create", ["input"] = new JsonObject
            {
                ["type"] = "HttpCsrfNote", ["data"] = new JsonObject { ["title"] = "Protected write" }
            }
        };
        using var missingCsrf = await host.Rpc(client, "operations.call", write, false);
        await AssertError(missingCsrf, HttpStatusCode.Forbidden, "FORBIDDEN");
        using var permitted = await host.Rpc(client, "operations.call", write, true);
        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
    }

    [Fact]
    public async Task Principal_fixed_windows_separate_reads_and_writes_and_return_retry_after()
    {
        await using var host = await RunningHost.Start("""{"limits":{"readRequestsPerMinute":1,"writeRequestsPerMinute":1}}""");
        using var client = host.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await host.ServiceToken());
        using var read = await host.Rpc(client, "identity.getCurrent", new JsonObject(), false);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var overRead = await host.Rpc(client, "identity.getCurrent", new JsonObject(), false);
        await AssertError(overRead, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.NotNull(overRead.Headers.RetryAfter);
        using var write = await host.Rpc(client, "users.create", new JsonObject { ["user"] = User("RateOne") }, false);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        using var overWrite = await host.Rpc(client, "users.create", new JsonObject { ["user"] = User("RateTwo") }, false);
        await AssertError(overWrite, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task Login_ip_window_counts_failures_and_ignores_spoofed_forwarded_addresses()
    {
        await using var host = await RunningHost.Start("""{"limits":{"loginAttemptsPerMinute":2}}""");
        using var client = host.Client();
        var csrf = await RunningHost.Csrf(client);
        string? previous = null;
        foreach (var name in new[] { "Admin", "NoSuchAccount", "AnotherAccount" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login") { Content = JsonContent.Create(new { userName = name, password = "incorrect-password" }) };
            request.Headers.Add(csrf.Header, csrf.Token);
            request.Headers.Add("X-Forwarded-For", "192.0.2." + (name.Length + 1));
            using var response = await client.SendAsync(request);
            if (name == "AnotherAccount") await AssertError(response, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
            else
            {
                var error = await AssertError(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
                var message = error["message"]!.GetValue<string>();
                if (previous is not null) Assert.Equal(previous, message);
                previous = message;
            }
        }
    }

    [Fact]
    public async Task Password_change_invalidates_other_sessions_at_next_request()
    {
        await using var host = await RunningHost.Start();
        using var first = host.Client();
        using var second = host.Client();
        await host.Login(first);
        await host.Login(second);
        var csrf = await RunningHost.Csrf(first);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/change-password")
        { Content = JsonContent.Create(new { currentPassword = RunningHost.Password, newPassword = "Changed-Password-2026!" }) };
        request.Headers.Add(csrf.Header, csrf.Token);
        using var response = await first.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var stale = await host.Rpc(second, "identity.getCurrent", new JsonObject(), false);
        await AssertError(stale, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Password_change_and_login_preserve_decomposed_unicode_secrets()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        await host.Login(client);
        const string replacement = "Changed-e\u0301-Password-2026!";
        var csrf = await RunningHost.Csrf(client);
        using var change = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/change-password")
        { Content = JsonContent.Create(new { currentPassword = RunningHost.Password, newPassword = replacement }) };
        change.Headers.Add(csrf.Header, csrf.Token);
        using var changed = await client.SendAsync(change);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using var fresh = host.Client();
        csrf = await RunningHost.Csrf(fresh);
        using var normalized = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login")
        { Content = JsonContent.Create(new { userName = "Admin", password = replacement.Normalize(NormalizationForm.FormC) }) };
        normalized.Headers.Add(csrf.Header, csrf.Token);
        using var wrongSecret = await fresh.SendAsync(normalized);
        await AssertError(wrongSecret, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        using var correctSecret = await host.Login(fresh, replacement);
        Assert.Equal(HttpStatusCode.OK, correctSecret.StatusCode);
    }

    [Fact]
    public async Task Reserved_methods_authenticate_return_unavailable_and_are_not_advertised()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        using var anonymous = await host.Rpc(client, "types.activate", new JsonObject(), false);
        await AssertError(anonymous, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await host.Login(client);
        foreach (var method in MethodCatalogue.ReservedMethods)
        {
            using var response = await host.Rpc(client, method, new JsonObject(), false);
            await AssertError(response, HttpStatusCode.UnprocessableEntity, "FEATURE_NOT_AVAILABLE");
        }
        var openapi = await client.GetFromJsonAsync<JsonObject>("/v1/openapi.json");
        foreach (var method in MethodCatalogue.ReservedMethods) Assert.Null(openapi!["paths"]!["/v1/methods/" + method]);
    }

    [Fact]
    public async Task Interrupted_replay_returns_409_with_safe_error_and_reserved_run_id()
    {
        await using var host = await RunningHost.Start();
        var api = host.App.Services.GetRequiredService<IApplicationApi>();
        const string source = """
            format: bos.flow/1.0
            name: HttpReplay
            mode: query
            transaction: readOnly
            inputSchema: {fields: {}}
            outputSchema: {fields: {value: {kind: string, required: true}}}
            dependencies: {types: {}, operations: {}}
            capabilities: []
            steps:
              - id: finish
                return: {object: {value: {literal: ok}}}
            """;
        var draft = await api.InvokeAsync("flows.create", new JsonObject { ["definition"] = new JsonObject { ["source"] = source } }, host.Admin, Guid.NewGuid().ToString("N"));
        await api.InvokeAsync("flows.publish", new JsonObject { ["name"] = "HttpReplay", ["expectedRevision"] = draft.Result!["draftRevision"]!.GetValue<int>() }, host.Admin, Guid.NewGuid().ToString("N"));
        using var client = host.Client();
        await host.Login(client);
        const string key = "http-replay-00000001";
        var args = new JsonObject { ["name"] = "HttpReplay", ["version"] = 1, ["input"] = new JsonObject() };
        using var completed = await host.Rpc(client, "flows.run", args, true, key);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var result = await completed.Content.ReadFromJsonAsync<JsonObject>();
        var runId = result!["result"]!["runId"]!.GetValue<string>();

        // A durable running reservation is the recovery precondition. This fixture changes only
        // its query run's stored state, then exercises the real recovery and HTTP replay paths.
        var factory = host.App.Services.GetRequiredService<IUnitOfWorkFactory>();
        var receiptKey = ReceiptProtocol.Key(host.Admin.PrincipalId, ReceiptProtocol.Scope("flows.run", args), key);
        await using (var uow = await factory.BeginWriteAsync())
        {
            var receipt = (await uow.Documents.GetAsync("receipts", receiptKey))!;
            var run = (await uow.Documents.GetAsync("runs", runId))!;
            foreach (var node in new[] { (JsonObject)receipt["result"]!, run })
            {
                node["status"] = "running";
                node.Remove("completedAt"); node.Remove("result"); node.Remove("error");
            }
            receipt["status"] = 202;
            await uow.Documents.PutAsync("runs", runId, run);
            await uow.Documents.PutAsync("receipts", receiptKey, receipt);
            await uow.CommitAsync();
        }
        await host.App.Services.GetRequiredService<IFlowRunner>().RecoverAsync();
        using var replay = await host.Rpc(client, "flows.run", args, true, key);
        var error = await AssertError(replay, HttpStatusCode.Conflict, "RUN_INTERRUPTED");
        var detail = Assert.Single((JsonArray)error["details"]!);
        Assert.Equal("runId", detail!["path"]!.GetValue<string>());
        Assert.Equal(runId, detail["message"]!.GetValue<string>());
    }

    [Fact]
    public void Catalogue_flow_activities_exclude_identity_administration_and_schemas_resolve_locally()
    {
        var activities = MethodCatalogue.All.Where(m => m.FlowCallable).ToArray();
        Assert.Equal(7, activities.Length);
        Assert.All(activities, method => Assert.StartsWith("records.", method.Name));
        Assert.False(Assert.Single(MethodCatalogue.All, m => m.Name == "records.validate").Mutating);
        Assert.True(Assert.Single(MethodCatalogue.All, m => m.Name == "operations.call").Mutating);
        Assert.Equal("cli", Assert.Single(MethodCatalogue.All, m => m.Name == "system.backup").Transport);
        Assert.Equal("cli", Assert.Single(MethodCatalogue.All, m => m.Name == "system.restore").Transport);
        Assert.DoesNotContain(MethodCatalogue.All, m => MethodCatalogue.ReservedMethods.Contains(m.Name));
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in MethodCatalogue.All.SelectMany(m => new[] { m.InputSchema, m.OutputSchema }))
        {
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
            Assert.True(identifiers.Add(schema["$id"]!.GetValue<string>()));
            Visit(schema, schema);
        }
        static void Visit(JsonNode? node, JsonObject root)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value)
                {
                    var reference = value.GetValue<string>();
                    Assert.StartsWith("#/$defs/", reference);
                    Assert.NotNull(root["$defs"]?[reference[8..]]);
                }
                foreach (var pair in obj) Visit(pair.Value, root);
            }
            else if (node is JsonArray array) foreach (var item in array) Visit(item, root);
        }
    }

    [Fact]
    public async Task Openapi_is_authenticated_and_declares_application_schemas_grants_and_mutation()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        using var anonymous = await client.GetAsync("/v1/openapi.json");
        await AssertError(anonymous, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await host.Login(client);
        var openapi = await client.GetFromJsonAsync<JsonObject>("/v1/openapi.json");
        var paths = (JsonObject)openapi!["paths"]!;
        foreach (var method in host.App.Services.GetRequiredService<IApplicationApi>().Methods.Where(m => m.Transport == "http"))
        {
            var operation = paths["/v1/methods/" + method.Name]?["post"];
            Assert.NotNull(operation);
            Assert.Equal(method.Grant, operation!["x-bos-grant"]!.GetValue<string>());
            Assert.Equal(method.Mutating, operation["x-bos-mutating"]!.GetValue<bool>());
            Assert.Equal("#/components/schemas/" + method.Name + ".Input", operation["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
            var schema = openapi["components"]!["schemas"]![method.Name + ".Input"]!;
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(method.InputSchema["required"], schema["required"]));
        }
        AssertOpenApiReferences(openapi);
    }

    [Fact]
    public void Build_snapshot_contains_all_http_and_auth_routes_with_resolved_response_metadata()
    {
        var document = JsonNode.Parse(ApiHost.OpenApi(MethodCatalogue.All).ToJsonString(new JsonSerializerOptions { WriteIndented = true }))!.AsObject();
        var paths = document["paths"]!.AsObject();
        var methods = MethodCatalogue.All.Where(method => method.Transport == "http").ToArray();
        Assert.Equal(53, methods.Length);
        string[] authPaths = ["/v1/auth/antiforgery", "/v1/auth/login", "/v1/auth/logout", "/v1/auth/change-password"];
        var expectedPaths = methods.Select(method => "/v1/methods/" + method.Name).Concat(authPaths);
        Assert.Equal(expectedPaths.Order(StringComparer.Ordinal), paths.Select(path => path.Key).Order(StringComparer.Ordinal));
        AssertOpenApiReferences(document);
        foreach (var method in methods)
        {
            var operation = paths["/v1/methods/" + method.Name]!["post"]!;
            Assert.Equal(method.Grant, operation["x-bos-grant"]!.GetValue<string>());
            Assert.Equal(method.Mutating, operation["x-bos-mutating"]!.GetValue<bool>());
            foreach (var status in new[] { "200", "202" })
            {
                var response = operation["responses"]![status]!["content"]!["application/json"]!["schema"]!;
                Assert.Contains("result", response["required"]!.AsArray().Select(value => value!.GetValue<string>()));
                Assert.Contains("meta", response["required"]!.AsArray().Select(value => value!.GetValue<string>()));
                Assert.Equal("#/components/schemas/" + method.Name + ".Output", response["properties"]!["result"]!["$ref"]!.GetValue<string>());
                Assert.Equal("#/components/schemas/Meta", response["properties"]!["meta"]!["$ref"]!.GetValue<string>());
            }
        }
        var meta = document["components"]!["schemas"]!["Meta"]!;
        Assert.Equal("traceId", Assert.Single(meta["required"]!.AsArray())!.GetValue<string>());
        Assert.Equal("uuid", meta["properties"]!["traceId"]!["format"]!.GetValue<string>());
        Assert.NotNull(paths["/v1/auth/antiforgery"]!["get"]);
        Assert.NotNull(paths["/v1/auth/login"]!["post"]!["responses"]!["200"]!["content"]);
        Assert.Null(paths["/v1/auth/logout"]!["post"]!["responses"]!["204"]!["content"]);
        Assert.Null(paths["/v1/auth/change-password"]!["post"]!["responses"]!["204"]!["content"]);
    }

    [Fact]
    public async Task Capabilities_response_conforms_to_catalogue_and_openapi_response_schemas()
    {
        await using var host = await RunningHost.Start();
        using var client = host.Client();
        using var login = await host.Login(client);
        using var response = await host.Rpc(client, "system.getCapabilities", new JsonObject(), false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var capabilities = (JsonObject)envelope["result"]!;
        var schema = Assert.Single(MethodCatalogue.All, m => m.Name == "system.getCapabilities").OutputSchema;
        AssertSchema(capabilities, schema, schema);
        Assert.Equal(MethodCatalogue.All.Count(m => m.Transport == "http"), ((JsonArray)capabilities["methods"]!).Count);
        Assert.Equal(MethodCatalogue.All.Count(m => m.Transport == "expression"), ((JsonArray)capabilities["helpers"]!).Count);

        var openapi = (await client.GetFromJsonAsync<JsonObject>("/v1/openapi.json"))!;
        var responseSchema = openapi["paths"]!["/v1/methods/system.getCapabilities"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        AssertSchema(envelope, responseSchema, openapi);

        // Prove that the lightweight evaluator follows nested references and enforces
        // required fields, types, enums, and closed DTOs rather than accepting any object.
        Action<JsonObject>[] corruptions =
        [
            value => value.Remove("helpers"),
            value => value["productVersion"] = 1,
            value => ((JsonObject)value["methods"]![0]!).Remove("contractVersion"),
            value => value["methods"]![0]!["mode"] = "command",
            value => ((JsonObject)value["helpers"]![0]!).Remove("outputSchema"),
            value => value["helpers"]![0]!["contractVersion"] = "1",
            value => value["helpers"]![0]!["unexpected"] = true,
            value => value["limits"]!["requestBytes"] = "262144",
            value => value["limits"]!["unexpected"] = 1
        ];
        foreach (var corrupt in corruptions)
        {
            var invalid = (JsonObject)capabilities.DeepClone();
            corrupt(invalid);
            Assert.NotEmpty(SchemaErrors(invalid, schema, schema));
            Assert.NotEmpty(SchemaErrors(invalid, openapi["components"]!["schemas"]!["system.getCapabilities.Output"]!, openapi));
        }
    }

    [Fact]
    public async Task Large_direct_records_replay_and_paging_deliver_complete_committed_results()
    {
        await using var host = await RunningHost.Start();
        var api = host.App.Services.GetRequiredService<IApplicationApi>();
        var draft = await api.InvokeAsync("types.create", new JsonObject { ["definition"] = new JsonObject
        {
            ["name"] = "LargeHttpRecord", ["fields"] = new JsonObject { ["chunks"] = new JsonObject
            {
                ["kind"] = "array", ["required"] = true, ["maxItems"] = 20,
                ["items"] = new JsonObject { ["kind"] = "string", ["maxLength"] = 12000 }
            } }
        } }, host.Admin, Guid.NewGuid().ToString("N"));
        await api.InvokeAsync("types.publish", new JsonObject
        {
            ["name"] = "LargeHttpRecord", ["expectedRevision"] = draft.Result!["draftRevision"]!.GetValue<int>()
        }, host.Admin, Guid.NewGuid().ToString("N"));
        using var client = host.Client();
        using var login = await host.Login(client);
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            var chunks = new JsonArray(Enumerable.Range(0, 10).Select(_ => (JsonNode)JsonValue.Create(new string((char)('a' + i), 10000))!).ToArray());
            var args = new JsonObject { ["type"] = "LargeHttpRecord", ["data"] = new JsonObject { ["chunks"] = chunks.DeepClone() } };
            var key = Guid.NewGuid().ToString("N");
            using var created = await host.Rpc(client, "records.create", args, true, key);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var record = (await created.Content.ReadFromJsonAsync<JsonObject>())!["result"]!;
            Assert.True(createdIds.Add(record["id"]!.GetValue<string>()));
            Assert.True(JsonNode.DeepEquals(chunks, record["data"]!["chunks"]));
            var schema = Assert.Single(MethodCatalogue.All, m => m.Name == "records.create").OutputSchema;
            AssertSchema(record, schema, schema);
            using var replay = await host.Rpc(client, "records.create", args, true, key);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(JsonNode.DeepEquals(record, (await replay.Content.ReadFromJsonAsync<JsonObject>())!["result"]));
        }
        using var listed = await host.Rpc(client, "records.list", new JsonObject { ["type"] = "LargeHttpRecord" }, false);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var bytes = await listed.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 256 * 1024);
        var page = JsonNode.Parse(bytes)!["result"]!;
        Assert.Equal(3, ((JsonArray)page["items"]!).Count);
        Assert.True(createdIds.SetEquals(((JsonArray)page["items"]!).Select(record => record!["id"]!.GetValue<string>())));
        Assert.Null(page["nextCursor"]);
        var pageSchema = Assert.Single(MethodCatalogue.All, m => m.Name == "records.list").OutputSchema;
        AssertSchema(page, pageSchema, pageSchema);
    }

    [Fact]
    public void Serve_options_reject_unknown_duplicate_and_plaintext_public_listeners()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.ParseServe(["--unknown", "value"]));
        Assert.Throws<ArgumentException>(() => HostOptions.ParseServe(["--listen", "http://0.0.0.0:8080"]));
        Assert.Throws<ArgumentException>(() => HostOptions.ParseServe(["--listen", "https://localhost:8080", "--listen", "https://localhost:8081"]));
        Assert.Equal("https://localhost:8080", HostOptions.ParseServe([]).Listen);
    }

    [Theory]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"limits\":{\"writeRequestsPerMinute\":61}}")]
    [InlineData("{\"limits\":{\"executionMilliseconds\":0}}")]
    [InlineData("{\"workspaceTimezone\":\"Not/A_Timezone\"}")]
    [InlineData("{\"allowedOrigins\":[\"*\"]}")]
    public void Configuration_rejects_unknown_settings_raised_limits_and_invalid_timezone_or_origins(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bos-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "bos.json"), json);
            Assert.ThrowsAny<Exception>(() => HostOptions.ParseServe([], directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Explicit_tls_rejects_unknown_settings_unsafe_files_and_secret_disclosure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bos-tls-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string actualPassword = "Test-Pfx-e\u0301-Secret!";
        const string wrongPassword = "Wrong-Pfx-Secret!";
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = certificate.Export(X509ContentType.Pkcs12, actualPassword);
        var certificatePath = Path.Combine(directory, "server.pfx");
        var passwordPath = Path.Combine(directory, "password.txt");
        var configPath = Path.Combine(directory, "bos.json");
        try
        {
            await File.WriteAllBytesAsync(certificatePath, pfx);
            await File.WriteAllTextAsync(passwordPath, wrongPassword + "\n");
            foreach (var invalid in new[]
            {
                new JsonObject { ["certificatePath"] = "server.pfx", ["password"] = actualPassword },
                new JsonObject { ["certificatePath"] = "server.pfx", ["unexpected"] = true },
                new JsonObject { ["passwordFile"] = "password.txt" },
                new JsonObject { ["certificatePath"] = "server.pfx", ["passwordFile"] = null }
            })
            {
                await File.WriteAllTextAsync(configPath, new JsonObject { ["https"] = invalid }.ToJsonString());
                var failure = Assert.ThrowsAny<Exception>(() => HostOptions.ParseServe([], directory));
                Assert.DoesNotContain(actualPassword, failure.ToString(), StringComparison.Ordinal);
            }

            await Refused("server.pfx", "password.txt"); // A wrong passphrase must not fall back to a development certificate.
            AssertPrivateFile(certificatePath);
            AssertPrivateFile(passwordPath);
            await File.WriteAllTextAsync(passwordPath, new string('x', 4097));
            await Refused("server.pfx", "password.txt");
            await File.WriteAllTextAsync(passwordPath, actualPassword);
            await File.WriteAllBytesAsync(certificatePath, new byte[1024 * 1024 + 1]);
            await Refused("server.pfx", "password.txt");
            await File.WriteAllBytesAsync(certificatePath, pfx);
            if (OperatingSystem.IsLinux())
            {
                File.CreateSymbolicLink(Path.Combine(directory, "linked.pfx"), certificatePath);
                await Refused("linked.pfx", "password.txt");
                File.CreateSymbolicLink(Path.Combine(directory, "linked-password.txt"), passwordPath);
                await Refused("server.pfx", "linked-password.txt");
                Directory.CreateDirectory(Path.Combine(directory, "real"));
                await File.WriteAllBytesAsync(Path.Combine(directory, "real", "server.pfx"), pfx);
                Directory.CreateSymbolicLink(Path.Combine(directory, "linked-directory"), Path.Combine(directory, "real"));
                await Refused("linked-directory/server.pfx", "password.txt");
            }
        }
        finally { Directory.Delete(directory, true); }

        async Task Refused(string cert, string password)
        {
            await File.WriteAllTextAsync(configPath, new JsonObject { ["https"] = new JsonObject { ["certificatePath"] = cert, ["passwordFile"] = password } }.ToJsonString());
            await using var app = ApiHost.Build(directory);
            using var logs = new CapturedLogs();
            app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
            Assert.Contains("Configured HTTPS certificate could not be loaded", failure.Message, StringComparison.Ordinal);
            foreach (var message in logs.Messages.Append(failure.ToString()))
            {
                Assert.DoesNotContain(actualPassword, message, StringComparison.Ordinal);
                Assert.DoesNotContain(wrongPassword, message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Explicit_private_pkcs12_configuration_serves_https_without_development_certificate_override()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bos-tls-valid-" + Guid.NewGuid().ToString("N"));
        var tlsDirectory = Path.Combine(directory, "tls");
        Directory.CreateDirectory(tlsDirectory);
        const string password = "  Test-Pfx-e\u0301-Secret!  ";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(tlsDirectory, "server.pfx");
        var passwordPath = Path.Combine(tlsDirectory, "password.txt");
        try
        {
            var pfx = certificate.Export(X509ContentType.Pkcs12, password);
            Assert.InRange(pfx.Length, 1, 8192);
            await File.WriteAllBytesAsync(certificatePath, pfx);
            await File.WriteAllTextAsync(passwordPath, password + "\r\n", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(certificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(passwordPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "bos.json"), new JsonObject
            {
                ["listen"] = "https://127.0.0.1:8080", ["https"] = new JsonObject
                {
                    ["certificatePath"] = "tls/server.pfx", ["passwordFile"] = passwordPath // Exercise relative and absolute references.
                }
            }.ToJsonString());
            var options = HostOptions.ParseServe([], directory);
            Assert.Equal(certificatePath, options.HttpsCertificatePath);
            Assert.Equal(passwordPath, options.HttpsPasswordFile);
            await using var app = ApiHost.Build(directory);
            app.Urls.Clear(); app.Urls.Add("https://127.0.0.1:0");
            // No ConfigureHttpsDefaults call: the real bos.json path must supply the certificate.
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == certificate.Thumbprint
                };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
                };
                var health = await client.GetFromJsonAsync<JsonObject>("/health/live");
                Assert.Equal("live", health!["status"]!.GetValue<string>());
                using var antiforgery = await client.GetAsync("/v1/auth/antiforgery");
                Assert.Equal(HttpStatusCode.OK, antiforgery.StatusCode);
                Assert.Contains(antiforgery.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
                AssertPrivateFile(certificatePath);
                AssertPrivateFile(passwordPath);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var owner = WindowsIdentity.GetCurrent().User!;
            var allowed = 0;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                Assert.Equal(owner, rule.IdentityReference);
                allowed++;
            }
            Assert.True(allowed > 0);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception));
        }
    }

    [Fact]
    public void Serve_and_offline_operations_share_exclusive_workspace_ownership()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bos-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var lease = new WorkspaceLease(directory))
            {
                var exception = Assert.Throws<BosException>(() => SqliteBackupAdapter.AcquireWorkspaceLock(directory).Dispose());
                Assert.Equal("STORAGE_BUSY", exception.Code);
            }
            using var offline = SqliteBackupAdapter.AcquireWorkspaceLock(directory);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] bytes;
        public ChunkedContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    private static JsonObject User(string name) => new() { ["userName"] = name, ["displayName"] = name, ["kind"] = "human" };

    private static void AssertOpenApiReferences(JsonObject document)
    {
        Visit(document);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference)
                {
                    var path = reference.GetValue<string>();
                    Assert.StartsWith("#/components/schemas/", path);
                    Assert.NotNull(document["components"]!["schemas"]![path[21..]]);
                }
                foreach (var pair in obj) Visit(pair.Value);
            }
            else if (node is JsonArray array) foreach (var item in array) Visit(item);
        }
    }

    // A dependency-free checker for the response-schema vocabulary used by this catalogue.
    // This is intentionally test-only, not another application argument-validation layer.
    private static void AssertSchema(JsonNode? value, JsonNode schema, JsonObject document)
    {
        var errors = SchemaErrors(value, schema, document);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static List<string> SchemaErrors(JsonNode? value, JsonNode schema, JsonObject document, string path = "$", int depth = 0)
    {
        if (depth > 128) throw new InvalidOperationException("Schema recursion limit exceeded.");
        if (schema is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed)) return allowed ? [] : [path + ": false schema"];
        var rule = schema as JsonObject ?? throw new InvalidOperationException("Expected an object or boolean schema.");
        var errors = new List<string>();
        if (rule["$ref"] is JsonValue reference)
        {
            var pointer = reference.GetValue<string>();
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) throw new InvalidOperationException("Expected a local schema reference.");
            JsonNode? target = document;
            foreach (var segment in pointer[2..].Split('/')) target = target?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            if (target is null) throw new InvalidOperationException("Unresolved schema reference: " + pointer);
            errors.AddRange(SchemaErrors(value, target, document, path, depth + 1));
        }
        if (rule["allOf"] is JsonArray all)
            foreach (var branch in all) errors.AddRange(SchemaErrors(value, branch!, document, path, depth + 1));
        if (rule["anyOf"] is JsonArray any && !any.Any(branch => SchemaErrors(value, branch!, document, path, depth + 1).Count == 0)) errors.Add(path + ": no anyOf branch matches");
        if (rule["oneOf"] is JsonArray one && one.Count(branch => SchemaErrors(value, branch!, document, path, depth + 1).Count == 0) != 1) errors.Add(path + ": expected exactly one matching branch");
        if (rule["not"] is { } not && SchemaErrors(value, not, document, path, depth + 1).Count == 0) errors.Add(path + ": forbidden schema matches");
        if (rule["type"] is { } type)
        {
            var matches = type is JsonArray types ? types.Any(t => TypeMatches(value, t!.GetValue<string>())) : TypeMatches(value, type.GetValue<string>());
            if (!matches) { errors.Add(path + ": incorrect type; expected " + type.ToJsonString()); return errors; }
        }
        if (rule.ContainsKey("const") && !JsonNode.DeepEquals(value, rule["const"])) errors.Add(path + ": incorrect constant");
        if (rule["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, value))) errors.Add(path + ": value is not in enum");
        if (value is JsonObject obj)
        {
            Bounds(obj.Count, "minProperties", "maxProperties");
            if (rule["required"] is JsonArray required)
                foreach (var name in required.Select(n => n!.GetValue<string>())) if (!obj.ContainsKey(name)) errors.Add(path + "." + name + ": required property missing");
            var properties = rule["properties"] as JsonObject;
            foreach (var (name, item) in obj)
            {
                if (rule["propertyNames"] is { } propertyNames) errors.AddRange(SchemaErrors(JsonValue.Create(name), propertyNames, document, path + "." + name, depth + 1));
                if (properties?.TryGetPropertyValue(name, out var declared) == true) errors.AddRange(SchemaErrors(item, declared!, document, path + "." + name, depth + 1));
                else if (rule["additionalProperties"] is { } additional) errors.AddRange(SchemaErrors(item, additional, document, path + "." + name, depth + 1));
            }
        }
        else if (value is JsonArray array)
        {
            Bounds(array.Count, "minItems", "maxItems");
            if (rule["items"] is { } items)
                for (var i = 0; i < array.Count; i++) errors.AddRange(SchemaErrors(array[i], items, document, path + "[" + i + "]", depth + 1));
            if (rule["uniqueItems"]?.GetValue<bool>() == true)
                for (var i = 0; i < array.Count; i++) if (array.Take(i).Any(previous => JsonNode.DeepEquals(previous, array[i]))) errors.Add(path + "[" + i + "]: duplicate item");
        }
        else if (value?.GetValueKind() == JsonValueKind.String)
        {
            var text = value.GetValue<string>();
            Bounds(text.EnumerateRunes().Count(), "minLength", "maxLength");
            if (rule["pattern"] is { } pattern && !Regex.IsMatch(text, pattern.GetValue<string>(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) errors.Add(path + ": pattern mismatch");
        }
        else if (value?.GetValueKind() == JsonValueKind.Number && (rule.ContainsKey("minimum") || rule.ContainsKey("maximum"))) Bounds(Number(value), "minimum", "maximum");
        return errors;

        void Bounds(decimal measured, string minimum, string maximum)
        {
            if (rule[minimum] is { } lower && measured < Number(lower)) errors.Add(path + ": below " + minimum);
            if (rule[maximum] is { } upper && measured > Number(upper)) errors.Add(path + ": above " + maximum);
        }
    }

    private static decimal Number(JsonNode value) => decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool TypeMatches(JsonNode? value, string type) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value?.GetValueKind() == JsonValueKind.String,
        "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value?.GetValueKind() == JsonValueKind.Number,
        "integer" => value?.GetValueKind() == JsonValueKind.Number && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && decimal.Truncate(number) == number,
        _ => throw new InvalidOperationException("Unsupported schema type: " + type)
    };

    private static async Task<JsonObject> AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        var error = Assert.IsType<JsonObject>(body!["error"]);
        Assert.Equal(code, error["code"]!.GetValue<string>());
        Assert.True(Guid.TryParseExact(error["traceId"]!.GetValue<string>(), "D", out _));
        Assert.IsType<JsonArray>(error["details"]);
        Assert.NotNull(error["message"]?.GetValue<string>());
        _ = error["retryable"]!.GetValue<bool>();
        Assert.Equal(5, error.Count);
        return error;
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        public const string Password = "Test-Password-2026!";
        public required WebApplication App { get; init; }
        public required string DirectoryPath { get; init; }
        public required X509Certificate2 Certificate { get; init; }
        public required Uri Address { get; init; }
        public required Caller Admin { get; init; }
        public required JsonObject AdminUser { get; init; }

        public static async Task<RunningHost> Start(string? configuration = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "bos-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (configuration is not null) await File.WriteAllTextAsync(Path.Combine(directory, "bos.json"), configuration);
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            WebApplication? app = null;
            try
            {
                app = ApiHost.Build(directory, ["--listen", "https://127.0.0.1:8080"]);
                app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
                app.Urls.Clear();
                app.Urls.Add("https://127.0.0.1:0");
                await app.StartAsync();
                var factory = app.Services.GetRequiredService<IUnitOfWorkFactory>();
                const string bootstrapPassword = "Bootstrap-Test-Password-2026!";
                var identity = app.Services.GetRequiredService<IIdentityService>();
                await using (var uow = await factory.BeginWriteAsync())
                {
                    await identity.BootstrapAsync(uow, bootstrapPassword);
                    await uow.CommitAsync();
                }
                var bootstrap = await identity.LoginAsync("Admin", bootstrapPassword);
                await app.Services.GetRequiredService<IApplicationApi>().InvokeAsync("users.setPassword", new JsonObject
                {
                    ["id"] = bootstrap.Caller.PrincipalId, ["newPassword"] = Password,
                    ["expectedRevision"] = bootstrap.User["revision"]!.GetValue<int>()
                }, bootstrap.Caller, Guid.NewGuid().ToString("N"));
                var login = await identity.LoginAsync("Admin", Password);
                return new RunningHost
                {
                    App = app, DirectoryPath = directory, Certificate = certificate, Admin = login.Caller, AdminUser = login.User,
                    Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
                };
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                certificate.Dispose();
                Directory.Delete(directory, true);
                throw;
            }
        }

        public HttpClient Client() => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
            // Only this loopback test client trusts this test-generated certificate.
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == Certificate.Thumbprint
        }) { BaseAddress = Address };

        public static async Task<(string Header, string Token)> Csrf(HttpClient client)
        {
            var json = await client.GetFromJsonAsync<JsonObject>("/v1/auth/antiforgery");
            return (json!["headerName"]!.GetValue<string>(), json["requestToken"]!.GetValue<string>());
        }

        public async Task<HttpResponseMessage> Login(HttpClient client, string password = Password)
        {
            var csrf = await Csrf(client);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login") { Content = JsonContent.Create(new { userName = "Admin", password }) };
            request.Headers.Add(csrf.Header, csrf.Token);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response;
        }

        public async Task<HttpResponseMessage> Rpc(HttpClient client, string method, JsonObject args, bool csrf, string? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/methods/" + method) { Content = JsonContent.Create(args) };
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
            if (csrf)
            {
                var token = await Csrf(client);
                request.Headers.Add(token.Header, token.Token);
            }
            return await client.SendAsync(request);
        }

        public async Task<string> ServiceToken()
        {
            var api = App.Services.GetRequiredService<IApplicationApi>();
            async Task<JsonNode> Call(string method, JsonObject args) => (await api.InvokeAsync(method, args, Admin, Guid.NewGuid().ToString("N"))).Result!;
            var service = await Call("users.create", new JsonObject { ["user"] = new JsonObject { ["userName"] = "Service" + Guid.NewGuid().ToString("N"), ["displayName"] = "Test Service", ["kind"] = "service" } });
            var id = service["id"]!.GetValue<string>();
            var assigned = await Call("roles.assign", new JsonObject { ["userId"] = id, ["roleId"] = AdminUser["roles"]![0]!.GetValue<string>(), ["expectedUserRevision"] = service["revision"]!.GetValue<int>() });
            await Call("users.update", new JsonObject { ["id"] = id, ["changes"] = new JsonObject { ["active"] = true }, ["expectedRevision"] = assigned["revision"]!.GetValue<int>() });
            var issued = await Call("tokens.create", new JsonObject { ["principalId"] = id, ["scopes"] = new JsonArray(new JsonObject { ["action"] = "identity.manage", ["resource"] = "*" }) });
            return issued["token"]!.GetValue<string>();
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Certificate.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
