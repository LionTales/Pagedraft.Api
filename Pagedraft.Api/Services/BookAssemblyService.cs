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
