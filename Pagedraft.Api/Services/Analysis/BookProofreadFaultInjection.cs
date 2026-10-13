using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// TEST-ONLY fault seam for the whole-book proofread (c20 gate): makes the FIRST whole-book attempt of each listed
/// chapter fail, in this process only, so the "retry the failed chapters" path can be shown end to end on a live
/// instance without breaking a model.
///
/// <para>OFF BY DEFAULT AND CANNOT SHIP ENABLED. It arms only when BOTH hold: the host environment is
/// Development, and <see cref="ConfigKey"/> is set. No appsettings file carries the key; the gate sets it on the
/// side process's environment (<c>Testing__BookProofread__FailChapterIdsOnce=&lt;chapterId&gt;[,&lt;chapterId&gt;]</c>).
/// In any other environment the key is ignored (a warning is logged when it is present). Each listed chapter
/// fails ONCE (its first unit's attempt throws before the model is called, so the child job is marked Failed
/// and the run continues); the next attempt of that chapter runs normally, which is what the retry needs.</para>
/// </summary>
public sealed class BookProofreadFaultInjection
{
    /// <summary>Comma-separated chapter ids whose first whole-book attempt fails.</summary>
    public const string ConfigKey = "Testing:BookProofread:FailChapterIdsOnce";

    private readonly ConcurrentDictionary<Guid, byte> _armed;

    private BookProofreadFaultInjection(IEnumerable<Guid> chapterIds)
    {
        _armed = new ConcurrentDictionary<Guid, byte>(chapterIds.Select(id => KeyValuePair.Create(id, (byte)0)));
    }

    /// <summary>The inert instance: nothing ever fails.</summary>
    public static BookProofreadFaultInjection None { get; } = new(Array.Empty<Guid>());

    /// <summary>Chapters still armed (a test/diagnostic read).</summary>
    public int ArmedCount => _armed.Count;

    /// <summary>
    /// Reads <see cref="ConfigKey"/> only in Development; anything else is <see cref="None"/>. Unparseable entries
    /// are skipped.
    /// </summary>
    public static BookProofreadFaultInjection FromConfiguration(IConfiguration config, IHostEnvironment env, ILogger? logger = null)
    {
        var raw = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw)) return None;
        if (!env.IsDevelopment())
        {
            logger?.LogWarning("{Key} is set but ignored: the whole-book proofread fault seam only arms in Development", ConfigKey);
            return None;
        }

        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return None;

        logger?.LogWarning(
            "TEST FAULT SEAM ARMED ({Key}): the first whole-book proofread attempt of {Count} chapter(s) will fail: {Ids}",
            ConfigKey, ids.Count, string.Join(",", ids));
        return new BookProofreadFaultInjection(ids);
    }

    /// <summary>Test factory: arms exactly these chapters.</summary>
    internal static BookProofreadFaultInjection For(params Guid[] chapterIds) => new(chapterIds);

    /// <summary>True (once per armed chapter) when this attempt of <paramref name="chapterId"/> must fail.</summary>
    public bool TryConsume(Guid chapterId) => _armed.TryRemove(chapterId, out _);
}
