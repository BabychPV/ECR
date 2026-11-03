using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Результат додавання копії аркуша.</summary>
/// <param name="SheetCopyId">Id копії (<c>doc.DocumentSheet.Id</c>) — він же <c>sheetCopyId</c> у даних копії.</param>
/// <param name="Ordinal">Номер у групі (2…): назва копії — «&lt;Назва&gt; N».</param>
public sealed record SheetCopyCreated(long SheetCopyId, short Ordinal);

/// <summary>
/// Копії аркушів документа (RS, рішення людини 03.11): додати, перейменувати групу, видалити копію.
/// </summary>
/// <remarks>
/// ⛔ Правила (людина 03.11): копіювати можна аркуш, позначений у шаблоні (<c>SheetDef.IsCopyable</c>); джерело копії —
/// завжди оригінал; копія ПОРОЖНЯ, зі структурою й формулами оригіналу, існує в усіх періодах і має власний стан у кожному;
/// перейменування групи міняє «&lt;Назва&gt; N» в усіх; видалити можна будь-яку копію, крім оригіналу, решта
/// перенумеровується. Копія в стані <c>Submitted</c>/<c>Approved</c> хоч в одному періоді не видаляється (409): її зрізи
/// подання не зникають безслідно.
/// <para>
/// ⛔ Право — те, що керує складом документа (<c>Document.Create</c>, склад аркушів задають при створенні), + грант запису
/// в проєкт документа; нового права не заведено. Усі три операції — під ВИНЯТКОВИМ блокуванням структури документа
/// (як перенос версії й видалення): паралельні правки, подання й імпорт чекають, а дві операції над групою не змішуються.
/// Журнал безпеки пишеться в ТІЙ САМІЙ транзакції.
/// </para>
/// </remarks>
public sealed class DocumentSheetCopyHandlers(
    IDocumentStore documents,
    IDocumentSheetCopyStore copies,
    IMetadataCache metadata,
    ISheetEditGate structureGate,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну складу документа.</summary>
    public const string Permission = "Document.Create";

    /// <summary>Тип події журналу безпеки: додано копію.</summary>
    public const string AddedEventType = "DocumentSheetCopyAdded";

    /// <summary>Тип події журналу безпеки: перейменовано групу.</summary>
    public const string RenamedEventType = "DocumentSheetGroupRenamed";

    /// <summary>Тип події журналу безпеки: видалено копію.</summary>
    public const string DeletedEventType = "DocumentSheetCopyDeleted";

    /// <summary>Найдовша назва групи (як назва аркуша шаблону).</summary>
    public const int MaxNameLength = 200;

    /// <summary>Додає порожню копію аркуша.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш (оригінал).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — документа чи аркуша немає або не видно.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-DOC-0422</c> — аркуш не позначений копіюваним.</exception>
    public async Task<SheetCopyCreated> AddAsync(long documentId, int sheetDefId, CancellationToken ct)
    {
        var (profile, userId, projectId) = await AuthorizeAsync(documentId, ct).ConfigureAwait(false);
        var created = new SheetCopyCreated(0, 0);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await EnterStructureAsync(documentId, projectId, innerCt).ConfigureAwait(false);
            await RequireVisibleSheetAsync(profile, documentId, sheetDefId, innerCt).ConfigureAwait(false);

            var group = await copies.FindGroupAsync(documentId, sheetDefId, innerCt).ConfigureAwait(false)
                        ?? throw SheetNotFound(documentId, sheetDefId);

            if (!await IsCopyableAsync(documentId, sheetDefId, innerCt).ConfigureAwait(false))
            {
                throw NotCopyable(sheetDefId);
            }

            var (copyId, ordinal) = await copies.AddCopyAsync(documentId, sheetDefId, innerCt).ConfigureAwait(false);
            created = new SheetCopyCreated(copyId, ordinal);

            await WriteAuditAsync(
                AddedEventType, userId,
                new { documentId, projectId, sheetDefId, originalId = group.OriginalId, sheetCopyId = copyId, ordinal },
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return created;
    }

    /// <summary>Перейменовує групу аркуша (назва на оригіналі; копії стають «&lt;Назва&gt; N»).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш (оригінал).</param>
    /// <param name="name">Назва за мовами (як <c>SaveSheetDefRequest.NameL10n</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-DOC-0422</c> — назва порожня/задовга, або аркуш не копіюваний і копій немає.</exception>
    public async Task RenameGroupAsync(
        long documentId, int sheetDefId, IReadOnlyDictionary<string, string>? name, CancellationToken ct)
    {
        var normalized = NormalizeName(name);
        var (profile, userId, projectId) = await AuthorizeAsync(documentId, ct).ConfigureAwait(false);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await EnterStructureAsync(documentId, projectId, innerCt).ConfigureAwait(false);
            await RequireVisibleSheetAsync(profile, documentId, sheetDefId, innerCt).ConfigureAwait(false);

            var group = await copies.FindGroupAsync(documentId, sheetDefId, innerCt).ConfigureAwait(false)
                        ?? throw SheetNotFound(documentId, sheetDefId);

            // Шаблон міг зняти позначку, коли копії вже є (Q4): група з копіями перейменовується й далі.
            if (group.CopyCount == 0 && !await IsCopyableAsync(documentId, sheetDefId, innerCt).ConfigureAwait(false))
            {
                throw NotCopyable(sheetDefId);
            }

            await copies.RenameGroupAsync(documentId, sheetDefId, normalized, innerCt).ConfigureAwait(false);

            await WriteAuditAsync(
                RenamedEventType, userId,
                new
                {
                    documentId, projectId, sheetDefId,
                    oldName = group.NameOverride?.Values, newName = normalized.Values,
                },
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Видаляє копію аркуша з даними всіх періодів; решту копій групи перенумеровує.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetCopyId">Копія (не оригінал).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — копії немає в документі.</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-DOC-0409</c>: <c>sheetCopyIsOriginal</c> — це оригінал; <c>sheetCopyHasWorkflow</c> — копія подана чи
    /// затверджена хоч в одному періоді (<c>periodKey</c>, <c>status</c>).
    /// </exception>
    public async Task DeleteCopyAsync(long documentId, long sheetCopyId, CancellationToken ct)
    {
        var (profile, userId, projectId) = await AuthorizeAsync(documentId, ct).ConfigureAwait(false);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await EnterStructureAsync(documentId, projectId, innerCt).ConfigureAwait(false);

            var copy = await copies.FindAsync(documentId, sheetCopyId, innerCt).ConfigureAwait(false)
                       ?? throw CopyNotFound(documentId, sheetCopyId);
            await RequireVisibleSheetAsync(profile, documentId, copy.SheetDefId, innerCt).ConfigureAwait(false);

            if (!copy.IsCopy)
            {
                throw new DomainException(
                    ErrorCodes.DocumentSubmitted,
                    $"Аркуш {copy.SheetDefId} — оригінал: видаляється лише копія.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-DOC-0409.sheetCopyIsOriginal",
                        ["sheetDefId"] = copy.SheetDefId.ToString(CultureInfo.InvariantCulture),
                    });
            }

            var blocking = await copies.FindBlockingStateAsync(documentId, sheetCopyId, innerCt).ConfigureAwait(false);
            if (blocking is not null)
            {
                throw new DomainException(
                    ErrorCodes.DocumentSubmitted,
                    $"Копія аркуша {copy.SheetDefId} №{copy.Ordinal} не видаляється: у періоді {blocking.PeriodKey} "
                    + $"вона в стані {blocking.Status}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-DOC-0409.sheetCopyHasWorkflow",
                        ["periodKey"] = blocking.PeriodKey.ToString(CultureInfo.InvariantCulture),
                        ["status"] = blocking.Status.ToString(),
                    });
            }

            var cells = await copies.DeleteCopyAsync(documentId, sheetCopyId, innerCt).ConfigureAwait(false);

            await WriteAuditAsync(
                DeletedEventType, userId,
                new
                {
                    documentId, projectId, sheetDefId = copy.SheetDefId, originalId = copy.OriginalId,
                    sheetCopyId, ordinal = copy.Ordinal, cells,
                },
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<(AccessProfile Profile, int UserId, int ProjectId)> AuthorizeAsync(
        long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може змінювати склад документа.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        // Чужий документ = неіснуючий: 403 сам по собі видав би, що такий документ є.
        var document = await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false);
        if (document is null || profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Read)
        {
            throw DocumentNotFound(documentId);
        }

        // ⛔ ФВ-6.14: право — у проєкті ЦЬОГО документа, і грант запису в нього.
        PermissionCheck.RequireIn(profile, Permission, document.ProjectId);
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

        return (profile, userId, document.ProjectId);
    }

    /// <summary>Структура — ВИНЯТКОВО й першою дією (як видалення документа і перенос версії); архівований проєкт — 409.</summary>
    private async Task EnterStructureAsync(long documentId, int projectId, CancellationToken ct)
    {
        if (await structureGate.EnterStructureAsync(documentId, exclusive: true, ct).ConfigureAwait(false) is null)
        {
            throw DocumentNotFound(documentId);
        }

        if (await documents.FindProjectStatusAsync(projectId, ct).ConfigureAwait(false) == ProjectStatus.Archived)
        {
            throw new DomainException(
                ErrorCodes.DocumentSubmitted,
                $"Проєкт {projectId} заархівований: склад документа {documentId} не змінюється.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0409.sheetCopyProjectArchived",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Аркуш, схований від читача, для нього не існує (як і в решті обробників документа).</summary>
    private async Task RequireVisibleSheetAsync(AccessProfile profile, long documentId, int sheetDefId, CancellationToken ct)
    {
        var readable = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!readable.CanReadSheet(sheetDefId))
        {
            throw SheetNotFound(documentId, sheetDefId);
        }
    }

    private async Task<bool> IsCopyableAsync(long documentId, int sheetDefId, CancellationToken ct)
    {
        var versionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(versionId, ct).ConfigureAwait(false);

        return snapshot.Sheets.Any(s => s.Id == sheetDefId && !s.IsDeleted && s.IsCopyable);
    }

    private static LocalizedText NormalizeName(IReadOnlyDictionary<string, string>? name)
    {
        var values = (name ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (values.Count == 0 || values.Values.Any(v => v.Length > MaxNameLength))
        {
            throw new BusinessRuleException(
                ErrorCodes.DocumentCompositionInvalid,
                $"Назва групи аркуша — від 1 до {MaxNameLength} символів хоча б однією мовою.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0422.sheetNameInvalid",
                    ["maxLength"] = MaxNameLength,
                });
        }

        return new LocalizedText(values);
    }

    private Task WriteAuditAsync(string eventType, int userId, object details, CancellationToken ct)
        => audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, eventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(details), userId, currentUser.CorrelationId),
            ct);

    private static NotFoundException DocumentNotFound(long documentId)
        => new(
            "ECR-DOC-0404", $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });

    private static NotFoundException SheetNotFound(long documentId, int sheetDefId)
        => new(
            "ECR-DOC-0404", $"Аркуш {sheetDefId} не входить у склад документа {documentId}.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.sheetCopy",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                ["sheetDefId"] = sheetDefId.ToString(CultureInfo.InvariantCulture),
            });

    private static NotFoundException CopyNotFound(long documentId, long sheetCopyId)
        => new(
            "ECR-DOC-0404", $"Копії аркуша {sheetCopyId} у документі {documentId} немає.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.sheetCopy",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                ["sheetCopyId"] = sheetCopyId.ToString(CultureInfo.InvariantCulture),
            });

    private static BusinessRuleException NotCopyable(int sheetDefId)
        => new(
            ErrorCodes.DocumentCompositionInvalid,
            $"Аркуш {sheetDefId} не позначений у шаблоні копіюваним.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0422.sheetNotCopyable",
                ["sheetDefId"] = sheetDefId.ToString(CultureInfo.InvariantCulture),
            });
}
