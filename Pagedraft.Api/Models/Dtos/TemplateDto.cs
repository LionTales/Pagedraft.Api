namespace Pagedraft.Api.Models.Dtos;

public record PromptTemplateDto(Guid Id, string Name, string Type, string TemplateText, bool IsBuiltIn, string Language);

public record CreateTemplateRequest(string Name, string Type, string TemplateText, string? Language);

public record UpdateTemplateRequest(string? Name, string? Type, string? TemplateText, string? Language);
