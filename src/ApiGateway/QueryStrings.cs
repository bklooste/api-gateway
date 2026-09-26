using System.Text;

using Microsoft.AspNetCore.Http;

namespace ApiGateway;

internal static class QueryStrings
{
    /// <summary>Query parameters derived from the trusted identity headers; a client may not supply them.</summary>
    private static readonly HashSet<string> TrustedParams =
        new(["brand", "customerId", "userId"], StringComparer.OrdinalIgnoreCase);

    // Parses the raw query (preserving duplicates), explodes comma-separated values (Swagger's non-exploded
    // array style) into repeated keys for the downstream string[] binder, then appends brand/customerId/userId.
    internal static (string Key, string? Value)[] GetQueryString(HttpRequest request, string? brand, string? userId)
    {
        var queryString = new List<(string, string?)>();

        if (request.QueryString.HasValue)
        {
            var pairs = request.QueryString.Value!.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kvp = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(kvp[0]);
                var value = kvp.Length > 1 ? Uri.UnescapeDataString(kvp[1]) : null;

                if (value != null && value.Contains(','))
                {
                    foreach (var v in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        queryString.Add((key, v.Trim()));
                }
                else
                {
                    queryString.Add((key, value));
                }
            }
        }

        // The identity-derived params OVERWRITE anything the client sent under the same name.
        //
        // These previously used add-if-absent, so a caller sending ?userId=someone-else had BOTH
        // values forwarded, theirs first — and a downstream binder taking the first match would bind
        // the attacker's. That is the same duplicate-value hazard CopyRequest guards against for
        // identity headers; the query string had the opposite behaviour.
        queryString.RemoveAll(x => TrustedParams.Contains(x.Item1));

        if (brand != null)
            queryString.Add(("brand", brand));
        if (userId != null)
        {
            queryString.Add(("customerId", userId));
            queryString.Add(("userId", userId));
        }

        return [.. queryString];
    }

    internal static string AddQueryParams(string url, (string paramName, string? paramValue)[] queryParams)
    {
        if (queryParams.Length == 0)
            return url;

        var sb = new StringBuilder();
        bool hasExistingQuery = url.Contains('?');
        bool endsWithSeparator = url.EndsWith('?') || url.EndsWith('&');

        foreach (var (paramName, paramValue) in queryParams)
        {
            if (paramValue == null)
                continue;

            if (sb.Length > 0 || (hasExistingQuery && !endsWithSeparator))
                sb.Append('&');
            else if (!hasExistingQuery && !endsWithSeparator)
                sb.Append('?');

            sb.Append(Uri.EscapeDataString(paramName));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(paramValue));
            endsWithSeparator = false;
        }

        return url + sb.ToString();
    }
}
