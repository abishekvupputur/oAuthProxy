using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using OAuthProxy.Core.Diagnostics;
using Yarp.ReverseProxy.Transforms;

namespace OAuthProxy.Core.Proxy;

/// <summary>
/// Writes the credential into the request body, for the upstreams that want it there rather
/// than in a header or the query string.
///
/// Unlike the other two placements this cannot be done in passing: the body has to be buffered,
/// parsed, edited, and re-serialized, which is only safe for bodies that are small, declared,
/// and in a shape this understands. Everything else is refused outright and reported, because a
/// half-rewritten body reaching an upstream is worse than an honest "could not attach" line in
/// the activity log next to the 401 it explains.
/// </summary>
internal static class RequestBodyCredentialInjector
{
    /// <summary>
    /// Anything larger is forwarded untouched. Buffering is the whole cost of this placement,
    /// and a route that streams file uploads must not have them held in memory.
    /// </summary>
    private const long MaxBufferedBodyBytes = 1024 * 1024;

    private enum BodyKind { Unsupported, Json, Form }

    public static async ValueTask<bool> TryInjectAsync(
        RequestTransformContext context,
        string fieldName,
        string value,
        ActivityLog activityLog,
        string? credentialName)
    {
        var request = context.HttpContext.Request;

        if (Refuse(request) is { } reason)
        {
            activityLog.Log($"BODY-AUTH '{credentialName}' not attached to {request.Method} {request.Path} — {reason}");
            return false;
        }

        var kind = Classify(request.ContentType);
        var buffer = new MemoryStream((int)(request.ContentLength ?? 0));
        await request.Body.CopyToAsync(buffer, context.HttpContext.RequestAborted);
        var body = Encoding.UTF8.GetString(buffer.ToArray());

        var (rewritten, mediaType) = kind == BodyKind.Json
            ? (RewriteJson(body, fieldName, value), $"{MediaTypeOf(request.ContentType)}; charset=utf-8")
            : (RewriteForm(body, fieldName, value), "application/x-www-form-urlencoded");

        if (rewritten is null)
        {
            // The only way to get here is a JSON body that is not an object — an array or a bare
            // literal has nowhere to put a named field.
            activityLog.Log($"BODY-AUTH '{credentialName}' not attached to {request.Method} {request.Path} "
                            + "— JSON body is not an object, so it has no field to set");

            // Still forwarded, just unauthenticated: the body was consumed above, so it has to
            // be put back either way.
            Apply(context, request, buffer.ToArray(), request.ContentType);
            return false;
        }

        Apply(context, request, Encoding.UTF8.GetBytes(rewritten), mediaType);
        return true;
    }

    /// <summary>Returns null when the body can be rewritten, or a short reason when it cannot.</summary>
    private static string? Refuse(HttpRequest request)
    {
        if (Classify(request.ContentType) == BodyKind.Unsupported)
        {
            return $"content type '{request.ContentType ?? "(none)"}' is not JSON or form-urlencoded";
        }

        // Decoding, editing and re-encoding a compressed body would mean re-implementing the
        // upstream's content coding; forwarding it as-is at least keeps the request valid.
        if (request.Headers.ContainsKey("Content-Encoding"))
        {
            return "body is content-encoded";
        }

        // Chunked bodies have no declared length, so there is no way to know whether buffering
        // is affordable before starting to read — and a partially read body cannot be undone.
        if (request.ContentLength is not { } length)
        {
            return "body has no Content-Length (chunked or streamed)";
        }

        return length > MaxBufferedBodyBytes
            ? $"body is {length} bytes, over the {MaxBufferedBodyBytes}-byte limit for body injection"
            : null;
    }

    private static BodyKind Classify(string? contentType)
    {
        var mediaType = MediaTypeOf(contentType);

        if (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return BodyKind.Form;
        }

        if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return BodyKind.Unsupported;
        }

        // The body is decoded as UTF-8 below. JSON is UTF-8 by default and an explicit
        // "charset=utf-8" is common, but anything else would be decoded wrongly and re-emitted
        // as mojibake, so leave those alone.
        var charset = ParameterOf(contentType, "charset");
        return charset is null
               || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
               || charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase)
            ? BodyKind.Json
            : BodyKind.Unsupported;
    }

    private static string MediaTypeOf(string? contentType) =>
        contentType is null ? "" : contentType.Split(';')[0].Trim();

    private static string? ParameterOf(string? contentType, string name)
    {
        if (contentType is null) return null;

        foreach (var part in contentType.Split(';').Skip(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>Sets the field on a JSON object body, or returns null if the body is not one.</summary>
    private static string? RewriteJson(string body, string fieldName, string value)
    {
        JsonNode? parsed;
        try
        {
            // An empty body is legitimate here: a POST with "Content-Type: application/json" and
            // nothing in it becomes the object carrying just the credential.
            parsed = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject json) return null;

        // Assignment, not Add: a caller-supplied field of the same name is overwritten rather
        // than duplicated, so the upstream cannot be shown two candidate credentials.
        json[fieldName] = JsonValue.Create(value);
        return json.ToJsonString();
    }

    private static string RewriteForm(string body, string fieldName, string value)
    {
        var fields = QueryHelpers.ParseQuery(body);
        fields[fieldName] = new StringValues(value);

        return string.Join("&", fields.SelectMany(field =>
            field.Value.Select(v => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(v ?? "")}")));
    }

    /// <summary>
    /// Puts the rewritten body back where the forwarder will pick it up.
    ///
    /// Two edits, because the outgoing request is assembled from two places. The body itself has
    /// to go back on HttpContext.Request — YARP streams the outgoing content from there and
    /// throws outright ("Replacing the YARP outgoing request HttpContent is not supported") if a
    /// transform hands it a different HttpContent. The *content headers*, however, were already
    /// copied onto that content before this transform ran, so a Content-Length left at the
    /// caller's original value fails the forward with "More bytes received than the specified
    /// Content-Length" the moment the rewritten body turns out to be longer.
    /// </summary>
    private static void Apply(RequestTransformContext context, HttpRequest request, byte[] bytes, string? mediaType)
    {
        request.Body = new MemoryStream(bytes, writable: false);
        request.ContentLength = bytes.Length;
        if (mediaType is not null) request.ContentType = mediaType;

        if (context.ProxyRequest.Content is not { } content) return;

        content.Headers.ContentLength = bytes.Length;

        if (mediaType is not null)
        {
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        }
    }
}
