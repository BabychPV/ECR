using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Documents;

/// <summary>
/// Рішення «чи віддавати читачеві вираз формули колонки» (UI-25, B4): вираз називає інші
/// колонки, таблиці й аркуші, тож віддається лише тоді, коли читач бачить КОЖНЕ з них.
/// </summary>
/// <remarks>
/// ⛔ Закрито за замовчуванням: вираз, що не розібрався, посилання, що не розв'язалося, залежність
/// невідомого виду, відсутній рушій — усе це <c>null</c>. Помилка розбору тут була б витоком, а не
/// «порожнім полем».
/// <para>
/// ⚠ Предикат динамічної таблиці (<c>[WHERE …]</c>) названих колонок у залежностях не несе — лише
/// текст умови. Тому залежність із предикатом вимагає видимості ВСІХ колонок своєї таблиці:
/// умова читає колонки саме її, а без цього прихована колонка мовчки ховалась би в умові.
/// </para>
/// Заголовки документа й довідники — не ресурси аркушів: їхню видимість це правило не змінює.
/// </remarks>
public static class ColumnExpressionVisibility
{
    /// <summary>Вираз формули колонки або <c>null</c>, коли хоч одне посилання читачеві не видне.</summary>
    /// <param name="formula">Формула колонки; <c>null</c> — формули немає.</param>
    /// <param name="table">Таблиця колонки.</param>
    /// <param name="columnDefId">Колонка, якій належить формула.</param>
    /// <param name="snapshot">Знімок структури версії.</param>
    /// <param name="readable">Межі читання читача.</param>
    /// <param name="engine">Рушій виразів; <c>null</c> — вираз не віддається.</param>
    public static string? Visible(
        FormulaDef? formula,
        TableDef table,
        int columnDefId,
        TemplateVersionSnapshot snapshot,
        DocumentReadScope readable,
        IFormulaEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(readable);
        return Visible(formula, table, columnDefId, snapshot, readable.CanReadTable, readable.CanReadColumn, engine);
    }

    /// <summary>То саме за межами читання, заданими предикатами (експорт знає лише списки прихованого).</summary>
    /// <param name="formula">Формула колонки; <c>null</c> — формули немає.</param>
    /// <param name="table">Таблиця колонки.</param>
    /// <param name="columnDefId">Колонка, якій належить формула.</param>
    /// <param name="snapshot">Знімок структури версії.</param>
    /// <param name="canReadTable">Чи бачить читач таблицю.</param>
    /// <param name="canReadColumn">Чи бачить читач колонку.</param>
    /// <param name="engine">Рушій виразів; <c>null</c> — вираз не віддається.</param>
    public static string? Visible(
        FormulaDef? formula,
        TableDef table,
        int columnDefId,
        TemplateVersionSnapshot snapshot,
        Func<int, bool> canReadTable,
        Func<int, bool> canReadColumn,
        IFormulaEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(canReadTable);
        ArgumentNullException.ThrowIfNull(canReadColumn);

        if (formula is null)
        {
            return null;
        }

        if (engine is null)
        {
            return null;
        }

        try
        {
            var parsed = engine.Parse(formula.Expression, formula.Dialect);
            if (!parsed.IsSuccess || parsed.Expression is null)
            {
                return null;
            }

            var extraction = engine.ExtractDependencies(
                parsed.Expression, snapshot, new DependencyContext(table.Id, null, columnDefId));
            if (extraction.Diagnostics.Count > 0)
            {
                return null;
            }

            foreach (var dependency in extraction.Dependencies)
            {
                if (!DependencyVisible(dependency, snapshot, canReadTable, canReadColumn))
                {
                    return null;
                }
            }

            return formula.Expression;
        }
#pragma warning disable CA1031 // розбір чужого тексту не має права зламати зріз; безпечний бік — не віддавати
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static bool DependencyVisible(
        FormulaDependencyRef dependency, TemplateVersionSnapshot snapshot,
        Func<int, bool> canReadTable, Func<int, bool> canReadColumn)
    {
        switch (dependency.DependsOnKind)
        {
            case DependencyExtractor.KindHeader:
                return true;

            // Ребро довідника без таблиці — довідник названо кодом, не коміркою аркуша. ⚠ Ребро з
            // TableDefId (форма REGFIELD) називає комірку аркуша: тоді як Cell. Сьогодні таке ребро
            // доповнює Cell-ребро генеричного обходу; перевірка тут — захист на майбутнє.
            case DependencyExtractor.KindRegistry:
            case DependencyExtractor.KindCell:
            case DependencyExtractor.KindCrossPeriod:
                // `!Formula` — ребро без таблиці: посилання на іншу формулу, не на комірку аркуша.
                if (dependency.TableDefId is not { } tableId)
                {
                    return true;
                }

                if (!canReadTable(tableId))
                {
                    return false;
                }

                if (dependency.ColumnDefId is { } columnId && !canReadColumn(columnId))
                {
                    return false;
                }

                // ⚠ FilterJson у ребра довідника — код поля, а не предикат: умову таблиці воно не несе.
                return dependency.DependsOnKind == DependencyExtractor.KindRegistry
                       || dependency.FilterJson is null
                       || AllColumnsVisible(snapshot, tableId, canReadColumn);

            default:
                return false;
        }
    }

    private static bool AllColumnsVisible(TemplateVersionSnapshot snapshot, int tableId, Func<int, bool> canReadColumn)
    {
        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == tableId);
        return table is not null && table.Columns.Where(c => !c.IsDeleted).All(c => canReadColumn(c.Id));
    }
}