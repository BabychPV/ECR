// src/Ecr.Application/Templates/GetTemplateStructureHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Templates;

/// <summary>
/// Структура опублікованої версії — **з кешу, без звернення до БД**.
/// Ключ `v{id}:r{rev}` (ФВ-2.5) робить інвалідацію непотрібною: інша
/// ревізія — інший ключ.
/// </summary>
public sealed class GetTemplateStructureHandler(IMetadataCache metadata)
{
    public Task<TemplateStructureDto> HandleAsync(int templateVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: metadata.GetStructureAsync за ключем v{id}:r{rev}. " +
            "Промах кешу — прогрів із БД одним запитом на всю версію, не по аркушах. " +
            "Звернення до DbContext звідси заборонене архітектурним тестом.");
}
