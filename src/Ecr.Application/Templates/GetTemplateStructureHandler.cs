// src/Ecr.Application/Templates/GetTemplateStructureHandler.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;

namespace Ecr.Application.Templates;

/// <summary>
/// Структура версії — **з кешу, без звернення до БД** для самого дерева
/// аркушів/таблиць/колонок.
/// </summary>
/// <remarks>
/// Ключ `v{id}:r{rev}` (ФВ-2.5) робить інвалідацію непотрібною для
/// презентаційних правок: інша ревізія — інший ключ. Структурні правки
/// чернетки (<c>PUT …/sheets/{code}</c>) ревізію не піднімають — вони
/// скидають кеш явно (<see cref="IMetadataCache.InvalidateAsync"/>), тим
/// самим способом, що й публікація.
/// </remarks>
public sealed class GetTemplateStructureHandler(
    IMetadataCache metadata,
    IUnitCatalog units,
    IRepository<TemplateVersion, int> versions,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Повертає структуру версії.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<TemplateStructureDto> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.View", ct)
            .ConfigureAwait(false);

        // ⛔ Жодного звернення до DbContext звідси: воно заборонене
        // архітектурним тестом, і не заради чистоти шарів. Структура читається
        // на кожне відкриття таблиці; похід у базу тут з'їв би весь бюджет
        // запиту ще до того, як почнеться читання даних.
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Легкий запит поверх кешованого дерева — той самий клас, що й
        // запит ревізії в `MetadataCache.GetAsync` сам: PK-пошук, не Include.
        // Статус — НЕ частина кешованого знімка (`ФВ-2.5` прив'язує кеш до
        // структури, а не до стану), тому питається окремо і завжди свіжий.
        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Версії шаблону {templateVersionId} не існує.");

        // ⛔ Одиниці розв'язуються і тут: конфігуратор без позначень
        // показував би «тип: Decimal» і жодної підказки, у чому саме
        // вимірюється колонка (`ФВ-16.1`).
        var catalogue = await units.GetAsync(ct).ConfigureAwait(false);
        var symbolById = catalogue.Units.Values.ToDictionary(u => u.Id, u => u.Code);

        var sheets = snapshot.Sheets
            .OrderBy(s => s.Ordinal)
            .Select(sheet => new SheetDto(
                sheet.Id,
                sheet.Code,
                sheet.NameL10n,
                sheet.Ordinal,
                sheet.SheetGroup,
                sheet.IsMandatory,
                sheet.IsVisible,
                [.. sheet.Tables.OrderBy(t => t.Ordinal).Select(table => Table(table, symbolById))]))
            .ToList();

        return new TemplateStructureDto(
            snapshot.TemplateVersionId, snapshot.PresentationRevision, !version.IsStructurallyFrozen, sheets);
    }

    // ⚠ Сигнатура тримає крок за `TableDto` (W5.1: `NameL10n`/`Ordinal`
    // додано, щоб редактор таблиці міг попередньо заповнити форму зі
    // структури, а не лише зі свіжого `PUT`). Це єдиний виробник DTO —
    // конструктор запису вимагає значення для кожного поля, тож зміна форми
    // `TableDto` без цього рядка просто не збереться.
    private static TableDto Table(TableDef table, IReadOnlyDictionary<int, string> symbols)
        => new(
            table.Id,
            table.Code,
            table.NameL10n,
            table.Ordinal,
            table.LayoutKind,
            table.RowMode,
            table.MaxDynamicRows,
            [.. table.Columns.OrderBy(c => c.Ordinal).Select(c => Column(c, symbols))],
            [.. RowsOf(table)]);

    /// <summary>Рядки таблиці з розгорнутою ієрархією за ключами.</summary>
    private static IEnumerable<TemplateRowDto> RowsOf(TableDef table)
    {
        var keysById = table.Rows.ToDictionary(r => r.Id, r => r.RowKeyValue);
        return table.Rows.OrderBy(r => r.Ordinal).Select(r => Row(r, keysById));
    }

    private static TemplateColumnDto Column(ColumnDef column, IReadOnlyDictionary<int, string> symbols)
        => new(
            column.Id,
            column.Code,

            // ⚠ Заголовок віддається ВСІМА мовами, а не однією. Екран
            // структури — це редактор презентаційного шару, і правка підпису
            // англійською не має стирати російський із казахським.
            column.HeaderL10n,
            column.DataType.ToString(),
            column.Ordinal,
            column.IsReadOnly,
            column.IsRequired,
            column.IsHidden,
            column.DisplayFormat,
            column.UnitId is { } unitId && symbols.TryGetValue(unitId, out var symbol) ? symbol : null);

    private static TemplateRowDto Row(RowDef row, Dictionary<int, string> keysById)
        => new(
            row.RowKeyValue,
            row.Ordinal,
            row.RowKind.ToString(),
            row.LabelL10n.Get("en"),

            // ⚠ Батько віддається КЛЮЧЕМ, а не Id: клієнт будує ієрархію за
            // ідентичностями, які переживають клон версії. Id після клону інші.
            row.ParentRowDefId is { } parent && keysById.TryGetValue(parent, out var key) ? key : null,
            row.IsReadOnly);
}
