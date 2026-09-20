using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaSparx.Backend;

var builder =
    WebApplication.CreateBuilder(args);

builder.Services
    .ConfigureHttpJsonOptions(
        options =>
        {
            options.SerializerOptions
                    .PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase;

            options.SerializerOptions
                    .DictionaryKeyPolicy =
                null;
        });

builder.Services
    .AddHttpClient<PublicFortniteSources>(
        client =>
        {
            client.Timeout =
                TimeSpan.FromMinutes(3);

            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd(
                    "NovaSparx/1.0 (+FNAA)");
        });

builder.Services
    .AddSingleton<LiveProviderService>();

builder.Services
    .AddSingleton<MeshResolverService>();

builder.Services
    .AddSingleton<AssetInspectorService>();

builder.Services
    .AddSingleton<TextureService>();

builder.Services
    .AddSingleton<PreviewResolverService>();

builder.Services
    .AddSingleton<ClientMeshPackageService>();

builder.Services
    .AddSingleton<NovaRequestDispatcher>();

// The hosted service is metadata-only. Asset warmup/parsing belongs to browser
// workers or offline indexing tools, never to the Back4App process.

builder.Services
    .AddHostedService<NovaLinkHostedService>();

builder.WebHost
    .ConfigureKestrel(
        options =>
        {
            options.AddServerHeader =
                false;

            options.Limits
                    .MaxRequestBodySize =
                10 * 1024;

            options.Limits
                    .MaxRequestLineSize =
                8 * 1024;

            options.Limits
                    .MaxRequestHeadersTotalSize =
                16 * 1024;

            options.Limits
                    .MaxRequestHeaderCount =
                64;

            options.Limits
                    .KeepAliveTimeout =
                TimeSpan.FromMinutes(3);

            options.Limits
                    .RequestHeadersTimeout =
                TimeSpan.FromSeconds(30);
        });

var app =
    builder.Build();

