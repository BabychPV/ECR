// src/Ecr.Application/Ports/ITableFillStore.cs

using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Скільки комірок документа вже заповнено — по таблицях, одним проходом
/// (<c>BE-10</c>).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод <see cref="ICellStore"/>: той віддає
/// <b>значення</b> комірок, і зріз документа з 91 таблицею — це десятки тисяч
/// рядків на дроті заради двох чисел на таблицю. Питання «скільки» має
/// відповідатися <c>GROUP BY</c> у базі, а не підрахунком у пам'яті над
/// вивантаженим зрізом (той самий привід, що вже записаний над
/// <see cref="IRowStore"/>: там потрібні версії, а не значення).
///
/// ⛔ <c>PeriodKey</c> стоїть у предикаті КОЖНОГО запиту — це ключ партиції
/// <c>doc.CellValue</c> і <c>doc.TableRow</c>. Без нього в <c>WHERE</c> запит
/// не має чим засікатися й іде по ВСІХ партиціях (урок <c>WR-05</c>, той
/// самий, що вже записаний над <c>IRowStore.TouchRowsAsync</c>).
/// </remarks>
public interface ITableFillStore
{
    /// <summary>
    /// Лічильники заповненості таблиць документа за період.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період; він же ключ партиції.</param>
    /// <param name="computedColumnDefIds">
    /// Колонки, які заповнює НЕ людина (<c>ColumnDef.IsComputed</c> —
    /// формула або вихід методології; <c>ColumnDef.IsReadOnly</c> — заповнює
    /// імпорт чи збір). Їхні комірки не входять у
    /// <see cref="TableFillCounts.FilledCells"/> — інакше «заповнено 40 %»
    /// вимірювало б частку формул у шаблоні, а не роботу людини.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// По одному запису на таблицю, у якої є хоч один рядок. Таблиця без
    /// жодного матеріалізованого рядка в результат не потрапляє — шукай її
    /// ключ через <c>TryGetValue</c>, а не індексатор.
    /// </returns>
    public Task<IReadOnlyList<TableFillCounts>> GetFillCountsAsync(
        long documentId,
        PeriodKey periodKey,
        IReadOnlyCollection<int> computedColumnDefIds,
        CancellationToken ct);

    /// <summary>Правила доступу до періоду версії шаблону.</summary>
    /// <remarks>
    /// ⚠ Читання лежить тут, а не в <c>IMetadataCache</c>: знімок метаданих
    /// описує СТРУКТУРУ версії, а правила доступу — політика над нею, і
    /// живуть вони в окремій таблиці <c>cfg.PeriodAccessRuleDef</c>. Саме
    /// рішення ухвалює чиста функція
    /// <c>Ecr.Application.Security.PeriodAccessRules.Evaluate</c>; порт лише
    /// приносить їй правила.
    /// </remarks>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PeriodAccessRuleDef>> GetPeriodAccessRulesAsync(
        int templateVersionId, CancellationToken ct);
}

/// <summary>Лічильники однієї таблиці документа за період.</summary>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="RowCount">Скільки нерозмічених видаленими рядків матеріалізовано.</param>
/// <param name="FilledCells">
/// Скільки комірок у НЕобчислюваних колонках має збережене значення.
/// Комірка з <c>IsEmpty = 1</c> (явна порожнеча, <c>R-B4</c>) сюди входить:
/// людина на це питання відповіла, і без цього таблиця з однією свідомо
/// порожньою коміркою ніколи не дійшла б до 100 %. Комірка з
/// <c>IsCalculated = 1</c> не входить — її написав перерахунок, не людина.
/// </param>
public sealed record TableFillCounts(int TableDefId, int RowCount, int FilledCells);
