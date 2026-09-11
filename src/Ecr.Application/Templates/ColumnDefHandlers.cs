// src/Ecr.Application/Templates/ColumnDefHandlers.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Записує колонку таблиці версії-чернетки: другий вертикальний зріз
/// авторства структури шаблону через API (<c>ФВ-2.1</c>..<c>ФВ-2.5</c>), за
/// зразком <see cref="SaveSheetDefHandler"/> (<c>W5.0</c>).
/// </summary>
/// <remarks>
/// ⚠ Таблиця адресується числовим <c>Id</c>, а не парою кодів
/// (<c>sheets/{sheetCode}/tables/{tableCode}/columns/{code}</c>) — та сама
/// форма, що й <c>SaveTableRelationCommand.SourceTableDefId</c>/
/// <c>TargetTableDefId</c> у цьому ж контролері (<see
/// cref="SaveTableRelationHandler"/>). Екран колонки відкривається з рядка
/// таблиці в уже завантаженій структурі (<c>GET …/structure</c>), де
/// <c>TableDto.Id</c> вже є, — повторне кодування шляху через коди аркуша й
/// таблиці означало б розв'язувати вдруге те, що сервер розв'язав один раз.
///
/// ⛔ <c>DataType</c> незмінний після створення: домен НЕ дає сеттера для
/// нього (так само, як <c>TableDef.LayoutKind</c>/<c>RowMode</c> — властивості,
/// що визначають форму сутності, сеттерів не мають узагалі). Те, що
/// <see cref="ChangeClassifier.GuardedFields"/> уже класифікує зміну
/// <c>DataType</c> як <c>Guarded</c>, а не <c>Breaking</c>, — це готовність
/// класифікатора до МАЙБУТНЬОЇ стратегії міграції (зміна тлумачення значення
/// без втрати даних), якої в цьому зрізі ще нема. Без неї мовчки прийняти
/// нову <c>DataType</c> означало б, що вже введені значення читаються під
/// невідповідним типом — тому повторний <c>PUT</c> із іншим типом відхиляється
/// явно, а не ігнорується мовчки.
///
/// ⛔ Той самий <see cref="IMetadataCache.InvalidateAsync"/>, що й у
/// <see cref="SaveSheetDefHandler"/> — з тієї самої причини (див. коментар
/// класу там): без нього прогрітий кеш <c>GET …/structure</c> віддавав би
/// знімок без щойно доданої чи зміненої колонки.
/// </remarks>
public sealed class SaveColumnDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює колонку.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить колонка.</param>
    /// <param name="code">Код колонки.</param>
    /// <param name="command">Налаштування колонки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або таблиці немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена (<c>ECR-TMPL-0409</c>), або повторний
    /// запис змінює <c>DataType</c> наявної колонки (<c>ECR-TMPL-0422</c>).
    /// </exception>
    public async Task<ColumnDefDto> HandleAsync(
        int templateVersionId, int tableDefId, string code, SaveColumnDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var table = FindTable(version, tableDefId);

        var ecrCode = EcrCode.Create(code);
        var header = new LocalizedText(new Dictionary<string, string>(command.HeaderL10n, StringComparer.OrdinalIgnoreCase));

        var existing = table.Columns.FirstOrDefault(
            c => string.Equals(c.Code, ecrCode.Value, StringComparison.Ordinal));

        if (existing is not null && existing.DataType != command.DataType)
        {
            // ⛔ Див. коментар класу: тип даних міняється лише створенням
            // нової колонки (або клоном версії), не повторним PUT.
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Тип колонки «{code}» незмінний після створення " +
                $"({existing.DataType} → {command.DataType}). Заведіть нову колонку або клонуйте версію.");
        }

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж: клас пишеться в аудит
        // структурних змін незалежно від того, чи міг він щось зламати —
        // «додано» без класу виглядало б у журналі так само, як зміна, яку
        // ніхто не класифікував.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(ColumnDef))
            : classifier.Classify(nameof(ColumnDef), nameof(ColumnDef.IsRequired), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        // ⛔ Q-244 (той самий клас дефекту, що Q-243): «запис → аудит →
        // SaveChanges» тепер ОДНИМ замиканням `IUnitOfWork.ExecuteInTransactionAsync`
        // — коміт рівно один, наприкінці. До цієї правки проміжний
        // `SaveChangesAsync` (потрібен лише для отримання `Id` нової колонки)
        // комітився ОКРЕМО від запису аудиту й фінального збереження: збій між
        // ними лишав структурну зміну без відповідного рядка в
        // `aud.StructureChange`.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            if (existing is null)
            {
                // ⚠ Ordinal, якщо не переданий явно, — за наявними колонками:
                // нова колонка стає ОСТАННЬОЮ в порядку показу (`SaveSheetDefHandler`).
                var ordinal = command.Ordinal
                    ?? (table.Columns.Count == 0 ? 0 : table.Columns.Max(c => c.Ordinal) + 1);

                existing = new ColumnDef(tableDefId, ecrCode, header, ordinal, command.DataType);
                ApplyOptionalFields(existing, command);

                table.AddColumn(existing);

                // ⛔ Запис ПЕРЕД аудитом, і лише для створення — так само, як
                // `SaveSheetDefHandler`. Аудит несе `EntityId`; у щойно доданої
                // сутності його ще немає до `SaveChanges`.
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            else
            {
                existing.Rename(header);
                ApplyOptionalFields(existing, command);

                if (command.Ordinal is { } ordinal)
                {
                    existing.Reorder(ordinal);
                }
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(ColumnDef), existing.Id,
                    change, oldJson is null ? "Create" : "Update",
                    oldJson, Describe(existing), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Див. коментар класу: без цього виклику `GET …/structure`,
        // прочитаний хоч раз до цієї правки, віддавав би знімок без щойно
        // доданої чи зміненої колонки, доки версію не опублікують.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ `existing` завжди присвоєно всередині щойно завершеного замикання
        // (гілка створення або гілка зміни): аналіз nullable через межу
        // лямбди цього не бачить, хоч потік виконання гарантує це так само,
        // як гарантував до Q-244, коли той самий код був інлайном.
        return Map(existing!);
    }

    /// <summary>Застосовує поля, спільні для створення й зміни.</summary>
    /// <remarks>
    /// ⚠ <c>SetLookup</c>/<c>SetUnit</c> викликаються лише коли команда несе
    /// значення: обидва кидають <see cref="DomainException"/>, якщо
    /// <c>DataType</c> колонки не відповідає полю (домен, а не цей обробник,
    /// вирішує сумісність) — виклик «на всякий випадок» з <c>null</c> нічого
    /// не додав би, а виклик зі значенням на невідповідному типі має впасти.
    /// </remarks>
    private static void ApplyOptionalFields(ColumnDef column, SaveColumnDefCommand command)
    {
        column.SetRequired(command.IsRequired);
        column.SetReadOnly(command.IsReadOnly);
        column.SetHidden(command.IsHidden);
        column.SetNumericFormat(command.Precision, command.Scale);
        column.SetPresentation(command.DefaultValue, command.DisplayFormat, command.StyleId);

        if (command.LookupRegistryDefId is { } lookupId)
        {
            column.SetLookup(lookupId, command.LookupFilter);
        }

        if (command.UnitId is { } unitId)
        {
            column.SetUnit(unitId);
        }
    }

    /// <summary>Знаходить таблицю версії за числовим ідентифікатором.</summary>
    /// <remarks>
    /// ⚠ Таблиці належать аркушам (`TableDef.SheetDefId`), а не версії
    /// напряму — обхід через усі аркуші версії той самий, що й у
    /// <c>GetTemplateStructureHandler</c>.
    /// </remarks>
    /// <exception cref="NotFoundException">Таблиці з таким Id у версії немає.</exception>
    internal static TableDef FindTable(TemplateVersion version, int tableDefId)
        => version.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == tableDefId)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Таблиці {tableDefId} у версії {version.Id} немає.");

    /// <summary>Складає DTO колонки для відповіді.</summary>
    internal static ColumnDefDto Map(ColumnDef column)
        => new(
            column.Id, column.Code, column.HeaderL10n, column.Ordinal, column.DataType,
            column.IsRequired, column.IsReadOnly, column.IsHidden,
            column.Precision, column.Scale,
            column.DefaultValue, column.DisplayFormat, column.StyleId,
            column.LookupRegistryDefId, column.LookupFilter, column.UnitId);

    /// <summary>Стан колонки для аудиту.</summary>
    internal static string Describe(ColumnDef column)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            column.Code,
            HeaderL10n = column.HeaderL10n.Values,
            column.Ordinal,
            column.DataType,
            column.IsRequired,
            column.IsReadOnly,
            column.IsHidden,
            column.Precision,
            column.Scale,
            column.DefaultValue,
            column.DisplayFormat,
            column.StyleId,
            column.LookupRegistryDefId,
            column.LookupFilter,
            column.UnitId,
        });
}

