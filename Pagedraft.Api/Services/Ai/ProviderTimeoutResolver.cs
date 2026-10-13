using System.Collections.Concurrent;
using System.Globalization;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// The ONE place a provider HTTP timeout is read from configuration, shared by the Ollama client
/// (<c>Ai:Providers:Ollama:TimeoutMinutes</c>, default 30) and the cloud clients
/// (<c>Ai:Providers:{provider}:TimeoutSeconds</c>, default 300).
///
/// Policy (before this, Ollama THREW at client creation on a garbage value while the cloud path silently fell
/// back, so one typo failed every local call but not a cloud one):
///  - absent                                    -> the default, silently;
///  - empty / unparsable / zero / negative      -> the default plus a warning naming the key;
///  - above <see cref="MaxTimeout"/>            -> clamped to <see cref="MaxTimeout"/> plus a warning
///    (24 h; <see cref="HttpClient.Timeout"/> itself rejects anything past ~24.8 days, so an absurd value would
///    otherwise throw at client creation);
///  - anything in (0, max]                      -> used as written.
/// </summary>
public static class ProviderTimeoutResolver
{
    /// <summary>The one clamp: no provider ceiling may exceed this.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(24);

    private static readonly ConcurrentDictionary<string, byte> Warned = new();

    /// <summary>
    /// Resolves <paramref name="key"/> read in units of <paramref name="unit"/> (a minute or a second).
    /// <paramref name="warning"/> is null when the value was absent or valid.
    /// </summary>
    public static TimeSpan Resolve(IConfiguration config, string key, TimeSpan unit, int defaultUnits, out string? warning)
    {
        var fallback = TimeSpan.FromTicks(unit.Ticks * defaultUnits);
        var raw = config[key];
        warning = null;
        if (raw is null) return fallback;

        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
        {
            warning = $"Config '{key}' is '{raw}', which is not a whole number; using the default of {defaultUnits}.";
            return fallback;
        }
        if (units <= 0)
        {
            warning = $"Config '{key}' is {units}, which is not positive; using the default of {defaultUnits}.";
            return fallback;
        }
        var maxUnits = MaxTimeout.Ticks / unit.Ticks;
        if (units > maxUnits)
        {
            warning = $"Config '{key}' is {units}, above the maximum of {maxUnits}; clamping to {maxUnits}.";
            return MaxTimeout;
        }
        return TimeSpan.FromTicks(unit.Ticks * units);
    }

    /// <summary>
    /// <see cref="Resolve"/> that logs the warning (once per key and raw value, since clients are created per request).
    /// </summary>
    public static TimeSpan ResolveAndLog(IConfiguration config, string key, TimeSpan unit, int defaultUnits, ILogger? logger)
    {
        var result = Resolve(config, key, unit, defaultUnits, out var warning);
        if (warning is not null && Warned.TryAdd(key + "=" + config[key], 0))
            logger?.LogWarning("{Warning}", warning);
        return result;
    }
}
