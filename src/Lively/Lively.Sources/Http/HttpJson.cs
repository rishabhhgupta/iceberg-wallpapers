using System.Net;
using System.Text.Json;

namespace Lively.Sources.Http;

internal static class HttpJson
{
    /// <summary>
    /// GETs a URL and parses the body as JSON, mapping every failure onto a
    /// <see cref="SourceException"/> with a person-readable message.
    ///
    /// Cancellation is distinguished from a timeout on purpose: scrolling away
    /// should be silent, while a dead network should raise an error banner.
    /// </summary>
    public static async Task<JsonDocument> GetAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new SourceException(SourceErrorKind.Cancelled, "Request cancelled.");
        }
        catch (TaskCanceledException ex)
        {
            throw new SourceException(SourceErrorKind.Network, "The source took too long to respond.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(SourceErrorKind.Network, "Could not reach the source. Check your connection.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new SourceException(SourceErrorKind.RateLimited, "The source is rate limiting. Try again in a minute.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                throw new SourceException(SourceErrorKind.Http, $"The source returned an error ({code}).");
            }

            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new SourceException(SourceErrorKind.Cancelled, "Request cancelled.");
            }
            catch (JsonException ex)
            {
                throw new SourceException(SourceErrorKind.Parse, "The source sent something unexpected.", ex);
            }
        }
    }

    public static string? String(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static int? Int(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), out var i) => i,
            _ => null,
        };
    }

    public static int IntOrDefault(JsonElement element, string property, int fallback) =>
        Int(element, property) ?? fallback;

    public static long? Long(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.String when long.TryParse(value.GetString(), out var l) => l,
            _ => null,
        };
    }

    public static JsonElement? Object(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Object ? value : null;
    }

    /// <summary>Adds query parameters, skipping null/blank values.</summary>
    public static string WithQuery(string baseUrl, params (string Key, string? Value)[] parameters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        if (parts.Count == 0) return baseUrl;
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + separator + string.Join("&", parts);
    }
}
