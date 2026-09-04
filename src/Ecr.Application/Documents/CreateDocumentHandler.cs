// src/Ecr.Application/Documents/CreateDocumentHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Documents;

/// <summary>Створює документ і його склад аркушів (ФВ-3.1, ФВ-3.2).</summary>
/// <remarks>
/// Документ **наскрізний по періодах**: період живе на рядках і комірках, а не
/// тут. Унікальність — `(ProjectId, BusinessKey)`.
/// </remarks>
public sealed class CreateDocumentHandler(
    IMetadataCache metadata, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task<long> HandleAsync(int projectId, int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) перевірити склад за SheetGroupRule: RequiresAll / RequiresOne / " +
            "   Optional; порушення → ECR-DOC-0422;\n" +
            "2) BusinessKey скласти зі значень колонок IsBusinessKey; дублікат у " +
            "   межах проєкту → ECR-DOC-0409;\n" +
            "3) ⛔ статусу документа НЕ ставити — його не існує (D-93). Робочий стан " +
            "   з'явиться в wf.ApprovalState при першому Submit;\n" +
            "4) TableInstance створювати ліниво, при першому записі в період, " +
            "   а не одразу на всі 12: більшість документів заповнюють не всі.");
}
