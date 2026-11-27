// src/Ecr.Application/Templates/TemplateCardHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

// Картка шаблону: читання, перейменування, архівування (директива №15, BE-26).
//
// ⛔ Архівування має сенс лише разом із лічильником залежних, тому він їде ТІЄЮ
// САМОЮ відповіддю (`TemplateCard.Dependents`). Кнопка «не пропонувати більше»,
// натиснута без числа «на цьому шаблоні 412 документів у 7 проєктах», — це
// рішення наосліп.
//
// ⚠ Код шаблону НЕ редагується: це бізнес-ключ, за яким шаблон шукають, і форма
// створення прямо обіцяє «змінити потім не можна» (`templates.codeHint`). Опису
// мовами в моделі немає взагалі — у `cfg.Template` лише `NameL10n` і
// `TagsJson`; колонка під опис — це міграція, тобто окремий PR.

/// <summary>Картка шаблону з лічильником залежних. Право <c>Template.View</c>.</summary>
public sealed class GetTemplateCardHandler(
    ITemplateVersionStore templates, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду шаблонів (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Повертає картку шаблону.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — шаблону немає.</exception>
    public async Task<TemplateCard> HandleAsync(int templateId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        return await templates.FindCardAsync(templateId, ct).ConfigureAwait(false)
               ?? throw NotFound(templateId);
    }

    /// <summary>Спільна відмова «шаблону немає» — ключ каталогу вже в сіді.</summary>
    internal static NotFoundException NotFound(int templateId)
        => new(
            ErrorCodes.TemplateNotFound,
            $"Шаблон {templateId} не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.template",
                ["templateId"] = templateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}

/// <summary>Перейменування шаблону. Право <c>Template.Edit</c>.</summary>
public sealed class RenameTemplateHandler(
    ITemplateVersionStore templates,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser)
{
    /// <summary>Право редагування шаблонів (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Змінює назву шаблону й повертає оновлену картку.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — шаблону немає.</exception>
    /// <exception cref="DomainException"><c>ECR-TMPL-0422</c> — назви немає жодною мовою.</exception>
    public async Task<TemplateCard> HandleAsync(
        int templateId, IReadOnlyDictionary<string, string> name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ ВІДСТЕЖУВАНИЙ шаблон: копія без відстеження прийняла б `Rename` і
        // не записала б нічого — правка «пройшла б» і зникла.
        var template = await templates.FindTemplateAsync(templateId, ct).ConfigureAwait(false)
                       ?? throw GetTemplateCardHandler.NotFound(templateId);

        // ⚠ Порівняння мов без урахування регістру — так само, як усюди, де
        // будується `LocalizedText` із мережі (`SheetDefHandlers`): `EN` і `en`
        // не є двома різними мовами.
        template.Rename(new LocalizedText(new Dictionary<string, string>(name, StringComparer.OrdinalIgnoreCase)));

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return await templates.FindCardAsync(templateId, ct).ConfigureAwait(false)
               ?? throw GetTemplateCardHandler.NotFound(templateId);
    }
}

/// <summary>
/// ✎ B151: скільки документів шаблону дозволено в проєкті — один чи скільки завгодно. Право <c>Template.Edit</c>.
/// </summary>
/// <remarks>
/// ⛔ Увімкнення «один на проєкт» при наявних дублікатах ВІДМОВЛЯЄ (<c>409 ECR-TMPL-0409</c>,
/// <c>singleDocumentDuplicates</c>) і називає документи-дублікати — лише ті, які цьому користувачеві видно, плюс
/// загальну кількість. Прапорець пишеться першим (X-замок рядка шаблону), дублікати шукаються після нього в тій
/// самій транзакції: створення, що читає прапорець під спільним замком (<c>CreateDocumentHandler</c>), або вже
/// закомітилось і потрапляє в перелік, або чекає й бачить увімкнений прапорець.
/// </remarks>
public sealed class SetTemplateDocumentPolicyHandler(
    ITemplateVersionStore templates,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право редагування шаблонів (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string EventType = "TemplateDocumentPolicyChanged";

    /// <summary>Задає правило «один документ на проєкт» і повертає оновлену картку.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="single"><c>true</c> — один документ на проєкт; <c>false</c> — скільки завгодно.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — шаблону немає.</exception>
    /// <exception cref="DomainException"><c>ECR-TMPL-0409</c> — у проєктах уже є кілька документів шаблону.</exception>
    public Task<TemplateCard> HandleAsync(int templateId, bool single, CancellationToken ct)
        => HandleAsync(templateId, single, yearEditWindow: null, ct);

    /// <summary>Задає правило «один документ на проєкт» і (b14) вікно року й повертає оновлену картку.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="single"><c>true</c> — один документ на проєкт; <c>false</c> — скільки завгодно.</param>
    /// <param name="yearEditWindow">
    /// ✎ b14 (D-579): «вікно редагування = рік проєкту + зміщення»; <c>null</c> — без змін.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — шаблону немає.</exception>
    /// <exception cref="DomainException"><c>ECR-TMPL-0409</c> — у проєктах уже є кілька документів шаблону.</exception>
    public async Task<TemplateCard> HandleAsync(int templateId, bool single, bool? yearEditWindow, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var before = false;
        var windowBefore = false;
        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                var template = await templates.FindTemplateAsync(templateId, innerCt).ConfigureAwait(false)
                               ?? throw GetTemplateCardHandler.NotFound(templateId);

                before = template.SingleDocumentPerProject;
                windowBefore = template.YearEditWindow;
                template.SetSingleDocumentPerProject(single);
                if (yearEditWindow is { } window)
                {
                    template.SetYearEditWindow(window);
                }
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

                if (single && !before)
                {
                    var duplicates = await templates.FindDuplicateDocumentsAsync(templateId, innerCt).ConfigureAwait(false);
                    if (duplicates.Count > 0)
                    {
                        throw await DuplicatesConflictAsync(profile, templateId, duplicates, innerCt).ConfigureAwait(false);
                    }
                }
            },
            ct).ConfigureAwait(false);

        var card = await templates.FindCardAsync(templateId, ct).ConfigureAwait(false)
                   ?? throw GetTemplateCardHandler.NotFound(templateId);

        if (before != single || windowBefore != card.YearEditWindow)
        {
            // ⚠ Після коміту — як в архівування: журнал пише власним підключенням і не відкочується.
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    EventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        templateId,
                        code = card.Code,
                        singleDocumentPerProject = single,
                        yearEditWindow = card.YearEditWindow,
                        yearEditWindowBefore = windowBefore,
                    }),
                    userId,
                    currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        return card;
    }

    /// <summary>Відмова з переліком видимих дублікатів і загальною кількістю.</summary>
    private async Task<DomainException> DuplicatesConflictAsync(
        AccessProfile profile, int templateId, IReadOnlyList<TemplateDuplicateDocument> duplicates, CancellationToken ct)
    {
        // ⛔ Номери лише видимих документів: перелік чужих розкривав би, що в невидимих проєктах є документи.
        var visible = new List<long>();
        foreach (var duplicate in duplicates)
        {
            if ((await access.CanReadDocumentAsync(profile, duplicate.DocumentId, ct).ConfigureAwait(false)).IsAllowed)
            {
                visible.Add(duplicate.DocumentId);
            }
        }

        return new DomainException(
            "ECR-TMPL-0409",
            $"Шаблон {templateId}: у проєктах уже по кілька документів ({duplicates.Count}); «один документ на проєкт» не вмикається.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0409.singleDocumentDuplicates",
                ["templateId"] = templateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["duplicateCount"] = duplicates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["documentIds"] = visible,
            });
    }
}

