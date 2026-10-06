using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Utils;

namespace Leaves;

// Shared HTTP response plumbing for every mod's IHttpListener: serve a file, write JSON, prefix-match a path, guard against traversal.
// All static; callers keep their own routing and directory layout.
public static class HttpUtils
{
    // True when the request path is under this mod's prefix.
    public static bool HasPrefix(HttpContext context, string prefix)
    {
        return context.Request.Path.Value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    // A GET whose path equals `path`, ignoring case and an optional trailing slash.
    // Lets a route match both "/foo/ui" and "/foo/ui/" without spelling out both.
    public static bool IsGet(HttpContext context, string path)
    {
        if (context.Request.Method != "GET")
        {
            return false;
        }
        string actual = (context.Request.Path.Value ?? "").TrimEnd('/');
        return string.Equals(actual, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    // A GET whose path ends with `suffix`, ignoring case, for routes matched by their tail (e.g. "/api/status").
    public static bool IsGetSuffix(HttpContext context, string suffix)
    {
        if (context.Request.Method != "GET")
        {
            return false;
        }
        return (context.Request.Path.Value ?? "").EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    // A POST whose path equals `path`, ignoring case and an optional trailing slash.
    public static bool IsPost(HttpContext context, string path)
    {
        if (context.Request.Method != "POST")
        {
            return false;
        }
        string actual = (context.Request.Path.Value ?? "").TrimEnd('/');
        return string.Equals(actual, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    // Reads a request body as text, transparently zlib-decompressing it.
    // SPT's client RequestHandler compresses POST bodies (zlib, 0x78 header), and an IHttpListener receives them raw - so a plain read yields garbage ('x' = 0x78, the zlib magic byte).
    // Falls back to treating the bytes as UTF-8 when they aren't zlib (e.g. a hand-made curl call), so both work.
    public static async Task<string> ReadBody(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        byte[] raw = buffer.ToArray();
        if (raw.Length == 0)
        {
            return "";
        }

        if (raw.Length >= 2 && raw[0] == 0x78)
        {
            try
            {
                using var input = new MemoryStream(raw);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            catch
            {
                // Not actually zlib after all - fall through and read as plain text.
            }
        }

        return Encoding.UTF8.GetString(raw);
    }

    // Serves one static file off disk. A missing file 500s with a rebuild hint, since it means a broken deploy, not a bad request.
    public static async Task ServeFile(
        HttpContext context,
        string fullPath,
        string contentType
    )
    {
        if (!File.Exists(fullPath))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"{Path.GetFileName(fullPath)} not found at {fullPath}. Rebuild the mod so assets deploy.");
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        await context.Response.WriteAsync(await File.ReadAllTextAsync(fullPath));
    }

    // Streams a JSON file straight through, no parse+reserialize, for handing back a large file verbatim.
    // Caller must have already path-checked (see TryResolveUnderRoot).
    public static async Task ServeRawJson(HttpContext context, string fullPath)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(await File.ReadAllTextAsync(fullPath));
    }

    // Serializes an object payload as the whole response body.
    // payload is object on purpose: System.Text.Json resolves the runtime type, so anonymous types serialize with their real properties.
    public static async Task WriteJson(
        HttpContext context,
        int statusCode,
        object payload,
        JsonSerializerOptions? options = null
    )
    {
        await WriteJsonRaw(context, statusCode, JsonSerializer.Serialize(payload, options));
    }

    // Writes an already-serialized JSON string as the response body, for callers using SPT's JsonUtil (game _id/_name field names) or a cached string.
    public static async Task WriteJsonRaw(
        HttpContext context,
        int statusCode,
        string json
    )
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(json);
    }

    // ============================================================
    // Web config editor - the two halves of a mod's Kind="config" route pair.
    // GET  /{mod}/config  -> WriteConfigView<TConfig>
    // POST /{mod}/config  -> read body, call the mod, WriteConfigApply(result)
    // ============================================================

    // The GET payload: the form schema (from TConfig's attributes) plus the live values.
    public sealed record ConfigView(object Schema, object Current);

    // The POST result. Messages carries validation errors when Ok is false, or notes
    // ("restart for the trader-list change") when it is true.
    public sealed record ConfigApplyResult(bool Ok, IReadOnlyList<string> Messages)
    {
        public static ConfigApplyResult Success(params string[] notes)
        {
            return new ConfigApplyResult(true, notes);
        }

        public static ConfigApplyResult Failure(params string[] errors)
        {
            return new ConfigApplyResult(false, errors);
        }
    }

    // Serialized with SPT's options so MongoId and friends round-trip. currentConfig is the live
    // config object; TConfig only drives the schema.
    public static async Task WriteConfigView<TConfig>(HttpContext context, object currentConfig)
    {
        ConfigView view = new(Utils.Instance.describeConfig<TConfig>(), currentConfig);
        await WriteJson(context, StatusCodes.Status200OK, view, JsonUtil.JsonSerializerOptionsNoIndent);
    }

    // The config-apply response: 200 when Ok, 422 otherwise. The one place that mapping lives, so
    // every mod's POST /{mod}/config answers the same way.
    public static async Task WriteConfigApply(HttpContext context, ConfigApplyResult result)
    {
        int status = result.Ok ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity;
        await WriteJson(context, status, result);
    }

    // Path-traversal guard: resolves relative under root, returns false if the result escapes (e.g. a ../ in the query); full is set but unusable when false.
    // The separator suffix on root stops "/rootEVIL" passing as under "/root".
    public static bool TryResolveUnderRoot(
        string root,
        string relative,
        out string full
    )
    {
        string rootFull = Path.GetFullPath(root);
        full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
