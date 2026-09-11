// src/Ecr.Application/Templates/TableRelationHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Зв'язки між таблицями версії — читання для редактора (<c>ФВ-2.12</c>).
/// </summary>
/// <remarks>
/// ⚠ Порожня відповідь тут — НОРМА, а не «ще не налаштували»: механізм
/// опційний, і шаблон без жодного зв'язку працює однаково (<c>ФВ-2.12</c>).
/// Саме тому обробник не підставляє нічого «за замовчуванням».
/// </remarks>
public sealed class ListTableRelationsHandler(
    IRepository<TemplateVersion, int> versions,
    ITemplateVersionStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання зв'язків (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Читає зв'язки версії.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Зв'язки в порядку коду разом зі станом версії.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<TableRelationsDto> HandleAsync(
        int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Версії шаблону {templateVersionId} не існує.");

        var codes = await store.ListTableCodesAsync(templateVersionId, ct).ConfigureAwait(false);
        var relations = await store.ListTableRelationsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⛔ `isEditable` їде в конверті, а не в кожному зв'язку: версія без
        // жодного зв'язку — найчастіший випадок (механізм опційний), і саме на
        // ньому поелементна відповідь мовчала б.
        return new TableRelationsDto(
            !version.IsStructurallyFrozen,
            [.. relations.Select(r => TableRelationMapper.Map(r, codes))]);
    }
}

/// <summary>Складання DTO зв'язку — одне на всі три обробники.</summary>
/// <remarks>
/// ⛔ Спільне навмисно: підстановка кодів таблиць — єдине місце, де DTO
/// розходиться з сутністю, і друга копія цієї підстановки давала б різні
/// підписи в переліку і у відповіді на збереження.
/// </remarks>
public static class TableRelationMapper
{
    /// <summary>Складає DTO зв'язку.</summary>
    /// <param name="relation">Зв'язок.</param>
    /// <param name="tableCodes">Коди таблиць версії: ідентифікатор → код.</param>
    /// <returns>Зв'язок для редактора.</returns>
    public static TableRelationDto Map(
        TableRelationDef relation, IReadOnlyDictionary<int, string> tableCodes)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(tableCodes);

        return new TableRelationDto(
            relation.Id,
            relation.Code,
            relation.RelationKind,
            relation.SourceTableDefId,
            Code(tableCodes, relation.SourceTableDefId),
            relation.TargetTableDefId,
            Code(tableCodes, relation.TargetTableDefId),
            relation.MatchJson,
            relation.MapJson,
            relation.OnSourceChange,
            relation.IsActive);
    }

    /// <summary>Код таблиці за ідентифікатором.</summary>
    /// <param name="tableCodes">Коди таблиць версії.</param>
    /// <param name="tableDefId">Таблиця.</param>
    /// <returns>Код або сам ідентифікатор, якщо таблиці у версії немає.</returns>
    /// <remarks>
    /// ⚠ Ідентифікатор у ролі підпису — не косметика. Таблицю можна прибрати
    /// зі структури, і зв'язок, що на неї посилається, має лишитися ВИДИМИМ:
    /// саме його треба виправити. Порожній рядок сховав би зв'язок, який
    /// тримає зайве посилання.
    /// </remarks>
    private static string Code(IReadOnlyDictionary<int, string> tableCodes, int tableDefId)
        => tableCodes.TryGetValue(tableDefId, out var code)
            ? code
            : tableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Записує зв'язок між таблицями версії-чернетки (<c>ФВ-2.13</c>).
