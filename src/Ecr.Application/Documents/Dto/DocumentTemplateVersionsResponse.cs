using Ecr.Application.Ports;

namespace Ecr.Application.Documents.Dto;

/// <summary>Усі версії шаблону серед видимих читачеві документів — варіанти фільтра переліку «Version».</summary>
/// <param name="Items">Версії за природним порядком (2.9 перед 2.10) з кількістю документів; не більше 200.</param>
/// <param name="WithoutVersionCount">Скільки видимих документів без версії.</param>
public sealed record DocumentTemplateVersionsResponse(
    IReadOnlyList<DocumentTemplateVersionOption> Items, int WithoutVersionCount);
