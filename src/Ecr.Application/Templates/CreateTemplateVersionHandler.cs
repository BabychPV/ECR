// src/Ecr.Application/Templates/CreateTemplateVersionHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>Створює чернетку версії шаблону — порожню або клоном (ФВ-2.8).</summary>
public sealed class CreateTemplateVersionHandler(
    IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task<int> HandleAsync(int templateId, string versionNumber, int? cloneFromVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) versionNumber валідувати як Major.Minor.Patch.Build;\n" +
            "2) коди елементів — через EcrCode (D-89): регекс ^[A-Za-z][A-Za-z0-9_]{0,63}$;\n" +
            "3) при cloneFromVersionId — глибокий клон зі ЗБЕРЕЖЕННЯМ RowKey і Code " +
            "   (ФВ-2.8): нові Id, старі ідентичності. Інакше формули клону " +
            "   почнуть посилатися в порожнечу;\n" +
            "4) Status = Draft; PresentationRevision = 0.");
}
