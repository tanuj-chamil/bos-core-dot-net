using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Flows;
using Bos.Identity;
using Bos.Sqlite;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using KestrelServerOptions = Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions;
using Microsoft.Net.Http.Headers;

namespace Bos.Host;

public static class ApiHost
{
    private const string TraceKey = "Bos.TraceId";
    // Generated discovery documents describe the entire catalogue; this separate bound
    // does not change request, record, or flow/business-output budgets.
    private const int ToolingResponseBytes = 2 * 1024 * 1024;
    private const int AttachmentContentBytes = 25 * 1024 * 1024;
    private const int AttachmentRequestBytes = AttachmentContentBytes + 1024 * 1024;
    private sealed class AttachmentWriteMarker;
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        MaxDepth = CanonicalJson.MaxDepth + 2, // Allow the transport envelope around a valid application result.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Builds the production composition; storage initialization/recovery precede listener startup.</summary>
    public static WebApplication Build(string dataDirectory, string[]? args = null)
    {
        var options = HostOptions.ParseServe(args ?? [], dataDirectory);
        // Do not load environment/command-line configuration providers: bos.json is the strict source.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(ApiHost).Assembly.FullName, EnvironmentName = Environments.Production
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls(options.Listen);
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = AttachmentRequestBytes;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        if (options.HttpsCertificatePath is not null)
        {
            // DI owns the ephemeral private key until the stopped host is disposed.
            builder.Services.AddSingleton<X509Certificate2>(_ => options.LoadHttpsCertificate());
            builder.Services.AddOptions<KestrelServerOptions>().Configure<IServiceProvider>((kestrel, services) =>
                kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = services.GetRequiredService<X509Certificate2>()));
        }
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.PostConfigure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.None);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<WorkspaceLease>(_ => new WorkspaceLease(options.DataDirectory));
        builder.Services.AddSingleton<IClock, Bos.Contracts.SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, UuidGenerator>();
        builder.Services.AddSingleton<IUnitOfWorkFactory>(sp =>
        {
            _ = sp.GetRequiredService<WorkspaceLease>();
            return new SqliteUnitOfWorkFactory(Path.Combine(options.DataDirectory, "bos.db"));
        });
        builder.Services.AddSingleton<IQueryEngine>(sp =>
        {
            _ = sp.GetRequiredService<WorkspaceLease>();
            return new SqliteQueryEngine(HostOptions.CursorKey(options.DataDirectory));
        });
        builder.Services.AddSingleton<IAuthorization, Authorization>();
        builder.Services.AddSingleton<IIdentityService, IdentityService>();
        builder.Services.AddSingleton<IOperationRegistry>(_ =>
        {
            var registry = new OperationRegistry();
            MaterialRequestOperations.Register(registry);
            return registry;
        });
        builder.Services.AddSingleton<IFlowCompiler, FlowCompiler>();
        builder.Services.AddSingleton<IFlowInterpreter, FlowInterpreter>();
        builder.Services.AddSingleton<FlowRunner>();
        builder.Services.AddSingleton<IFlowRunner>(sp => sp.GetRequiredService<FlowRunner>());
        builder.Services.AddSingleton<IApplicationApi>(sp =>
        {
            var runner = sp.GetRequiredService<FlowRunner>();
            runner.WorkspaceTimezone = options.WorkspaceTimezone;
            var application = new ApplicationApi(sp.GetRequiredService<IUnitOfWorkFactory>(), sp.GetRequiredService<IAuthorization>(),
                sp.GetRequiredService<IIdentityService>(), sp.GetRequiredService<IQueryEngine>(), sp.GetRequiredService<IOperationRegistry>(),
                sp.GetRequiredService<IFlowCompiler>(), runner, sp.GetRequiredService<IClock>(), sp.GetRequiredService<IIdGenerator>())
                { WorkspaceTimezone = options.WorkspaceTimezone };
            runner.Dispatcher = application;
            return application;
        });
        builder.Services.AddSingleton<IAttachmentApi>(sp => (IAttachmentApi)sp.GetRequiredService<IApplicationApi>());
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = AttachmentRequestBytes;
            form.MultipartHeadersLengthLimit = 16 * 1024;
            form.ValueCountLimit = 1;
        });
        builder.Services.AddHostedService<StorageLifetime>();
        builder.Services.AddSingleton<StorageLifetimeState>();
        var keys = Path.Combine(options.DataDirectory, "keys");
        builder.Services.AddDataProtection().SetApplicationName("BOS.Core.v1");
        builder.Services.AddOptions<KeyManagementOptions>().Configure<ILoggerFactory>((o, logger) =>
            o.XmlRepository = new OwnerOnlyKeyRepository(keys, logger));
        builder.Services.AddBosAuthentication();
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = "__Host-Bos.Antiforgery";
            o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        });
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
        {
            if (options.AllowedOrigins.Length != 0)
                policy.WithOrigins(options.AllowedOrigins).WithMethods("GET", "POST").WithHeaders("Content-Type", "Authorization", "Idempotency-Key", "X-CSRF-TOKEN")
                    .WithExposedHeaders("Retry-After", "Content-Disposition", "ETag", "Accept-Ranges", "Content-Range").AllowCredentials();
        }));
        builder.Services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = 429;
            rate.OnRejected = async (context, ct) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 60;
                context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                await Error(context.HttpContext, new BosException("RATE_LIMITED", "Request rate limit exceeded.", 429, retryable: true), ct);
            };
            rate.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => Window(options.LoginAttemptsPerMinute)));
            rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (context.User.Identity?.IsAuthenticated != true || id is null) return RateLimitPartition.GetNoLimiter("anonymous");
                var method = context.GetEndpoint()?.Metadata.GetMetadata<MethodDescriptor>();
                var write = method?.Mutating == true || context.GetEndpoint()?.Metadata.GetMetadata<AttachmentWriteMarker>() is not null ||
                    context.Request.Path.StartsWithSegments("/v1/auth") && HttpMethods.IsPost(context.Request.Method);
                return RateLimitPartition.GetFixedWindowLimiter(id + (write ? ":write" : ":read"), _ => Window(write ? options.WriteRequestsPerMinute : options.ReadRequestsPerMinute));
            });
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Items[TraceKey] = Guid.NewGuid().ToString("D");
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var attachmentUpload = IsAttachmentUpload(context);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(attachmentUpload ? TimeSpan.FromMinutes(2) : TimeSpan.FromMilliseconds(options.ExecutionMilliseconds));
            var disconnected = context.RequestAborted;
            context.RequestAborted = deadline.Token;
            try
            {
                var requestLimit = attachmentUpload ? AttachmentRequestBytes : options.RequestBodyBytes;
                if (context.Request.ContentLength > requestLimit)
                    throw new BosException("REQUEST_TOO_LARGE", "Request body exceeds the byte limit.", 413);
                if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/v1") && !attachmentUpload) ValidateMediaType(context);
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = requestLimit;
                await next(context);
                if (context.Response.StatusCode >= 400 && !context.Response.HasStarted && context.Response.ContentLength is null)
                    await Error(context, StatusError(context.Response.StatusCode), disconnected);
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                var error = ex switch
                {
                    BosException known => known,
                    BadHttpRequestException bad when bad.StatusCode == 413 => new BosException("REQUEST_TOO_LARGE", "Request body exceeds the byte limit.", 413),
                    BadHttpRequestException => new BosException("INVALID_REQUEST", "Invalid HTTP request.", 400),
                    AntiforgeryValidationException => new BosException("FORBIDDEN", "Antiforgery validation failed.", 403),
                    OperationCanceledException when !disconnected.IsCancellationRequested => new BosException("DEADLINE_EXCEEDED", "Request deadline exceeded.", 422),
                    OperationCanceledException => new BosException("REQUEST_CANCELLED", "Request was cancelled.", 400),
                    _ => new BosException("INTERNAL_ERROR", "The request could not be completed.", 500)
                };
                if (error.Status == 429 && !context.Response.Headers.ContainsKey("Retry-After")) context.Response.Headers.RetryAfter = "1";
                if (error.Status >= 500) app.Logger.LogError("HTTP request failed with {Code}; trace {TraceId}", error.Code, Trace(context));
                if (!disconnected.IsCancellationRequested) await Error(context, error, disconnected);
            }
            finally { context.RequestAborted = disconnected; }
        });
        app.UseRouting();
        app.UseCors();
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch =>
        {
            branch.UseAuthentication();
            branch.UseRateLimiter();
        });

        app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
        app.MapGet("/health/ready", (StorageLifetimeState state) => Results.Json(new { status = state.Ready ? "ready" : "notReady" }, statusCode: state.Ready ? 200 : 503));
        app.MapGet("/v1/auth/antiforgery", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            RequireHttps(context);
            var tokens = antiforgery.GetAndStoreTokens(context);
            await WriteJson(context, new JsonObject { ["requestToken"] = tokens.RequestToken, ["headerName"] = tokens.HeaderName }, 200, context.RequestAborted);
        });
        app.MapPost("/v1/auth/login", async (HttpContext context, IAntiforgery antiforgery, IIdentityService identity) =>
        {
            RequireHttps(context);
            var input = await Body(context, options);
            JsonArgs.Only(input, "userName", "password");
            var name = JsonArgs.String(input, "userName");
            var password = Secret(input, "password");
            await antiforgery.ValidateRequestAsync(context);
            if (name.Length is < 1 or > 64 || password.Length is < 1 or > 128)
                throw new BosException("UNAUTHENTICATED", "Invalid credentials.", 401);
            (Caller Caller, JsonObject User) login;
            try
            {
                login = await identity.LoginAsync(name, password, context.RequestAborted);
                if (login.User["kind"]?.GetValue<string>() != "human") throw new BosException("UNAUTHENTICATED", "Invalid credentials.", 401);
            }
            catch (BosException e) when (e.Status is 400 or 401 or 403 or 404 or 409 or 422 or 429)
            { throw new BosException("UNAUTHENTICATED", "Invalid credentials.", 401); }
            await context.SignInAsync(Authentication.CookieScheme, Authentication.CookiePrincipal(login.Caller), new AuthenticationProperties
            {
                IsPersistent = false, AllowRefresh = false, IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
            await Result(context, login.User);
        }).RequireRateLimiting("login");
        app.MapPost("/v1/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            RequireHttps(context);
            RequireCookie(context);
            JsonArgs.Only(await Body(context, options));
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(Authentication.CookieScheme);
            context.Response.StatusCode = 204;
        });
        app.MapPost("/v1/auth/change-password", async (HttpContext context, IAntiforgery antiforgery, IIdentityService identity) =>
        {
            RequireHttps(context);
            var caller = RequireCookie(context);
            var input = await Body(context, options);
            JsonArgs.Only(input, "currentPassword", "newPassword");
            var current = Secret(input, "currentPassword");
            var replacement = Secret(input, "newPassword", 12);
            await antiforgery.ValidateRequestAsync(context);
            if (current.Length is < 1 or > 128) throw new BosException("UNAUTHENTICATED", "Invalid credentials.", 401);
            try { await identity.ChangePasswordAsync(caller, current, replacement, context.RequestAborted); }
            catch (BosException e) when (e.Status is 401 or 403 or 404)
            { throw new BosException("UNAUTHENTICATED", "Invalid credentials.", 401); }
            await context.SignOutAsync(Authentication.CookieScheme);
            context.Response.StatusCode = 204;
        });

        // One explicit endpoint per declared method supplies mutation metadata before rate admission.
        // There is no reflection fallback and no independent host-side authorization catalogue.
        IApplicationApi application;
        try { application = app.Services.GetRequiredService<IApplicationApi>(); }
        catch { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        foreach (var descriptor in application.Methods.Where(m => m.Transport == "http"))
        {
            var method = descriptor;
            app.MapPost("/v1/methods/" + method.Name, async (HttpContext context, IAntiforgery antiforgery) =>
            {
                var caller = Authentication.RequireCaller(context);
                var input = await Body(context, options);
                if (method.Mutating && Authentication.IsCookie(context) &&
                    !(method.Name == "operations.call" && IsReadOperation(input, context.RequestServices.GetRequiredService<IOperationRegistry>())))
                    await antiforgery.ValidateRequestAsync(context);
                if (method.Name == "users.setPassword") RequireHttps(context);
                var key = context.Request.Headers["Idempotency-Key"];
                if (key.Count > 1) throw new BosException("INVALID_REQUEST", "Only one idempotency key is allowed.", 400);
                var response = await application.InvokeAsync(method.Name, input, caller, key.Count == 0 ? null : key[0], context.RequestAborted);
                await ApplicationResult(context, response, tooling: method.Name == "system.getCapabilities");
            }).WithMetadata(method);
        }
        app.MapPost("/v1/attachments", async (HttpContext context, IAntiforgery antiforgery, IAttachmentApi attachmentApi) =>
        {
            if (context.Request.QueryString.HasValue) throw new BosException("INVALID_REQUEST", "Query arguments are not accepted.", 400);
            var caller = Authentication.RequireCaller(context);
            if (Authentication.IsCookie(context)) await antiforgery.ValidateRequestAsync(context);
            var key = context.Request.Headers["Idempotency-Key"];
            if (key.Count != 1) throw new BosException("INVALID_REQUEST", "Exactly one idempotency key is required.", 400);
            if (!context.Request.HasFormContentType || !context.Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new BosException("UNSUPPORTED_MEDIA_TYPE", "A multipart/form-data body is required.", 415);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Count != 0 || form.Files.Count != 1 || form.Files[0].Name != "file")
                throw new BosException("INVALID_REQUEST", "The multipart body must contain exactly one file part named 'file' and no fields.", 400);
            var file = form.Files[0];
            if (file.Length > AttachmentContentBytes) throw new BosException("REQUEST_TOO_LARGE", "Attachment exceeds the 25 MiB limit.", 413);
            await using var stream = new MemoryStream((int)file.Length);
            await file.CopyToAsync(stream, context.RequestAborted);
            var result = await attachmentApi.UploadAttachmentAsync(file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                stream.ToArray(), caller, key[0]!, context.RequestAborted);
            await Result(context, result);
        }).WithMetadata(new AttachmentWriteMarker());
        app.MapGet("/v1/attachments/{id}/content", async (HttpContext context, string id, IAttachmentApi attachmentApi) =>
        {
            if (context.Request.QueryString.HasValue) throw new BosException("INVALID_REQUEST", "Query arguments are not accepted.", 400);
            var caller = Authentication.RequireCaller(context);
            var download = await attachmentApi.DownloadAttachmentAsync(id, caller, context.RequestAborted);
            var etag = new EntityTagHeaderValue('"' + download.Metadata["sha256"]!.GetValue<string>() + '"');
            await Results.File(download.Content, download.Metadata["contentType"]!.GetValue<string>(),
                download.Metadata["fileName"]!.GetValue<string>(), entityTag: etag, enableRangeProcessing: true).ExecuteAsync(context);
        });
        app.MapPost("/v1/methods/{methodName}", async (HttpContext context, string methodName) =>
        {
            var caller = Authentication.RequireCaller(context);
            var input = await Body(context, options);
            // The application owns reserved/unknown names; none are advertised or dispatched by reflection.
            var response = await application.InvokeAsync(methodName, input, caller, null, context.RequestAborted);
            await ApplicationResult(context, response);
        });
        app.MapGet("/v1/openapi.json", async (HttpContext context) =>
        {
            var caller = Authentication.RequireCaller(context);
            var capabilities = await application.InvokeAsync("system.getCapabilities", new JsonObject(), caller, null, context.RequestAborted);
            await WriteJson(context, OpenApi(application.Methods, capabilities.Result), 200, context.RequestAborted, tooling: true);
        });
        return app;
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };

    private static Caller RequireCookie(HttpContext context)
    {
        var caller = Authentication.RequireCaller(context);
        if (!Authentication.IsCookie(context)) throw new BosException("UNAUTHENTICATED", "A human session is required.", 401);
        return caller;
    }

    private static bool IsReadOperation(JsonObject input, IOperationRegistry registry)
    {
        try
        {
            var name = JsonArgs.String(input, "name");
            int? version = input.ContainsKey("contractVersion") ? JsonArgs.Int(input, "contractVersion") : null;
            return registry.Resolve(name, version).Descriptor.Mode == "read";
        }
        catch (BosException)
        {
            // Classification never discloses registry errors or bypasses application authorization.
            // Invalid/unavailable targets retain the conservative state-changing request policy.
            return false;
        }
    }

    private static bool IsAttachmentUpload(HttpContext context) => HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/v1/attachments";

    private static void RequireHttps(HttpContext context)
    {
        if (!context.Request.IsHttps) throw new BosException("INVALID_REQUEST", "HTTPS is required for this endpoint.", 400);
    }

    private static async Task<JsonObject> Body(HttpContext context, HostOptions options)
    {
        if (context.Request.QueryString.HasValue) throw new BosException("INVALID_REQUEST", "Query arguments are not accepted.", 400);
        ValidateMediaType(context);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (count == 0) break;
            if (bytes.Length + count > options.RequestBodyBytes) throw new BosException("REQUEST_TOO_LARGE", "Request body exceeds the byte limit.", 413);
            bytes.Write(buffer, 0, count);
        }
        try
        {
            return CanonicalJson.Parse(StrictUtf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), preserveSecrets: true) as JsonObject
                ?? throw new BosException("INVALID_REQUEST", "The request body must be a JSON object.", 400);
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        { throw new BosException("INVALID_REQUEST", "The request body must be a strict JSON object.", 400); }
    }

    private static void ValidateMediaType(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Charset.HasValue && !HeaderUtilities.RemoveQuotes(contentType.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            contentType.Parameters.Count > 1 || contentType.Parameters.Any(p => !p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) ||
            context.Request.Headers.ContainsKey("Content-Encoding"))
            throw new BosException("UNSUPPORTED_MEDIA_TYPE", "An application/json UTF-8 body is required.", 415);
    }

    private static string Trace(HttpContext context) => (string?)context.Items[TraceKey] ?? context.TraceIdentifier;

    private static string Secret(JsonObject input, string name, int minimum = 0)
    {
        if (input[name] is not JsonValue value || !value.TryGetValue<string>(out var secret) || secret.Length < minimum || secret.Length > 128)
            throw new BosException("INVALID_REQUEST", "Invalid password argument.", 400, name);
        return secret;
    }

    private static async Task ApplicationResult(HttpContext context, ApiResult response, bool tooling = false)
    {
        if (response.Status == 409 && response.Result is JsonObject receipt && receipt["runId"] is JsonValue id &&
            id.TryGetValue<string>(out var runId) && Guid.TryParseExact(runId, "D", out _) && receipt["status"]?.GetValue<string>() == "interrupted")
        {
            await Error(context, new BosException("RUN_INTERRUPTED", "The run was interrupted; its idempotency key remains reserved.", 409)
            { Details = [new FieldError("runId", "RUN_INTERRUPTED", runId)] }, context.RequestAborted);
            return;
        }
        if (response.Status is not (200 or 202)) throw new InvalidOperationException("Invalid application success status.");
        await Result(context, response.Result, response.Status, tooling);
    }

    private static Task Result(HttpContext context, JsonNode? value, int status = 200, bool tooling = false)
    {
        // The transaction owner enforces semantic output budgets before commit. The HTTP
        // adapter must not turn an already committed large record into an output-limit error.
        return WriteJson(context, new JsonObject { ["result"] = value?.DeepClone(), ["meta"] = new JsonObject { ["traceId"] = Trace(context) } }, status, context.RequestAborted, tooling);
    }

    private static Task Error(HttpContext context, BosException error, CancellationToken ct = default)
    {
        var details = new JsonArray(error.Details.Select(d => (JsonNode)new JsonObject { ["path"] = d.Path, ["code"] = d.Code, ["message"] = d.Message }).ToArray());
        if (details.Count == 0 && error.Path.Length != 0) details.Add(new JsonObject { ["path"] = error.Path, ["code"] = error.Code, ["message"] = error.Message });
        return WriteJson(context, new JsonObject { ["error"] = new JsonObject
        {
            ["code"] = error.Code, ["message"] = error.Message, ["details"] = details, ["traceId"] = Trace(context), ["retryable"] = error.Retryable
        } }, error.Code == "REVISION_CONFLICT" ? StatusCodes.Status412PreconditionFailed : error.Status, ct);
    }

    private static async Task WriteJson(HttpContext context, JsonNode value, int status, CancellationToken ct = default, bool tooling = false)
    {
        byte[]? bytes = null;
        if (tooling)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, ResponseJson);
            if (bytes.Length > ToolingResponseBytes) throw new BosException("OUTPUT_INVALID", "Discovery document exceeds the response byte limit.", 500);
        }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        if (bytes is not null)
        {
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, ct);
        }
        else await JsonSerializer.SerializeAsync(context.Response.Body, value, ResponseJson, ct);
    }

    private static BosException StatusError(int status) => status switch
    {
        400 => new("INVALID_REQUEST", "Invalid HTTP request.", status),
        401 => new("UNAUTHENTICATED", "Authentication is required.", status),
        403 => new("FORBIDDEN", "Access is forbidden.", status),
        404 => new("NOT_FOUND", "Endpoint not found.", status),
        405 => new("METHOD_NOT_ALLOWED", "HTTP method is not allowed.", status),
        413 => new("REQUEST_TOO_LARGE", "Request body exceeds the byte limit.", status),
        415 => new("UNSUPPORTED_MEDIA_TYPE", "Unsupported media type.", status),
        429 => new("RATE_LIMITED", "Request rate limit exceeded.", status, retryable: true),
        _ => new("INTERNAL_ERROR", "The request could not be completed.", status, retryable: status == 503)
    };

    /// <summary>Renders the complete HTTP catalogue for build-time tooling, without constructing a host.</summary>
    public static JsonObject OpenApi(IReadOnlyList<MethodDescriptor> methods) => OpenApi(methods, new JsonObject
    {
        ["methods"] = new JsonArray(methods.Select(method => (JsonNode)new JsonObject { ["name"] = method.Name }).ToArray())
    });

    private static JsonObject OpenApi(IReadOnlyList<MethodDescriptor> methods, JsonNode? capabilities)
    {
        var visible = capabilities?["methods"] as JsonArray ?? throw new InvalidOperationException("Capabilities must declare visible methods.");
        var names = visible.Select(m => m?["name"]?.GetValue<string>()).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        var paths = new JsonObject();
        var schemas = new JsonObject
        {
            ["Meta"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("traceId"), ["properties"] = new JsonObject { ["traceId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } } },
            ["ErrorResponse"] = JsonNode.Parse("""{"type":"object","additionalProperties":false,"required":["error"],"properties":{"error":{"type":"object","additionalProperties":false,"required":["code","message","details","traceId","retryable"],"properties":{"code":{"type":"string"},"message":{"type":"string"},"details":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["path","code","message"],"properties":{"path":{"type":"string"},"code":{"type":"string"},"message":{"type":"string"}}}},"traceId":{"type":"string","format":"uuid"},"retryable":{"type":"boolean"}}}}}""")
        };
        foreach (var method in methods.Where(m => m.Transport == "http" && names.Contains(m.Name)))
        {
            var inputName = method.Name + ".Input";
            var outputName = method.Name + ".Output";
            schemas[inputName] = OpenApiSchema(method.InputSchema, schemas);
            schemas[outputName] = OpenApiSchema(method.OutputSchema, schemas);
            JsonObject Input() => new() { ["$ref"] = "#/components/schemas/" + inputName };
            JsonObject Output() => new() { ["$ref"] = "#/components/schemas/" + outputName };
            paths["/v1/methods/" + method.Name] = new JsonObject { ["post"] = new JsonObject
            {
                ["operationId"] = method.Name, ["x-bos-grant"] = method.Grant, ["x-bos-mutating"] = method.Mutating,
                ["x-bos-readOnly"] = !method.Mutating,
                ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }, method.Mutating
                    ? new JsonObject { ["cookieAuth"] = new JsonArray(), ["csrfToken"] = new JsonArray() }
                    : new JsonObject { ["cookieAuth"] = new JsonArray() }),
                ["parameters"] = method.Mutating ? new JsonArray(new JsonObject
                {
                    ["name"] = "Idempotency-Key", ["in"] = "header", ["required"] = method.Name != "operations.call",
                    ["description"] = method.Name == "operations.call" ? "Required when the selected operation is a write; read operations do not reserve receipts." : "Required for this command.",
                    ["schema"] = new JsonObject { ["type"] = "string", ["minLength"] = 16, ["maxLength"] = 128, ["pattern"] = "^[\\x20-\\x7e]+$" }
                }) : new JsonArray(),
                ["requestBody"] = new JsonObject { ["required"] = true, ["content"] = JsonContent(Input()) },
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject { ["description"] = "Completed", ["content"] = JsonContent(ResponseSchema(Output())) },
                    ["202"] = new JsonObject { ["description"] = "Invocation is still running", ["content"] = JsonContent(ResponseSchema(Output())) },
                    ["409"] = new JsonObject { ["description"] = "Conflict; RUN_INTERRUPTED includes the reserved runId in error.details.", ["content"] = JsonContent(new JsonObject { ["$ref"] = "#/components/schemas/ErrorResponse" }) },
                    ["default"] = new JsonObject { ["description"] = "Safe error", ["content"] = JsonContent(new JsonObject { ["$ref"] = "#/components/schemas/ErrorResponse" }) }
                }
            } };
        }
        if (!schemas.ContainsKey("Bos.Attachment"))
        {
            var attachmentMethod = methods.Single(method => method.Name == "attachments.list");
            _ = OpenApiSchema(attachmentMethod.OutputSchema, schemas);
        }
        AddAttachmentPaths(paths);
        AddAuthenticationPaths(paths);
        return new JsonObject
        {
            ["openapi"] = "3.1.0", ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
            ["info"] = new JsonObject { ["title"] = "BOS Core", ["version"] = "1.0.0" }, ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" },
                    ["cookieAuth"] = new JsonObject { ["type"] = "apiKey", ["in"] = "cookie", ["name"] = "__Host-Bos.Session" },
                    ["csrfToken"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-CSRF-TOKEN" }
                },
                ["schemas"] = schemas
            }
        };
    }

    private static JsonObject JsonContent(JsonNode schema) => new() { ["application/json"] = new JsonObject { ["schema"] = schema } };

    // Discovery is bounded too. Promote identical catalogue definitions once rather than
    // duplicating recursive DTO trees in every method's request and response document.
    private static JsonObject OpenApiSchema(JsonObject document, JsonObject components)
    {
        var root = (JsonObject)document.DeepClone();
        if (root.Remove("$defs", out var definitions) && definitions is JsonObject objects)
        {
            foreach (var (name, definition) in objects)
            {
                var value = definition!.DeepClone();
                Rewrite(value);
                var key = "Bos." + name;
                if (components.TryGetPropertyValue(key, out var previous))
                {
                    if (!JsonNode.DeepEquals(previous, value)) throw new InvalidOperationException("Conflicting catalogue schema definitions.");
                }
                else components[key] = value;
            }
        }
        root.Remove("$schema"); root.Remove("$id");
        Rewrite(root);
        return root;

        static void Rewrite(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                    obj["$ref"] = "#/components/schemas/Bos." + reference[8..];
                foreach (var pair in obj) Rewrite(pair.Value);
            }
            else if (node is JsonArray array) foreach (var item in array) Rewrite(item);
        }
    }

    private static JsonObject ResponseSchema(JsonNode result) => new()
    {
        ["type"] = "object", ["required"] = new JsonArray("result", "meta"),
        ["properties"] = new JsonObject { ["result"] = result, ["meta"] = new JsonObject { ["$ref"] = "#/components/schemas/Meta" } }
    };

    private static void AddAttachmentPaths(JsonObject paths)
    {
        var bearerOrCookie = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }, new JsonObject { ["cookieAuth"] = new JsonArray() });
        var writeSecurity = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }, new JsonObject { ["cookieAuth"] = new JsonArray(), ["csrfToken"] = new JsonArray() });
        var errors = new JsonObject { ["description"] = "Safe error", ["content"] = JsonContent(new JsonObject { ["$ref"] = "#/components/schemas/ErrorResponse" }) };
        paths["/v1/attachments"] = new JsonObject { ["post"] = new JsonObject
        {
            ["operationId"] = "attachments.upload", ["x-bos-grant"] = "", ["x-bos-mutating"] = true,
            ["security"] = writeSecurity, ["parameters"] = new JsonArray(
                new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string", ["minLength"] = 16, ["maxLength"] = 128 } }),
            ["requestBody"] = new JsonObject { ["required"] = true, ["content"] = new JsonObject { ["multipart/form-data"] = new JsonObject { ["schema"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("file"),
                ["properties"] = new JsonObject { ["file"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } }
            } } } },
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject { ["description"] = "Uploaded to private pending storage", ["content"] = JsonContent(ResponseSchema(new JsonObject { ["$ref"] = "#/components/schemas/Bos.Attachment" })) },
                ["default"] = errors.DeepClone()
            }
        } };
        paths["/v1/attachments/{id}/content"] = new JsonObject { ["get"] = new JsonObject
        {
            ["operationId"] = "attachments.download", ["x-bos-grant"] = "record.read", ["x-bos-readOnly"] = true,
            ["security"] = bearerOrCookie, ["parameters"] = new JsonArray(new JsonObject
            {
                ["name"] = "id", ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/Bos.Uuid" }
            }),
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject { ["description"] = "Attachment content", ["content"] = new JsonObject { ["application/octet-stream"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } } } },
                ["default"] = errors
            }
        } };
    }

    private static void AddAuthenticationPaths(JsonObject paths)
    {
        static JsonObject Text() => new() { ["type"] = "string" };
        static JsonObject Object(JsonObject properties, params string[] required) => new()
        {
            ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
        static JsonObject Operation(string name, JsonObject? input, JsonNode? result, bool csrf, bool session, bool envelope = true)
        {
            var security = new JsonObject();
            if (session) security["cookieAuth"] = new JsonArray();
            if (csrf) security["csrfToken"] = new JsonArray();
            var operation = new JsonObject
            {
                ["operationId"] = name, ["description"] = "HTTPS only. Cookie sessions expire after eight hours without sliding renewal.",
                ["security"] = new JsonArray(security),
                ["responses"] = new JsonObject
                {
                    [result is null ? "204" : "200"] = result is null ? new JsonObject { ["description"] = "Completed; no body" }
                        : new JsonObject { ["description"] = "Completed", ["content"] = JsonContent(envelope ? ResponseSchema(result) : result) },
                    ["default"] = new JsonObject { ["description"] = "Safe error", ["content"] = JsonContent(new JsonObject { ["$ref"] = "#/components/schemas/ErrorResponse" }) }
                }
            };
            if (input is not null) operation["requestBody"] = new JsonObject { ["required"] = true, ["content"] = JsonContent(input) };
            return operation;
        }
        paths["/v1/auth/antiforgery"] = new JsonObject { ["get"] = Operation("auth.antiforgery", null,
            Object(new JsonObject { ["requestToken"] = Text(), ["headerName"] = Text() }, "requestToken", "headerName"), false, false, false) };
        var user = Object(new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }, ["userName"] = Text(), ["displayName"] = Text(),
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("human", "service") },
            ["active"] = new JsonObject { ["type"] = "boolean" }, ["roles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
            ["revision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
        }, "id", "userName", "displayName", "kind", "active", "roles", "revision");
        paths["/v1/auth/login"] = new JsonObject { ["post"] = Operation("auth.login",
            Object(new JsonObject { ["userName"] = Text(), ["password"] = new JsonObject { ["type"] = "string", ["format"] = "password", ["maxLength"] = 128 } }, "userName", "password"), user, true, false) };
        paths["/v1/auth/logout"] = new JsonObject { ["post"] = Operation("auth.logout", Object(new JsonObject()), null, true, true) };
        paths["/v1/auth/change-password"] = new JsonObject { ["post"] = Operation("auth.changePassword", Object(new JsonObject
        {
            ["currentPassword"] = new JsonObject { ["type"] = "string", ["format"] = "password", ["maxLength"] = 128 },
            ["newPassword"] = new JsonObject { ["type"] = "string", ["format"] = "password", ["minLength"] = 12, ["maxLength"] = 128 }
        }, "currentPassword", "newPassword"), null, true, true) };
    }

    private sealed class OwnerOnlyKeyRepository(string directory, ILoggerFactory logger) : IXmlRepository
    {
        private readonly FileSystemXmlRepository repository = new(new DirectoryInfo(directory), logger);
        public IReadOnlyCollection<System.Xml.Linq.XElement> GetAllElements()
        {
            Protect();
            return repository.GetAllElements();
        }
        public void StoreElement(System.Xml.Linq.XElement element, string friendlyName)
        {
            HostOptions.ProtectDirectory(directory);
            repository.StoreElement(element, friendlyName);
            Protect();
        }
        private void Protect()
        {
            HostOptions.ProtectDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory)) HostOptions.ProtectFile(file);
        }
    }

    private sealed class StorageLifetimeState { public bool Ready { get; set; } }

    private sealed class StorageLifetime(IServiceProvider services, HostOptions options) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<WorkspaceLease>();
            _ = services.GetService<X509Certificate2>();
            HostOptions.ProtectDirectory(Path.Combine(options.DataDirectory, "keys"));
            _ = services.GetRequiredService<IApplicationApi>();
            await services.GetRequiredService<IUnitOfWorkFactory>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<IFlowRunner>().RecoverAsync(cancellationToken);
            services.GetRequiredService<StorageLifetimeState>().Ready = true;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            services.GetRequiredService<StorageLifetimeState>().Ready = false;
            return Task.CompletedTask;
        }
    }
}