/// </summary>
/// <remarks>
/// ⛔ <b>Правиться лише чернетка.</b> Зв'язок — це структура: від нього
/// залежить, звідки в таблиці беруться числа, а тому зміна зв'язку в
/// опублікованій версії тихо змінила б уже подані форми (<c>ФВ-7.1</c>).
/// Заборону тримає домен (<see cref="TemplateVersion.EnsureStructurallyMutable"/>),
/// а не форма: форма, яка була б єдиною перевіркою, впала б від першого
/// прямого запиту.
///
/// ⚠ <c>PUT</c>, а не <c>POST</c>: адресою зв'язку є його <b>код</b>, і задає
/// його викликач. Створення й зміна — та сама дія рівно тому (<c>D2-147</c>).
/// </remarks>
public sealed class SaveTableRelationHandler(
    IRepository<TemplateVersion, int> versions,
    IRepository<TableRelationDef, int> relations,
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування зв'язків (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює зв'язок.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код зв'язку.</param>
    /// <param name="request">Налаштування зв'язку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Збережений зв'язок.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія заморожена (<c>ECR-TMPL-0409</c>) або таблиця не належить версії
    /// (<c>ECR-TMPL-0422</c>).
    /// </exception>
    public async Task<TableRelationDto> HandleAsync(
        int templateVersionId, string code, SaveTableRelationCommand request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Версії шаблону {templateVersionId} не існує.");

        // ⛔ ПЕРШИМ ділом і доменом. Усе інше нижче — перевірки самого зв'язку;
        // вони мають сенс лише там, де правка взагалі дозволена.
        version.EnsureStructurallyMutable();

        var tableCodes = await store.ListTableCodesAsync(templateVersionId, ct).ConfigureAwait(false);
        EnsureBelongs(tableCodes, request.SourceTableDefId, templateVersionId, "джерела");
        EnsureBelongs(tableCodes, request.TargetTableDefId, templateVersionId, "приймача");

        var existing = await store
            .FindTableRelationAsync(templateVersionId, code, ct)
            .ConfigureAwait(false);

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж, хоч додавання завжди `Safe`:
        // клас пишеться в аудит структурних змін, і «додано» без класу
        // виглядало б у журналі так само, як зміна, яку ніхто не класифікував.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(TableRelationDef))
            : classifier.Classify(nameof(TableRelationDef), nameof(TableRelationDef.RelationKind), hasDocuments);

        RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        // ⛔ Q-244: «запис → аудит → SaveChanges» — одним замиканням
        // `IUnitOfWork.ExecuteInTransactionAsync`, коміт рівно один,
        // наприкінці (той самий клас дефекту, що Q-243).
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            if (existing is null)
            {
                existing = new TableRelationDef(
                    EcrCode.Create(code),
                    request.SourceTableDefId,
                    request.TargetTableDefId,
                    request.RelationKind,
                    request.MatchJson);

                existing.Update(
                    request.SourceTableDefId, request.TargetTableDefId, request.RelationKind,
                    request.MatchJson, request.MapJson, request.OnSourceChange, request.IsActive);

                relations.Add(existing);

                // ⛔ Запис ПЕРЕД аудитом, і лише для створення. Аудит іде окремим
                // `INSERT` і несе `EntityId`; у щойно доданої сутності його ще
                // немає — база призначає його на `SaveChanges`. Без цього рядка
                // журнал структурних змін заповнювався б нулями, тобто ставав би
                // непридатним рівно для того питання, заради якого існує: «що
                // сталося з ЦИМ зв'язком».
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            else
            {
                existing.Update(
                    request.SourceTableDefId, request.TargetTableDefId, request.RelationKind,
                    request.MatchJson, request.MapJson, request.OnSourceChange, request.IsActive);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(TableRelationDef), existing.Id,
                    change, oldJson is null ? "Create" : "Update",
                    oldJson, Describe(existing), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⚠ `existing` завжди присвоєно всередині щойно завершеного замикання
        // — той самий довід, що в `SaveColumnDefHandler`.
        return TableRelationMapper.Map(existing!, tableCodes);
    }

    /// <summary>Перевіряє, що таблиця належить саме цій версії.</summary>
    /// <param name="tableCodes">Коди таблиць версії.</param>
    /// <param name="tableDefId">Таблиця з запиту.</param>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="role">Роль таблиці у зв'язку — для тексту відмови.</param>
    /// <exception cref="BusinessRuleException">Таблиці у версії немає.</exception>
    internal static void EnsureBelongs(
        IReadOnlyDictionary<int, string> tableCodes, int tableDefId, int templateVersionId, string role)
    {
        if (!tableCodes.ContainsKey(tableDefId))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Таблиці {tableDefId} у версії {templateVersionId} немає, тож вона не може бути таблицею {role}. " +
                "Зовнішній ключ таке прийняв би: він не знає про версії — і зв'язок перетнув би межу версії, " +
                "після чого клон переніс би лише його половину.");
        }
    }

    /// <summary>Відхиляє <c>Breaking</c>-зміну у версії з документами (<c>ФВ-7.4</c>).</summary>
    /// <param name="change">Клас зміни.</param>
    /// <param name="code">Код зв'язку.</param>
    /// <param name="hasDocuments">Чи є документи на версії.</param>
    /// <param name="operation">Що робили: «Зміна» або «Видалення».</param>
    /// <exception cref="ConcurrencyConflictException">Зміна руйнівна.</exception>
    internal static void RejectBreaking(ChangeClass change, string code, bool hasDocuments, string operation)
    {
        if (change != ChangeClass.Breaking)
        {
            return;
        }

        // ⚠ Саме `ConcurrencyConflictException`: статус відповіді береться з
        // ТИПУ винятку, а цифри коду кажуть 409 (той самий висновок, що в
        // `PatchPresentationHandler`).
        throw new ConcurrencyConflictException(
            ErrorCodes.BreakingChange,
            $"{operation} зв'язку «{code}» руйнівна: до цієї версії вже прив'язані документи. " +
            "Числа в них рахувалися з урахуванням зв'язку, і прибрати його означало б змінити вже подане " +
            "заднім числом (ФВ-7.4). Це відмова, а не попередження.",
            new Dictionary<string, object?>
            {
                ["relationCode"] = code,
                ["hasDocuments"] = hasDocuments,
            });
    }

    /// <summary>Стан зв'язку для аудиту.</summary>
    /// <param name="relation">Зв'язок.</param>
    /// <returns>JSON із полями, які взагалі можуть змінитися.</returns>
    internal static string Describe(TableRelationDef relation)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            relation.Code,
            RelationKind = relation.RelationKind.ToString(),
            relation.SourceTableDefId,
            relation.TargetTableDefId,
            relation.MatchJson,
            relation.MapJson,
            relation.OnSourceChange,
            relation.IsActive,
        });
}