/// <summary>
/// Архівування шаблону й повернення його в обіг. Право <c>Template.Edit</c>.
/// </summary>
/// <remarks>
/// ⚠ Право НОВЕ не заводиться: архівування — це редагування картки шаблону, а
/// не окрема влада. Право, видане під одну кнопку, довелося б окремо роздати
/// всім, хто вже веде шаблони, і матриця доступу виросла б без жодної нової
/// межі (<c>BE-28</c> — саме про ціну зайвих прав).
/// </remarks>
public sealed class SetTemplateArchivedHandler(
    ITemplateVersionStore templates,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право редагування шаблонів (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Тип події журналу безпеки: шаблон архівовано.</summary>
    public const string ArchivedEventType = "TemplateArchived";

    /// <summary>Тип події журналу безпеки: шаблон повернено в обіг.</summary>
    public const string RestoredEventType = "TemplateRestored";

    /// <summary>Архівує шаблон або повертає його в обіг.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="archived"><c>true</c> — архівувати, <c>false</c> — повернути в обіг.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Оновлена картка разом із лічильником залежних.</returns>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — шаблону немає.</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — перехід заборонений: шаблон уже в цьому стані.
    /// </exception>
    public async Task<TemplateCard> HandleAsync(int templateId, bool archived, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⚠ `RequireAsync` уже відмовила анонімові — сюди доходить лише
        // названий користувач; кидок лишається, бо автор події журналу не може
        // бути «невідомо хто».
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var template = await templates.FindTemplateAsync(templateId, ct).ConfigureAwait(false)
                       ?? throw GetTemplateCardHandler.NotFound(templateId);

        // ⛔ Перехід ухвалює ДОМЕН, а не цей обробник: повторне архівування —
        // `ECR-TMPL-0409`, і саме суфікс `-0409` робить із нього 409, а не 422
        // (`ExceptionHandlingMiddleware.Map`).
        if (archived)
        {
            template.Archive(clock.UtcNow);
        }
        else
        {
            template.Restore();
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        var card = await templates.FindCardAsync(templateId, ct).ConfigureAwait(false)
                   ?? throw GetTemplateCardHandler.NotFound(templateId);

        // ⛔ Журнал пишеться ПІСЛЯ збереження. `IAuditWriter` комітить власним
        // підключенням одразу (`AuditWriter.CreateCommand`), тож запис перед
        // збереженням лишив би доказ події, якої не сталося, — той самий вибір,
        // що в `ReopenPeriodHandler` і `RunConsistencyCheckHandler`.
        //
        // ⚠ Лічильники йдуть У ЗАПИС. Через рік питання буде не «хто
        // заархівував», а «скільки роботи це зачепило», і відповідь мусить
        // лишитися в журналі: перерахувати її заднім числом уже не вийде.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                archived ? ArchivedEventType : RestoredEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    templateId,
                    code = card.Code,
                    versions = card.Dependents.Versions,
                    projects = card.Dependents.Projects,
                    documents = card.Dependents.Documents,
                }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return card;
    }
}
