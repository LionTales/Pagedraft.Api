using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;

namespace Pagedraft.Api.Services;

public class BookAssemblyService
{
    /// <summary>
    /// Assembles multiple DOCX chapter buffers into a single DOCX document.
    /// </summary>
    public byte[] AssembleDocx(IReadOnlyList<byte[]> chapterDocxBuffers)
    {
        if (chapterDocxBuffers == null || chapterDocxBuffers.Count == 0)
        {
            using var empty = new WordDocument();
            // A bare WordDocument has NO section, and serializing one throws "There are no sections present
            // in the document" - so this branch, which reads as the safe empty-document fallback, in fact
            // threw every time it was reached (an export of a book with no chapters answered 500). EnsureMinimal
            // adds the section and paragraph a valid DOCX needs.
            //
            // NO PRODUCTION CALLER REACHES THIS ANY MORE. BookExportService answers NothingWritten before it
            // would call in with an empty list, because a valid empty document is the one answer an export
            // must never give an author. Kept, correct and tested, so this stays a safe fallback rather than a
            // throw for any future caller - but a caller that lands here on an EXPORT path is a defect.
            empty.EnsureMinimal();
            using var stream = new MemoryStream();
            empty.Save(stream, FormatType.Docx);
            return stream.ToArray();
        }
        if (chapterDocxBuffers.Count == 1)
            return chapterDocxBuffers[0];

        using var firstDoc = new WordDocument(new MemoryStream(chapterDocxBuffers[0]), FormatType.Docx);
        for (var i = 1; i < chapterDocxBuffers.Count; i++)
        {
            using var nextStream = new MemoryStream(chapterDocxBuffers[i]);
            using var nextDoc = new WordDocument(nextStream, FormatType.Docx);
            firstDoc.ImportContent(nextDoc, ImportOptions.UseDestinationStyles);
        }
        using var outStream = new MemoryStream();
        firstDoc.Save(outStream, FormatType.Docx);
        return outStream.ToArray();
    }
}