/// <summary>Налаштування зв'язку, що приходять із форми.</summary>
/// <param name="SourceTableDefId">Таблиця-джерело.</param>
/// <param name="TargetTableDefId">Таблиця-приймач.</param>
/// <param name="RelationKind">Вид зв'язку.</param>
/// <param name="MatchJson">Як зіставляються рядки джерела і приймача.</param>
/// <param name="MapJson">Які колонки на які; <c>null</c> — перенесення немає.</param>
/// <param name="OnSourceChange">Реакція на зміну джерела: 0 Recalc, 1 Warn, 2 Block.</param>
/// <param name="IsActive">Чи діє зв'язок.</param>
public sealed record SaveTableRelationCommand(
    int SourceTableDefId,
    int TargetTableDefId,
    TableRelationKind RelationKind,
    string MatchJson,
    string? MapJson,
    byte OnSourceChange,
    bool IsActive);

/// <summary>
/// Прибирає зв'язок із версії-чернетки (<c>ФВ-2.13</c>).
/// </summary>
/// <remarks>
/// ⚠ Прибрати зв'язок і вимкнути його (<c>isActive = false</c>) — різні дії,
/// і обидві потрібні. Вимкнений зв'язок лишається в структурі й переживає
/// клон; прибраний зникає. Перше — «поки не рахуємо», друге — «цього зв'язку
/// не було».
/// </remarks>
public sealed class DeleteTableRelationHandler(
    IRepository<TemplateVersion, int> versions,
    IRepository<TableRelationDef, int> relations,
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування зв'язків (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє зв'язок.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код зв'язку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або зв'язку немає.</exception>
    /// <exception cref="BusinessRuleException">Версія заморожена.</exception>
    /// <exception cref="ConcurrencyConflictException">На версії є документи.</exception>
    public async Task HandleAsync(int templateVersionId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Версії шаблону {templateVersionId} не існує.");

        version.EnsureStructurallyMutable();

        var relation = await store.FindTableRelationAsync(templateVersionId, code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound,
                $"Зв'язку «{code}» у версії {templateVersionId} немає.");

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(TableRelationDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Видалення");

        // ⛔ Q-244: аудит і видалення/`SaveChanges` тепер одна транзакція.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(TableRelationDef), relation.Id,
                    change, "Delete",
                    SaveTableRelationHandler.Describe(relation), NewJson: null, ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            relations.Remove(relation);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
