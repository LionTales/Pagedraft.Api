using DocumentFormat.OpenXml;

namespace Pagedraft.Api.Services;

public record RawChapterSegment
{
    public required string Title { get; init; }
    public string? PartName { get; init; }
    public int Order { get; init; }
    public required List<OpenXmlElement> BodyElements { get; init; }
}
