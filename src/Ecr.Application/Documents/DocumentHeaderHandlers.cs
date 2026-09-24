// src/Ecr.Application/Documents/DocumentHeaderHandlers.cs
using System.Globalization;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Documents;

/// <summary>Поточні значення шапки документа — усі поля версії шаблону з їхнім станом.</summary>
public sealed class GetDocumentHeaderHandler(
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає шапку документа.</summary>
    /// <exception cref="NotFoundException">Документа немає або він не видимий.</exception>
    public async Task<DocumentHeaderDto> HandleAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ Той самий шлях, що ValidateDocumentHandler: право перевіряється
        // ТУТ, а не лише в контролері (A7-53) — шапка несе зміст документа.
        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);

        var fields = snapshot.HeaderFields
            .OrderBy(f => f.Ordinal)
            .Select(f => new DocumentHeaderFieldDto(
                f.Id, f.Code, f.LabelL10n, f.DataType, f.IsRequired,
                values.TryGetValue(f.Id, out var value) ? HeaderValueMapping.ToRuleValue(value) : null,
                f.LookupRegistryDefId))
            .ToList();

        return new DocumentHeaderDto(fields);
    }
}

/// <summary>
/// Оновлює значення полів шапки документа.
/// </summary>
/// <remarks>
/// ⚠ Судження про право (немає готового прецеденту рівно для цього рівня
/// грануляції): без окремого функціонального права, лише грант <c>Write</c>
/// на проєкт документа — той самий, грантово-орієнтований підхід (без
/// <c>PermissionCheck.RequireAsync</c>), що вже застосовує
/// <c>PatchCellsHandler</c> для звичайного редагування даних документа
/// (`02-contracts.md` §9: <c>PATCH …/cells</c> — «через
/// <c>IAccessDecisionService</c>», без права). Право
/// <c>Document.ChangeKey</c> навмисно НЕ використовується: воно означає
/// контрольовану, окремо аудитовану операцію зміни бізнес-ключа (ФВ-3.9), а
/// шапка — звичайне редагування даних, ближче до комірок, ніж до рекею.
/// В моделі доступу немає <c>ResourceKind.Document</c>, тож найближчий
/// рівень грануляції — проєктний, дослівно як у
/// <see cref="ChangeDocumentKeyHandler"/> для 404-проти-403.
/// </remarks>
public sealed class PatchDocumentHeaderHandler(
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Оновлює шапку і повертає її повний, щойно збережений стан.</summary>
    /// <exception cref="NotFoundException">Документа немає, він не видимий, або код поля невідомий.</exception>
    /// <exception cref="AccessDeniedException">Анонімний запит, або немає гранта на запис у проєкт документа.</exception>
    /// <exception cref="BusinessRuleException">Значення не відповідає типу чи обов'язковості поля.</exception>
    public async Task<DocumentHeaderDto> HandleAsync(
        long documentId, PatchDocumentHeaderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var document = await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false);
        if (document is null || profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Read)
        {
            throw new NotFoundException(
                ErrorCodes.DocumentNotFound, $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {document.ProjectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = document.ProjectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var fieldsByCode = snapshot.HeaderFields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        var toSave = new Dictionary<int, Domain.ValueObjects.DocumentHeaderValueData>();
        foreach (var change in request.Fields)
        {
            if (!fieldsByCode.TryGetValue(change.Code, out var field))
            {
                throw new NotFoundException(
                    ErrorCodes.HeaderFieldNotFound,
                    $"У версії шаблону {templateVersionId} немає поля шапки з кодом «{change.Code}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0404.headerField",
                        ["templateVersionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                        ["headerFieldCode"] = change.Code,
                    });
            }

            var data = change.IsEmpty
                ? Domain.ValueObjects.DocumentHeaderValueData.Empty
                : HeaderValueReader.Read(change.Value, field) ?? Domain.ValueObjects.DocumentHeaderValueData.Empty;

            if (field.ValidateValue(data) is { } errorCode)
            {
                throw new BusinessRuleException(
                    errorCode,
                    $"Значення поля шапки «{field.Code}» не відповідає типу чи обов'язковості.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0422.validationBlocked",
                        ["headerFieldCode"] = field.Code,
                    });
            }

            toSave[field.Id] = data;
        }

        await headers.SaveValuesAsync(documentId, toSave, ct).ConfigureAwait(false);

        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var result = snapshot.HeaderFields
            .OrderBy(f => f.Ordinal)
            .Select(f => new DocumentHeaderFieldDto(
                f.Id, f.Code, f.LabelL10n, f.DataType, f.IsRequired,
                values.TryGetValue(f.Id, out var value) ? HeaderValueMapping.ToRuleValue(value) : null,
                f.LookupRegistryDefId))
            .ToList();

        return new DocumentHeaderDto(result);
    }
}
