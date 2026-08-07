using Microsoft.Extensions.Hosting;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// Loads the shipped product-guides corpus from disk (chatbot phase A, c1).
///
/// <para>LOCATION. The directory is resolved as
/// <c>{IHostEnvironment.ContentRootPath}/Content/guides</c> - the SAME on-disk location
/// <c>Pagedraft.Api.csproj</c> ships (<c>&lt;Content Include="Content\guides\**\*.md"
/// CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" /&gt;</c>), so it
/// resolves identically under <c>dotnet run</c> and under a <c>dotnet publish</c> output. There is
/// deliberately no test-only path and no fallback search: a reader that quietly found the corpus
/// somewhere else would hide exactly the publish-layout regression the guides plan verified against.
/// The directory is constructor-injectable so a test can point it at a fixture, and the production
/// registration passes the content root.</para>
///
/// <para>CACHING. The guides are shipped content and do not change while the process runs, so a
/// SUCCESSFUL load is cached for the lifetime of this singleton. A FAULTED load is deliberately not
/// cached: a deployment that is missing its Content folder should recover the moment it is fixed,
/// and re-reading 15 small files is cheap next to a model call.</para>
///
/// <para>OBSERVABILITY. Every reason a document does not make it into the corpus is logged with the
/// file name and the reason - a missing directory, a file with no frontmatter, a file with no
/// <c>id</c>, and a <c>lang</c> that disagrees with the <c>.he.md</c> filename suffix. A layer that
/// swallowed these to stay non-throwing would ship a silently half-loaded corpus, and the answer
/// would still look confident.</para>
/// </summary>
public class GuidesCorpusReader
{
    /// <summary>Path of the guides directory relative to the app's content root.</summary>
    public const string RelativeDirectory = "Content/guides";

    private readonly string _directory;
    private readonly ILogger<GuidesCorpusReader> _logger;
    private readonly object _gate = new();
    private GuidesCorpus? _cached;

    public GuidesCorpusReader(IHostEnvironment environment, ILogger<GuidesCorpusReader> logger)
        : this(Path.Combine(environment.ContentRootPath, RelativeDirectory.Replace('/', Path.DirectorySeparatorChar)), logger)
    {
    }

    /// <param name="directory">Absolute path of the guides directory. Test seam.</param>
    public GuidesCorpusReader(string directory, ILogger<GuidesCorpusReader> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>The resolved on-disk directory, so a fault can name it.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Loads (or returns the cached) corpus. Never throws: an unreadable corpus is a
    /// <see cref="GuidesCorpus.Fault"/>, because the caller has to be able to tell "no guides" from
    /// "no relevant guides" and answer honestly instead of from the model's priors.
    /// </summary>
    public GuidesCorpus Load()
    {
        var cached = _cached;
        if (cached != null) return cached;

        lock (_gate)
        {
            if (_cached != null) return _cached;

            var corpus = ReadFromDisk();
            if (corpus.CanGround) _cached = corpus;
            return corpus;
        }
    }

    private GuidesCorpus ReadFromDisk()
    {
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                _logger.LogWarning(
                    "Product guides corpus is unavailable: the directory {GuidesDirectory} does not exist. " +
                    "The chat endpoint will refuse to answer rather than answer ungrounded. Check that " +
                    "Content/guides shipped with the app (Pagedraft.Api.csproj Content include, " +
                    "CopyToOutputDirectory/CopyToPublishDirectory).",
                    _directory);
                return new GuidesCorpus(Array.Empty<GuideDocument>(), ProductChatFaults.GuidesUnavailable, _directory, 0);
            }

            files = System.IO.Directory.GetFiles(_directory, "*.md", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex,
                "Product guides corpus is unavailable: enumerating {GuidesDirectory} failed. The chat endpoint " +
                "will refuse to answer rather than answer ungrounded.",
                _directory);
            return new GuidesCorpus(Array.Empty<GuideDocument>(), ProductChatFaults.GuidesUnavailable, _directory, 0);
        }

        // Ordinal sort so the corpus order is identical on every platform and filesystem; the selector
        // relies on a stable input order for its final tie-break.
        Array.Sort(files, StringComparer.Ordinal);

        var documents = new List<GuideDocument>(files.Length);
        var unparseable = 0;

        foreach (var path in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unparseable++;
                _logger.LogWarning(ex, "Product guide {GuideFile} could not be read and is not in the corpus.",
                    Path.GetFileName(path));
                continue;
            }

            var fileName = Path.GetFileName(path);
            var (document, reason) = GuideFrontmatter.Parse(fileName, text);
            if (document == null)
            {
                unparseable++;
                _logger.LogWarning(
                    "Product guide {GuideFile} has unusable frontmatter ({Reason}) and is not in the corpus. " +
                    "Every guide must open with a --- block carrying at least id and lang.",
                    fileName, reason);
                continue;
            }

            var suffixLang = GuideFrontmatter.LanguageFromFileName(fileName);
            if (!string.Equals(suffixLang, document.Lang, StringComparison.OrdinalIgnoreCase))
            {
                // NOT a rejection: the frontmatter is authoritative (d1 item 1). It is still a content
                // bug someone has to fix, so it is loud rather than silent.
                _logger.LogWarning(
                    "Product guide {GuideFile} declares lang '{FrontmatterLang}' but its filename implies " +
                    "'{FileNameLang}'. The frontmatter wins (it is the authoritative signal); rename the file or " +
                    "fix the frontmatter so the two agree.",
                    fileName, document.Lang, suffixLang);
            }

            documents.Add(document);
        }

        if (documents.Count == 0)
        {
            _logger.LogWarning(
                "Product guides corpus is EMPTY: {GuidesDirectory} holds {FileCount} markdown file(s) and none " +
                "parsed ({UnparseableCount} rejected). The chat endpoint will refuse to answer rather than " +
                "answer ungrounded.",
                _directory, files.Length, unparseable);
            return new GuidesCorpus(Array.Empty<GuideDocument>(), ProductChatFaults.GuidesEmpty, _directory, unparseable);
        }

        if (unparseable > 0)
        {
            _logger.LogWarning(
                "Product guides corpus loaded PARTIALLY from {GuidesDirectory}: {LoadedCount} guide(s) usable, " +
                "{UnparseableCount} rejected (each logged above). Answers will be grounded in a corpus that is " +
                "missing content.",
                _directory, documents.Count, unparseable);
        }
        else
        {
            _logger.LogInformation(
                "Product guides corpus loaded from {GuidesDirectory}: {LoadedCount} guide(s), languages [{Languages}].",
                _directory, documents.Count,
                string.Join(", ", documents.Select(d => d.Lang).Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal)));
        }

        return new GuidesCorpus(documents, Fault: null, _directory, unparseable);
    }
}
