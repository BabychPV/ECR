// src/Ecr.Application/Templates/RowDefHandlers.cs
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
/// Записує рядок фіксованої таблиці версії-чернетки: третій вертикальний
/// зріз авторства структури шаблону через API (<c>ФВ-2.1</c>..<c>ФВ-2.5</c>),
/// за зразком <see cref="SaveSheetDefHandler"/> (<c>W5.0</c>) і
/// <see cref="SaveColumnDefHandler"/> (<c>W5.2</c>).
/// </summary>
/// <remarks>
/// ⚠ Таблиця адресується числовим <c>Id</c> — та сама причина, що й у
/// <see cref="SaveColumnDefHandler"/>: рядок і колонка — сиблінги під тією
/// самою таблицею, і адресація мусить бути ОДНАКОВОЮ, інакше клієнтський
/// екран мав би дві різні форми побудови шляху для двох сусідніх кнопок.
///
/// ⛔ <c>RowKind</c> незмінний після створення — тим самим міркуванням, що й
/// <c>DataType</c> у <see cref="SaveColumnDefHandler"/>: домен НЕ дає сеттера
/// (лише конструктор), і зміна ролі рядка (наприклад, <c>Item</c> →
/// <c>Balance</c>) переосмислює вже наявні дані, а не просто змінює вигляд.
///
/// ⛔ Той самий <see cref="IMetadataCache.InvalidateAsync"/>, що й у
/// <see cref="SaveSheetDefHandler"/> — з тієї самої причини.
/// </remarks>
public sealed class SaveRowDefHandler(
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

    /// <summary>Створює або змінює рядок.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить рядок.</param>
    /// <param name="code">Ключ рядка (<see cref="RowKey"/>).</param>
    /// <param name="command">Налаштування рядка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або таблиці немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена, таблиця динамічна (<c>ECR-TMPL-0422</c>,
    /// рядки заводить не шаблон), повторний запис змінює <c>RowKind</c>
    /// наявного рядка, або <c>ParentRowKey</c> не знайдено в цій таблиці.
    /// </exception>
    public async Task<RowDefDto> HandleAsync(
        int templateVersionId, int tableDefId, string code, SaveRowDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var table = SaveColumnDefHandler.FindTable(version, tableDefId);

        var rowKey = RowKey.Create(code);
        var label = new LocalizedText(new Dictionary<string, string>(command.LabelL10n, StringComparer.OrdinalIgnoreCase));

        var existing = table.Rows.FirstOrDefault(
            r => string.Equals(r.RowKeyValue, rowKey.Value, StringComparison.Ordinal));

        if (existing is not null && existing.RowKind != command.RowKind)
        {
            // ⛔ Див. коментар класу: роль рядка міняється лише створенням
            // нового рядка (або клоном версії), не повторним PUT.
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Вид рядка «{code}» незмінний після створення " +
                $"({existing.RowKind} → {command.RowKind}). Заведіть новий рядок або клонуйте версію.");
        }

        var parentRowDefId = ResolveParent(table, code, command.ParentRowKey);

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж — той самий підхід, що й у
        // `SaveSheetDefHandler`/`SaveColumnDefHandler`.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(RowDef))
            : classifier.Classify(nameof(RowDef), nameof(RowDef.IsReadOnly), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        if (existing is null)
        {
            // ⚠ Ordinal, якщо не переданий явно, — за наявними рядками: новий
            // рядок стає ОСТАННІМ у порядку показу (`SaveSheetDefHandler`).
            var ordinal = command.Ordinal
                ?? (table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Ordinal) + 1);

            existing = new RowDef(tableDefId, rowKey, ordinal, label, command.RowKind);
            existing.SetReadOnly(command.IsReadOnly);
            existing.SetParent(parentRowDefId);

            table.AddRow(existing);

            // ⛔ Запис ПЕРЕД аудитом, і лише для створення — так само, як
            // `SaveSheetDefHandler`. Аудит несе `EntityId`; у щойно доданої
            // сутності його ще немає до `SaveChanges`.
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            existing.Rename(label);
            existing.SetReadOnly(command.IsReadOnly);
            existing.SetParent(parentRowDefId);

            if (command.Ordinal is { } ordinal)
            {
                existing.Reorder(ordinal);
            }
        }

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(RowDef), existing.Id,
                change, oldJson is null ? "Create" : "Update",
                oldJson, Describe(existing), ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Див. коментар класу: без цього виклику `GET …/structure`,
        // прочитаний хоч раз до цієї правки, віддавав би знімок без щойно
        // доданого чи зміненого рядка, доки версію не опублікують.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return Map(existing, table);
    }

    /// <summary>Розв'язує ключ батьківського рядка в межах ТІЄЇ САМОЇ таблиці.</summary>
    /// <remarks>
    /// ⚠ Батько адресується КЛЮЧЕМ, а не Id — так само, як
    /// <c>GetTemplateStructureHandler</c> віддає його клієнту (`TemplateRowDto.ParentRowKey`):
    /// ключі переживають клон версії, Id — ні.
    /// </remarks>
    /// <exception cref="BusinessRuleException">
    /// Батька з таким ключем у таблиці немає, або рядок посилається сам на себе.
    /// </exception>
    private static int? ResolveParent(TableDef table, string ownCode, string? parentRowKey)
    {
        if (parentRowKey is null)
        {
            return null;
        }

        if (string.Equals(parentRowKey, ownCode, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid, $"Рядок «{ownCode}» не може бути батьком самому собі.");
        }

        var parent = table.Rows.FirstOrDefault(r => string.Equals(r.RowKeyValue, parentRowKey, StringComparison.Ordinal))
            ?? throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Батьківського рядка «{parentRowKey}» у таблиці {table.Id} немає.");

        return parent.Id;
    }

    /// <summary>Складає DTO рядка для відповіді.</summary>
    internal static RowDefDto Map(RowDef row, TableDef table)
    {
        var parentKey = row.ParentRowDefId is { } parentId
            ? table.Rows.FirstOrDefault(r => r.Id == parentId)?.RowKeyValue
            : null;

        return new RowDefDto(
            row.Id, row.RowKeyValue, row.LabelL10n, row.Ordinal, row.RowKind, parentKey, row.IsReadOnly);
    }

    /// <summary>Стан рядка для аудиту.</summary>
    internal static string Describe(RowDef row)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            RowKey = row.RowKeyValue,
            LabelL10n = row.LabelL10n.Values,
            row.Ordinal,
            row.RowKind,
            row.ParentRowDefId,
            row.IsReadOnly,
        });
}

