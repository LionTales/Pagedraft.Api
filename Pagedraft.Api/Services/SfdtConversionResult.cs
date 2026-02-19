namespace Pagedraft.Api.Services;

public record SfdtConversionResult
{
    public required string SfdtJson { get; init; }
    public required string PlainText { get; init; }
    public int WordCount { get; init; }
}
