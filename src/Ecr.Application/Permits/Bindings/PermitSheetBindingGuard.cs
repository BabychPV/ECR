// src/Ecr.Application/Permits/Bindings/PermitSheetBindingGuard.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Охоронець правки структури аркуша, яку тримають прив'язки до дозволів (Permits, PM-09b): таблиця чи колонка, на яку
/// посилається прив'язка, не стає <c>Fixed</c>, не видаляється й не лишає прив'язку без цілі - відмова
/// <c>409 ECR-PRM-4102</c> з назвою прив'язки ДО запису (а не зворотна 422 при наступному збереженні прив'язки).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Видалення таблиці й колонки шаблону - м'яке (<c>SoftDelete</c>), тож зовнішній ключ прив'язки його не зупинив би:
/// прив'язка лишилась би з мертвою ціллю, а рантайм PM-10 писав би рядки в таблицю, якої немає у структурі.
/// </para>
/// <para>
/// ⚠ Читання - один запит на перевірку (<see cref="IPermitBindingStore.ListAsync"/> по версії - прив'язок на версію
/// одиниці; <see cref="IPermitBindingStore.FindHoldAsync"/> по таблицях - для видалення таблиці й аркуша), без запитів
/// на шляху документа: рантайм (<c>EnsureTable</c>, <c>PatchCells</c>) цього класу не торкається.
/// </para>
/// <para>
/// ⛔ Охоронець кличеться ПІД блоком рядка версії (<c>DraftVersionLock</c>), в одній транзакції з записом: збереження
/// прив'язки бере той самий блок, тож між перевіркою й комітом прив'язка не з'явиться (PM-09b2, знахідка Аналізу ч.1).
/// Прапор <c>HasActivePermitBindings</c> знімка метаданих тут НЕ годиться навіть як швидкий вихід: він рахує лише ЧИННІ
/// прив'язки (вимкнена теж тримає таблицю/аркуш/колонку) і оновлюється ПІСЛЯ коміту - у вікні між комітом прив'язки й
/// скиданням знімка він бреше «немає».
/// </para>
/// </remarks>
internal static class PermitSheetBindingGuard
{
    /// <summary>
    /// Перехід таблиці в <see cref="TableRowMode.Fixed"/> з чинною прив'язкою «рядки»: рядки дозволу в таблицю з
    /// фіксованими рядками не додати. Прив'язка в режимі формул режим рядків не читає й не блокує.
    /// </summary>
    /// <param name="bindings">Прив'язки.</param>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="table">Таблиця, що змінюється.</param>
    /// <param name="newMode">Режим рядків після правки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-PRM-4102</c> <c>rowMode</c>.</exception>
    internal static async Task RequireRowModeAllowedAsync(
        IPermitBindingStore bindings, int templateVersionId, TableDef table, TableRowMode newMode, CancellationToken ct)
    {
        // Лише перехід: таблиця, що вже Fixed, правиться (назва, порядок) без перевірки - вона не нова поломка.
        if (newMode != TableRowMode.Fixed || table.RowMode == TableRowMode.Fixed)
        {
            return;
        }

        var held = (await bindings.ListAsync(templateVersionId, ct).ConfigureAwait(false))
            .FirstOrDefault(v => v.Binding.SheetTableDefId == table.Id && v.Binding.IsActive && v.Binding.Mode == PermitBindingMode.Rows);

        if (held is not null)
        {
            throw PermitBindingErrors.SheetRowModeHeld(table.Code, held.Binding.Code);
        }
    }

    /// <summary>
    /// Стеля рядків таблиці з чинною прив'язкою «рядки» не знижується (PM-09b2). Кількість рядків живих дозволів у
    /// момент правки шаблону невідома, тож правило просте: прив'язка є - стеля не менша за попередню; «без стелі»
    /// (<c>null</c>) вважається найбільшою, тож з нього можна лише зняти стелю, не поставити.
    /// </summary>
    /// <param name="bindings">Прив'язки.</param>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="table">Таблиця, що змінюється (ще зі СТАРОЮ стелею).</param>
    /// <param name="newMax">Стеля після правки; <c>null</c> - без стелі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-PRM-4102</c> <c>maxRows</c>.</exception>
    internal static async Task RequireMaxRowsNotLoweredAsync(
        IPermitBindingStore bindings, int templateVersionId, TableDef table, int? newMax, CancellationToken ct)
    {
        // Без запиту, якщо це не зниження: нова стеля не менша за стару, або стелю знімають.
        var previous = table.MaxDynamicRows;
        if (newMax is not { } requested || (previous is { } old && requested >= old))
        {
            return;
        }

        var held = (await bindings.ListAsync(templateVersionId, ct).ConfigureAwait(false))
            .FirstOrDefault(v => v.Binding.SheetTableDefId == table.Id && v.Binding.IsActive && v.Binding.Mode == PermitBindingMode.Rows);

        if (held is not null)
        {
            throw PermitBindingErrors.SheetMaxRowsHeld(table.Code, held.Binding.Code, previous, requested);
        }
    }

    /// <summary>Аркуш, на якому є таблиця з будь-якою прив'язкою, не видаляється (PM-09b2).</summary>
    /// <param name="bindings">Прив'язки.</param>
    /// <param name="sheet">Аркуш, що видаляється.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-PRM-4102</c> <c>sheet</c>.</exception>
    internal static async Task RequireSheetFreeAsync(IPermitBindingStore bindings, SheetDef sheet, CancellationToken ct)
    {
        // Один запит по ідентифікаторах таблиць аркуша (а не перелік усіх прив'язок версії з колонками й довідниками):
        // аркуш без таблиць не питає нічого, версія без прив'язок (Land) - один індексний запит.
        var tablesById = sheet.Tables.ToDictionary(t => t.Id);
        var held = await bindings.FindHoldAsync([.. tablesById.Keys], ct).ConfigureAwait(false);

        if (held is not null)
        {
            throw PermitBindingErrors.SheetHeld(sheet.Code, tablesById[held.SheetTableDefId].Code, held.Code);
        }
    }

    /// <summary>Таблиця з будь-якою прив'язкою (чинною чи вимкненою, у будь-якому режимі) не видаляється.</summary>
    /// <param name="bindings">Прив'язки.</param>
    /// <param name="table">Таблиця, що видаляється.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-PRM-4102</c> <c>table</c>.</exception>
    internal static async Task RequireTableFreeAsync(IPermitBindingStore bindings, TableDef table, CancellationToken ct)
    {
        var held = await bindings.FindHoldAsync([table.Id], ct).ConfigureAwait(false);

        if (held is not null)
        {
            throw PermitBindingErrors.SheetTableHeld(table.Code, held.Code);
        }
    }

    /// <summary>Колонка, яку зіставляє прив'язка, не видаляється.</summary>
    /// <param name="bindings">Прив'язки.</param>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="column">Колонка, що видаляється.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-PRM-4102</c> <c>column</c>.</exception>
    internal static async Task RequireColumnFreeAsync(
        IPermitBindingStore bindings, int templateVersionId, ColumnDef column, CancellationToken ct)
    {
        var held = (await bindings.ListAsync(templateVersionId, ct).ConfigureAwait(false))
            .FirstOrDefault(v => v.Columns.Any(c => c.SheetColumnDefId == column.Id));

        if (held is not null)
        {
            throw PermitBindingErrors.SheetColumnHeld(column.Code, held.Binding.Code);
        }
    }
}
