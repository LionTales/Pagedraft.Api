using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// ONE repair for a model that answers in JSON mode but puts an expected ARRAY inside a STRING:
/// <c>{"suggestions":"[{\"original\":...}]","overallFeedback":""}</c>. Measured 2026-09-10 on the LineEdit
/// cloud spot-check (gemma-4-31b over OpenRouter, <c>response_format: json_object</c>): 2 of 6 replies had
/// that shape, and the typed deserialize throws on a string where a list is declared, so every parse path
/// returned null and the run persisted 0 suggestions with nothing on screen to say why. The recorded reply
/// opened the string with <c>": ["</c>, so ONE leading colon (plus whitespace) is tolerated as well.
///
/// <para>The rule, and its limits: a TOP-LEVEL property of <typeparamref name="T"/> whose declared type is a
/// collection (any non-string <see cref="IEnumerable"/>) and whose JSON value is a string is replaced by that
/// string parsed as JSON, ONLY when the trimmed string (after the optional colon) is itself a complete JSON
/// ARRAY. A string holding prose, a JSON object, a truncated array or anything else is left exactly as it
/// came, so the deserialize fails the way it failed before (a failure, never a fabricated list). Keys are
/// matched case-insensitively, mirroring the parse options. Nested fields are out of scope: the measured
/// leak was top-level.</para>
///
/// <para>Fail-safe: input that is not a JSON object is returned unchanged, and the text is re-serialized
/// only when a field was actually unwrapped, so a well-formed reply passes through byte-identical.</para>
/// </summary>
internal static class StringifiedArrayFields
{
    private static readonly ConcurrentDictionary<Type, string[]> ArrayKeyCache = new();

    /// <summary>Unwrap every string-valued array field of <typeparamref name="T"/> in
    /// <paramref name="json"/> (see the class docblock for the exact rule).</summary>
    internal static string Unwrap<T>(string json, ILogger? logger = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        var arrayKeys = ArrayKeysOf(typeof(T));
        if (arrayKeys.Length == 0) return json;

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return json;
        }
        if (obj is null) return json;

        var replacements = new List<(string Key, JsonArray Array)>();
        try
        {
            foreach (var kvp in obj)
            {
                if (!arrayKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase)) continue;
                if (kvp.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                if (TryParseArray(text) is { } array)
                    replacements.Add((kvp.Key, array));
            }
        }
        catch (ArgumentException)
        {
            // A DUPLICATED key (final-r01): JsonObject builds lazily and throws here, not at Parse, and not a
            // JsonException, so it would escape every caller's catch. The typed deserialize accepts the reply
            // (last value wins), so hand it on exactly as it came.
            return json;
        }

        if (replacements.Count == 0) return json;

        foreach (var (key, array) in replacements)
        {
            obj[key] = array;
            logger?.LogWarning(
                "Structured parse ({Type}): field '{Key}' arrived as a STRING holding a JSON array ({Count} items); unwrapped it instead of losing the list.",
                typeof(T).Name, key, array.Count);
        }

        return obj.ToJsonString();
    }

    /// <summary>The string as a JSON array, or null when it is anything else.</summary>
    internal static JsonArray? TryParseArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var candidate = text.Trim();
        if (candidate[0] == ':')
            candidate = candidate[1..].TrimStart();
        if (candidate.Length == 0 || candidate[0] != '[') return null;

        try
        {
            return JsonNode.Parse(candidate) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ArrayKeysOf(Type type) => ArrayKeyCache.GetOrAdd(type, static t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray());
}
