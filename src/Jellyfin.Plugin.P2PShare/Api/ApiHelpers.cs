using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.P2PShare.Api;

/// <summary>
/// JSON helpers so the plugin's wire format is independent of Jellyfin's MVC JSON settings.
/// </summary>
internal static class ApiHelpers
{
    public static ContentResult Json(object value, int status = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(value, RemoteJson.Options),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status,
    };

    public static ContentResult Error(int status, string message) => Json(new RemoteError { Error = message }, status);

    public static ContentResult ValidationErrors(IReadOnlyList<string> errors)
        => Json(new { error = string.Join(" ", errors), errors }, StatusCodes.Status400BadRequest);

    public static async Task<T?> ReadBodyAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, RemoteJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
