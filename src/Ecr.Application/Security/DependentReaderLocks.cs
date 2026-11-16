using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>✎ B79 (квіз 16.11): аркуш-читач стовпця (інший, ніж аркуш правки) — для замків і відмови.</summary>
/// <param name="SheetDefId">Аркуш формули-читача.</param>
/// <param name="SheetCode">Код аркуша (подробиця <c>sheets</c> відмови).</param>
public sealed record DependentReaderSheet(int SheetDefId, string SheetCode);

/// <summary>
/// ✎ B79 (квіз 16.11; координатор: «перевірка після факту без замка — не досить»): спільні замки аркушів-ЧИТАЧІВ стовпця
/// за місяці року разом із замками аркуша правки — в ОДНОМУ каноничному порядку (аркуш ↑, копія ↑, період ↑, як
/// <c>RecalculationService.EnterSheetsAsync</c>), і повторна перевірка правила під ними.
/// </summary>
/// <remarks>
/// ⚠ Порядок — не «спершу аркуш правки»: аркуш-читач із меншим Id береться раніше. Затвердження місяця читача бере ВИНЯТКОВИЙ
/// замок того самого ключа, тож тут або воно ще не почалось (і чекатиме коміту правки), або вже зафіксоване — і стан
/// <c>Approved</c> відхиляє запис. Копії аркушів-читачів не блокуються (замок — оригіналу; затверджена копія читача
/// враховується рішенням поза транзакцією) — відома межа.
/// </remarks>
public static class DependentReaderLocks
{
    /// <summary>Крок плану замків: аркуш правки (<c>Reader = false</c>) чи аркуш-читач.</summary>
    /// <param name="SheetDefId">Аркуш.</param>
    /// <param name="CopyId">Копія; <c>null</c> — оригінал.</param>
    /// <param name="Period">Ключ періоду.</param>
    /// <param name="Reader">Аркуш-читач.</param>
    public readonly record struct Step(int SheetDefId, long? CopyId, int Period, bool Reader);

    /// <summary>Усі замки запису в каноничному порядку: аркуш ↑, копія ↑, період ↑; без повторів.</summary>
    /// <param name="sheetDefId">Аркуш правки.</param>
    /// <param name="copyId">Копія аркуша правки.</param>
    /// <param name="ownPeriods">Періоди аркуша правки.</param>
    /// <param name="readers">Аркуші-читачі.</param>
    /// <param name="readerPeriods">Місяці року для аркушів-читачів.</param>
    public static IReadOnlyList<Step> Plan(
        int sheetDefId, long? copyId, IEnumerable<int> ownPeriods,
        IEnumerable<DependentReaderSheet> readers, IEnumerable<int> readerPeriods)
    {
        ArgumentNullException.ThrowIfNull(ownPeriods);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(readerPeriods);

        var months = readerPeriods.Distinct().ToList();
        var steps = ownPeriods.Distinct().Select(p => new Step(sheetDefId, copyId, p, Reader: false))
            .Concat(readers
                .Select(r => r.SheetDefId)
                .Where(r => r != sheetDefId)
                .Distinct()
                .SelectMany(r => months.Select(p => new Step(r, null, p, Reader: true))));

        return [.. steps.OrderBy(s => s.SheetDefId).ThenBy(s => s.CopyId ?? 0).ThenBy(s => s.Period)];
    }

    /// <summary>Повторна перевірка під замками: затверджений місяць аркуша-читача відхиляє запис з <c>sheets</c>.</summary>
    /// <param name="readers">Аркуші-читачі.</param>
    /// <param name="periods">Місяці року.</param>
    /// <param name="statuses">Стани аркушів-читачів, прочитані під замками: (аркуш, період).</param>
    /// <param name="deniedCount">Скільки комірок відхиляє відмова.</param>
    /// <exception cref="Errors.AccessDeniedException"><c>DependentMonthsLocked</c> з <c>sheets</c>.</exception>
    public static void ThrowIfLocked(
        IEnumerable<DependentReaderSheet> readers, IEnumerable<int> periods,
        IReadOnlyDictionary<(int SheetDefId, int Period), DocumentStatus> statuses, int deniedCount)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(statuses);

        var months = periods.Distinct().ToList();
        var blocks = readers
            .DistinctBy(r => r.SheetDefId)
            .OrderBy(r => r.SheetCode, StringComparer.Ordinal)
            .Select(r => new DependentMonthsBlock(
                r.SheetDefId, r.SheetCode,
                DependentMonthsRule.BlockingMonths(
                    months.Where(p => statuses.ContainsKey((r.SheetDefId, p))),
                    p => statuses[(r.SheetDefId, p)])))
            .Where(b => b.Months.Count > 0)
            .ToList();

        if (blocks.Count == 0)
        {
            return;
        }

        throw DependentMonthsRule.Denied(
            deniedCount, [.. blocks.SelectMany(b => b.Months).Distinct().Order()], blocks);
    }

    /// <summary>
    /// Бере замки плану через <paramref name="gate"/>: аркуш правки — <paramref name="enterOwn"/> (у викликача там свої
    /// виняткові кроки), аркуш-читач — спільний замок оригіналу. Повертає стани аркушів-читачів.
    /// </summary>
    /// <param name="gate">Замок аркушів.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="plan">План (<see cref="Plan"/>).</param>
    /// <param name="enterOwn">Як узяти замок аркуша правки за період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<Dictionary<(int SheetDefId, int Period), DocumentStatus>> EnterAsync(
        ISheetEditGate gate, long documentId, IReadOnlyList<Step> plan, Func<int, Task> enterOwn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(enterOwn);

        var readers = new Dictionary<(int, int), DocumentStatus>();
        foreach (var step in plan)
        {
            if (!step.Reader)
            {
                await enterOwn(step.Period).ConfigureAwait(false);
                continue;
            }

            readers[(step.SheetDefId, step.Period)] = await gate
                .EnterEditOfAsync(documentId, step.SheetDefId, new PeriodKey(step.Period), null, ct)
                .ConfigureAwait(false);
        }

        return readers;
    }
}