/// <summary>Налаштування колонки, що приходять із форми.</summary>
/// <param name="HeaderL10n">Заголовок колонки мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — нова колонка стає останньою за порядком.</param>
/// <param name="DataType">Тип даних; незмінний після створення (див. коментар класу).</param>
/// <param name="IsRequired">Обов'язковість заповнення.</param>
/// <param name="IsReadOnly">Заборона ручного вводу.</param>
/// <param name="IsHidden">Видимість колонки; презентаційне поле.</param>
/// <param name="Precision">Точність для <see cref="CellDataType.Decimal"/>.</param>
/// <param name="Scale">Масштаб для <see cref="CellDataType.Decimal"/>.</param>
/// <param name="DefaultValue">Значення за замовчуванням порожньої комірки.</param>
/// <param name="DisplayFormat">Формат відображення; презентаційне поле.</param>
/// <param name="StyleId">Стиль показу; презентаційне поле.</param>
/// <param name="LookupRegistryDefId">Довідник; лише для <see cref="CellDataType.Lookup"/>.</param>
/// <param name="LookupFilter">Звуження списку довідника.</param>
/// <param name="UnitId">Одиниця значень колонки (ФВ-16.1); не для <see cref="CellDataType.Unit"/>.</param>
public sealed record SaveColumnDefCommand(
    IReadOnlyDictionary<string, string> HeaderL10n,
    int? Ordinal,
    CellDataType DataType,
    bool IsRequired,
    bool IsReadOnly,
    bool IsHidden,
    byte? Precision,
    byte? Scale,
    string? DefaultValue,
    string? DisplayFormat,
    int? StyleId,
    int? LookupRegistryDefId,
    string? LookupFilter,
    int? UnitId);