/// <summary>Налаштування рядка, що приходять із форми.</summary>
/// <param name="LabelL10n">Підпис рядка мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — новий рядок стає останнім за порядком.</param>
/// <param name="RowKind">Роль рядка; незмінна після створення (див. коментар класу).</param>
/// <param name="ParentRowKey">Ключ батьківського рядка в тій самій таблиці; <c>null</c> — корінь.</param>
/// <param name="IsReadOnly">Заборона ручного вводу (наприклад, підсумковий рядок).</param>
public sealed record SaveRowDefCommand(
    IReadOnlyDictionary<string, string> LabelL10n,
    int? Ordinal,
    RowKind RowKind,
    string? ParentRowKey,
    bool IsReadOnly);

/// <summary>Рядок у відповіді на запис/читання через цей обробник.</summary>
/// <remarks>
/// ⛔ Окремий тип від <c>TemplateRowDto</c> (`Dto/TemplateStructureDto.cs`) і
/// від <c>RowDto</c> (рядок зі значеннями в зрізі документа) — той самий
/// принцип, що вже діє для колонок (`Q-012`, <see cref="ColumnDefDto"/>).
/// </remarks>
public sealed record RowDefDto(
    int Id,
    string RowKey,
    LocalizedText LabelL10n,
    int Ordinal,
    RowKind RowKind,
    string? ParentRowKey,
    bool IsReadOnly);

/// <summary>
/// Прибирає рядок із таблиці версії-чернетки — м'яко (<c>ФВ-7.6</c>).
/// </summary>
/// <remarks>
/// ⛔ Рядок не зникає фізично: на нього посилаються комірки документів і
/// власний <c>RowKey</c> у формулах. <see cref="RowDef.SoftDelete"/> лишає
/// запис — той самий підхід, що й <see cref="DeleteSheetDefHandler"/> і
/// <see cref="DeleteColumnDefHandler"/>.
/// </remarks>
public sealed class DeleteRowDefHandler(
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

    /// <summary>Видаляє рядок.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить рядок.</param>
    /// <param name="code">Ключ рядка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії, таблиці або рядка немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    public async Task HandleAsync(int templateVersionId, int tableDefId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var table = SaveColumnDefHandler.FindTable(version, tableDefId);

        var row = table.Rows.FirstOrDefault(r => string.Equals(r.RowKeyValue, code, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Рядка «{code}» у таблиці {tableDefId} немає.");

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(RowDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Видалення");

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(RowDef), row.Id,
                change, "Delete",
                SaveRowDefHandler.Describe(row), NewJson: null, ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        row.SoftDelete(userId, clock.UtcNow);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
