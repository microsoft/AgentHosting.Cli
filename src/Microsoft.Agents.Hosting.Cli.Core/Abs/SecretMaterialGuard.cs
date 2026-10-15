using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// No-secret enforcement for the ABS-scaffolding output contract. A faithful C# port of the shipped
/// TypeScript <c>Redaction</c> guard, it walks the serialized report and flags any non-empty
/// string leaf that either sits under a known credential key name or looks like raw credential
/// material (a bearer token, a JWT, or a PEM block). Detection is deliberately conservative and
/// two-pronged so a benign envelope field that merely contains the substring "secret"
/// (<c>secretReference</c>, <c>secretMaterialReturned</c>) is never flagged.
///
/// It is an internal invariant guard, not user-facing input validation: the report builder calls it
/// on its own output so a leak fails loudly (<see cref="SecretMaterialException"/>, exit 1) here
/// instead of reaching stdout. The thrown message lists the offending <em>paths</em> only — never the
/// values.
/// </summary>
internal static class SecretMaterialGuard
{
    // The camelCase serialization used both here and by the host renderer, so the flagged dotted
    // paths match the emitted JSON exactly (e.g. abs.appId, changes[1].target).
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // A JWT (header.payload.signature, base64url) — how ARM/Graph bearer tokens look on the wire.
    private static readonly Regex JwtPattern =
        new("^eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static readonly Regex BearerPattern =
        new("^bearer\\s+\\S", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Exact (lowercased) field names that carry credential material. Full names so secretReference /
    // secretMaterialReturned (legitimate no-secret envelope fields) never match.
    private static readonly HashSet<string> SecretKeyNames = new(StringComparer.Ordinal)
    {
        "secret",
        "secrettext",
        "password",
        "passwordcredential",
        "clientsecret",
        "client_secret",
        "access_token",
        "accesstoken",
        "refresh_token",
        "refreshtoken",
        "id_token",
        "idtoken",
        "authorization",
        "assertion",
        "clientassertion",
        "client_assertion",
        "certificatepassword",
        "privatekey",
        "private_key",
        "pfx",
    };

    /// <summary>
    /// Throws <see cref="SecretMaterialException"/> when <paramref name="value"/> serializes to any
    /// secret material. The message names the offending paths only.
    /// </summary>
    public static void AssertNoSecretMaterial(object value, string context)
    {
        var hits = FindSecretMaterial(value);
        if (hits.Count > 0)
        {
            throw new SecretMaterialException(
                $"Refusing to emit {context}: secret material detected at {string.Join(", ", hits)}. " +
                "ABS scaffolding output must be no-secret by default; return a secret reference instead.");
        }
    }

    /// <summary>
    /// Walks the camelCase-serialized <paramref name="value"/> and returns the dotted paths at which
    /// secret material was detected. Only non-empty string leaves are flagged; the returned paths never
    /// include the offending value.
    /// </summary>
    public static IReadOnlyList<string> FindSecretMaterial(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), CamelCase);
        var hits = new List<string>();
        Walk(node, string.Empty, keyName: null, hits);
        return hits;
    }

    private static void Walk(JsonNode? node, string path, string? keyName, List<string> hits)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    Walk(child, path.Length == 0 ? key : $"{path}.{key}", key, hits);
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", keyName: null, hits);
                }

                break;

            case JsonValue jsonValue when jsonValue.TryGetValue(out string? text) && text is not null:
                if (text.Length > 0 &&
                    ((keyName is not null && IsSecretKey(keyName)) || LooksLikeSecretValue(text)))
                {
                    hits.Add(path.Length == 0 ? "<root>" : path);
                }

                break;
        }
    }

    private static bool IsSecretKey(string key) => SecretKeyNames.Contains(key.ToLowerInvariant());

    private static bool LooksLikeSecretValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (BearerPattern.IsMatch(trimmed))
        {
            return true;
        }

        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return true;
        }

        return JwtPattern.IsMatch(trimmed);
    }
}
