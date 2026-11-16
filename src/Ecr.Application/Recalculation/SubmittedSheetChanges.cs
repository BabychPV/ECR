// src/Ecr.Application/Recalculation/SubmittedSheetChanges.cs

namespace Ecr.Application.Recalculation;

/// <summary>
/// ✎ B79: ПОДАНІ аркуші, у яких прогін перерахунку справді змінив хоч одне збережене число, — єдина підстава для
/// позначки «перераховано після подання» (слово людини 16.11: перераховувати все, крім погодженого, «якщо вхідні дані
/// для нього змінилися»).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Позначка — не «прогін торкнувся аркуша», а «числа аркуша стали іншими». Прогін, що нічого не змінив (нічний
/// розклад над незмінними входами), у набір нічого не кладе, і позначка не ставиться: інакше кожна ніч позначала б
/// усі подані аркуші, і погоджувач перестав би їй вірити.
/// </para>
/// <para>
/// Набір наповнюють два джерела, і обидва знають про ЗМІНУ, а не про запис:
/// <list type="bullet">
/// <item>комірки формул — шлях запису <c>RecalculationService</c> (незмінене значення він не пише взагалі), параметр
/// <c>submittedWritten</c> каскаду й повного прогону;</item>
/// <item>результати методологій — порівняння нового прогону з актуальним у транзакції перемикання
/// (<c>ICalculationResultStore.ChangedResultsAsync</c>): новий прогін пише всі числа заново, тож «записано» там
/// нічого не означає.</item>
/// </list>
/// </para>
/// <para>
/// Спільна частина для всіх шляхів перерахунку (каскад формул, повний прогін документа, нічний розклад, кнопка,
/// автоперерахунок після збору й залежних; B80 бере її ж). Не потокобезпечна: один прогін — один набір.
/// </para>
/// </remarks>
public sealed class SubmittedSheetChanges
{
    private readonly HashSet<(long DocumentId, int PeriodKey, int SheetDefId, long CopyKey)> changed = [];

    /// <summary>Скільки поданих аркушів (документ × період × аркуш × копія) змінено.</summary>
    public int Count => changed.Count;

    /// <summary>Запам'ятовує, що в поданому аркуші змінилося хоч одне число.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    public void Add(long documentId, int periodKey, int sheetDefId, long? sheetCopyId)
        => changed.Add((documentId, periodKey, sheetDefId, sheetCopyId ?? 0L));

    /// <summary>Запам'ятовує подані аркуші одного документа й періоду, у які шлях запису формул записав змінені комірки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheets">Аркуші (аркуш × копія) з <c>submittedWritten</c>.</param>
    public void AddRange(long documentId, int periodKey, IEnumerable<(int SheetDefId, long? CopyId)> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        foreach (var (sheetDefId, copyId) in sheets)
        {
            Add(documentId, periodKey, sheetDefId, copyId);
        }
    }

    /// <summary>Чи змінилося хоч одне число поданого аркуша — тобто чи ставити позначку.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <returns><c>true</c> — змінилося.</returns>
    public bool Contains(long documentId, int periodKey, int sheetDefId, long? sheetCopyId)
        => changed.Contains((documentId, periodKey, sheetDefId, sheetCopyId ?? 0L));
}