/// <summary>Колонка у відповіді на запис/читання через цей обробник.</summary>
/// <remarks>
/// ⛔ Окремий тип від <c>TemplateColumnDto</c> (`Dto/TemplateStructureDto.cs`)
/// і від <c>ColumnDto</c> (опис колонки в зрізі документа) — той самий
/// принцип, що вже діє в цій кодовій базі для колонок і рядків (`Q-012`):
/// один DTO на одну турботу. <c>TemplateColumnDto</c> навмисно бідніший —
/// він для екрана презентаційної правки опублікованої версії і не носить
/// полів на кшталт <c>Precision</c>/<c>LookupRegistryDefId</c>, яких там
/// міняти не можна. Цей DTO — повний склад того, що приймає й повертає
/// <c>PUT …/tables/{tableId}/columns/{code}</c>.
/// </remarks>
public sealed record ColumnDefDto(
    int Id,
    string Code,
    LocalizedText HeaderL10n,
    int Ordinal,
    CellDataType DataType,
    bool IsRequired,
    bool IsReadOnly,
    bool IsHidden,
    byte? Precision,
    byte? Scale,
    string? DefaultValue,
    string? DisplayFormat,
    int? StyleId,
    int? LookupRegistryDefId,
    string? LookupFilter,
    int? UnitId);

/// <summary>
/// Прибирає колонку з таблиці версії-чернетки — м'яко (<c>ФВ-7.6</c>).
/// </summary>
/// <remarks>
/// ⛔ Колонка не зникає фізично: на неї можуть посилатися формули й правила
/// валідації навіть у чернетці. <see cref="ColumnDef.SoftDelete"/> лишає
/// запис, щоб такі посилання лишалися видимими, а не провалювалися у
/// порожнечу — той самий підхід, що й <see cref="DeleteSheetDefHandler"/>.
/// </remarks>
public sealed class DeleteColumnDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє колонку.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить колонка.</param>
    /// <param name="code">Код колонки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії, таблиці або колонки немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    public async Task HandleAsync(int templateVersionId, int tableDefId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var table = SaveColumnDefHandler.FindTable(version, tableDefId);

        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Колонки «{code}» у таблиці {tableDefId} немає.");

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(ColumnDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Видалення");

        // ⛔ Q-244: аудит писався РАНІШЕ за `SoftDelete`/`SaveChanges` без
        // жодної спільної транзакції — збій між ними лишав журнал із записом
        // про видалення, якого в даних не було.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(ColumnDef), column.Id,
                    change, "Delete",
                    SaveColumnDefHandler.Describe(column), NewJson: null, ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            column.SoftDelete(userId, clock.UtcNow);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