static bool Authorized(
    HttpRequest request)
{
    // NOVASPARX_SHARED_TOKEN is the canonical secret for the edge,
    // direct backend and AutoLink. Previous values remain valid during
    // rotation so the edge and backend can be updated without downtime.
    var configured =
        new[]
        {
            "NOVASPARX_SHARED_TOKEN",
            "NOVASPARX_BACKEND_TOKEN",
            "NOVASPARX_LINK_TOKEN",
            "NOVASPARX_SHARED_TOKEN_PREVIOUS",
            "NOVASPARX_BACKEND_TOKEN_PREVIOUS",
            "NOVASPARX_LINK_TOKEN_PREVIOUS"
        };

    var expectedTokens =
        new List<string>();

    foreach (var name in configured)
    {
        var token =
            Environment.GetEnvironmentVariable(
                name)?
                .Trim();

        if (
            string.IsNullOrWhiteSpace(
                token) ||
            Encoding.UTF8
                .GetByteCount(token) >
                4096)
        {
            continue;
        }

        var duplicate = false;

        foreach (var existing in expectedTokens)
        {
            if (string.Equals(
                    existing,
                    token,
                    StringComparison.Ordinal))
            {
                duplicate = true;
                break;
            }
        }

        if (!duplicate)
        {
            expectedTokens.Add(
                token);
        }
    }

    // Public /health stays unauthenticated, but every /v1 direct operation is
    // closed unless at least one private token is explicitly configured.
    if (expectedTokens.Count == 0)
    {
        return false;
    }

    var authorization =
        request.Headers
            .Authorization
            .ToString();

    const string prefix =
        "Bearer ";

    if (!authorization.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var supplied =
        authorization[
            prefix.Length..]
            .Trim();

    if (
        string.IsNullOrEmpty(
            supplied) ||
        Encoding.UTF8
            .GetByteCount(
                supplied) >
            4096)
    {
        return false;
    }

    var suppliedBytes =
        Encoding.UTF8
            .GetBytes(supplied);

    foreach (var expected in expectedTokens)
    {
        var expectedBytes =
            Encoding.UTF8
                .GetBytes(expected);

        if (
            expectedBytes.Length ==
                suppliedBytes.Length &&
            CryptographicOperations
                .FixedTimeEquals(
                    expectedBytes,
                    suppliedBytes))
        {
            return true;
        }
    }

    return false;
}

static Dictionary<string, string>
    QueryDictionary(
        HttpRequest request)
{
    const int maxQueryParameters =
        16;

    const int maxQueryKeyLength =
        64;

    const int maxQueryValueLength =
        4096;

    if (
        request.Query.Count >
        maxQueryParameters)
    {
        throw new BadHttpRequestException(
            "Too many query parameters.",
            StatusCodes.Status400BadRequest);
    }

    var result =
        new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

    foreach (var pair in request.Query)
    {
        var key =
            pair.Key.Trim();

        var value =
            pair.Value.ToString();

        if (
            key.Length == 0 ||
            key.Length >
                maxQueryKeyLength ||
            value.Length >
                maxQueryValueLength)
        {
            throw new BadHttpRequestException(
                "Query parameter exceeds the allowed size.",
                StatusCodes.Status400BadRequest);
        }

        if (!result.ContainsKey(
                key))
        {
            result[key] =
                value;
        }
    }

    return result;
}

static async Task DispatchHttpAsync(
    HttpContext context,
    NovaRequestDispatcher dispatcher,
    string route,
    bool requireAuth,
    CancellationToken cancellationToken)
{
    if (requireAuth &&
        !Authorized(
            context.Request))
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;

        context.Response.ContentType =
            "application/json; charset=utf-8";

        await context.Response
            .WriteAsJsonAsync(
                new
                {
                    state = "error",
                    error = "Unauthorized."
                },
                cancellationToken);

        return;
    }

    Dictionary<string, string>
        query;

    try
    {
        query =
            QueryDictionary(
                context.Request);
    }
    catch (BadHttpRequestException ex)
    {
        context.Response.StatusCode =
            ex.StatusCode;

        context.Response.ContentType =
            "application/json; charset=utf-8";

        context.Response.Headers[
            "X-Content-Type-Options"] =
            "nosniff";

        context.Response.Headers[
            "Referrer-Policy"] =
            "no-referrer";

        context.Response.Headers
            .CacheControl =
            "no-store";

        await context.Response
            .WriteAsJsonAsync(
                new
                {
                    state = "error",
                    error =
                        "Invalid request query."
                },
                cancellationToken);

        return;
    }

    var response =
        await dispatcher.DispatchAsync(
            context.Request.Method,
            route,
            query,
            cancellationToken);

    context.Response.StatusCode =
        response.Status;

    context.Response.ContentType =
        response.ContentType;

    context.Response.Headers[
        "X-Content-Type-Options"] =
        "nosniff";

    context.Response.Headers[
        "Referrer-Policy"] =
        "no-referrer";

    if (
        response.Status ==
        StatusCodes.Status503ServiceUnavailable)
    {
        context.Response.Headers[
            "Retry-After"] =
            "1";
    }

    // Direct /v1 responses are authenticated and must never become shared
    // intermediary cache entries. The public Cloudflare edge owns any
    // explicitly safe client-facing cache policy.
    context.Response.Headers
        .CacheControl =
        "private, no-store";

    if (response.Status == 200 &&
        response.ContentType.StartsWith(
            "image/",
            StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers
            .ETag =
            $"\"{Convert.ToHexString(
                SHA256.HashData(
                    response.Body))
                .ToLowerInvariant()}\"";
    }

    if (response.Body.Length > 0)
    {
        await context.Response.Body
            .WriteAsync(
                response.Body,
                cancellationToken);
    }
}

app.MapGet(
    "/",
    () =>
    {
        return Results.Json(
            new
            {
                ok = true,
                service =
                    "NovaSparx.Backend",
                version =
                    LiveProviderService
                        .BackendVersion,
                schema =
                    "novasparx.preview.v1",
                serverHeavyProcessing = false,
                universalAssetInspection =
                    false,
                staticMeshPreview =
                    false,
                skeletalMeshPreview =
                    false,
                clientRendered3d =
                    true,
                clientMeshBinary =
                    ClientMeshPackageService.Schema,
                serverSide3dRendering =
                    false,
                endpoints =
                    new[]
                    {
                        "/health",
                        "/v1/health",
                        "/v1/warmup",
                        "/v1/refresh",
                        "/v1/resolve?path=...",
                        "/v1/preview?path=...",
                        "/v1/client-mesh?path=...",
                        "/v1/inspect?path=...",
                        "/v1/references?path=...",
                        "/v1/texture?path=..."
                    }
            });
    });

app.MapGet(
    "/health",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/health",
            requireAuth: false,
            cancellationToken));

app.MapGet(
    "/v1/health",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/health",
            requireAuth: true,
            cancellationToken));

app.MapPost(
    "/v1/warmup",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/warmup",
            requireAuth: true,
            cancellationToken));

app.MapPost(
    "/v1/refresh",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/refresh",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/resolve",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/resolve",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/preview",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/preview",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/client-mesh",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/client-mesh",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/inspect",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/inspect",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/references",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/references",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/texture",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/texture",
            requireAuth: true,
            cancellationToken));

app.Run();
