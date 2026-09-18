// src/Ecr.Application/Recalculation/CellValueComparison.cs

using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Порівняння значення комірки зі значенням, яке збирається туди записати
/// перерахунок.
/// </summary>
/// <remarks>
/// ⛔ Директива №14 частина 3, <c>DAT-02</c> п. 1. До цього
/// <c>RecalculationService.Evaluate</c> додавав <c>upsert</c> БЕЗУМОВНО, не
/// дивлячись на те, що вже лежить у комірці. Колонкова формула віддає ціль у
/// КОЖНОМУ рядку таблиці (<c>Targets</c>), тож правка однієї комірки давала до
/// 500 рядків <c>MERGE</c>, стільки ж рядків аудиту з <c>старе = нове</c> і
/// стільки ж піднятих <c>RowVersion</c> — тобто хибні <c>409</c> у всіх, хто
/// тримає ту таблицю відкритою. Незмінене значення не є зміною, і писати його
/// не можна не з міркувань швидкості, а тому, що кожен такий запис БРЕШЕ:
/// журнал стверджує зміну, якої не було, а версія рядка — правку, якої не
/// робили.
///
/// ⚠ Чистий метод і окремий тип навмисно: це єдине місце, де вирішується
/// «те саме чи інше», і воно мусить бути перевіреним окремо від усього
/// конвеєра перерахунку. Директива називає його ж першим методом майбутньої
/// стратегії на <c>CellDataType</c> (<c>AR-04</c>).
/// </remarks>
public static class CellValueComparison
{
    /// <summary>
    /// Чи означає <paramref name="candidate"/> те саме, що вже записане в
    /// <paramref name="current"/>.
    /// </summary>
    /// <param name="current">
    /// Значення, яке лежить у комірці зараз; <c>null</c> — комірки в базі
    /// НЕМАЄ.
    /// </param>
    /// <param name="candidate">Значення, яке порахувала формула.</param>
    /// <returns><c>true</c> — записувати нічого.</returns>
    /// <remarks>
    /// ⛔ <c>null</c> — це «комірки не було», а НЕ «комірка порожня» (R-B4).
    /// Відсутня комірка ніколи не дорівнює порахованому значенню: інакше
    /// перший же прогін на новому рядку не записав би нічого, і число
    /// з'явилося б лише після того, як хтось змінить вхід удруге.
    ///
    /// ⚠ Порівняння <c>decimal</c> — оператором <c>==</c>, тобто за ЧИСЛОМ, а
    /// не за поданням: <c>1.0m == 1.00m</c> — істина, хоча
    /// <c>decimal.Equals</c> для них теж істина, а от <c>ToString()</c> дає
    /// різні рядки. Масштаб зберігається в <c>doc.CellValue</c> як властивість
    /// колонки, а не значення, і вважати зміною перехід <c>1.0 → 1.00</c>
    /// означало б переписувати всю таблицю щоразу, коли формула поверне той
    /// самий результат з іншим масштабом.
    ///
    /// ⚠ Рядки — <see cref="StringComparison.Ordinal"/>: значення комірки не
    /// має культури, і «рівність без урахування регістру» тут перетворила б
    /// правку <c>"га" → "ГА"</c> на «нічого не сталося».
    ///
    /// ⚠ <see cref="CellValueData.IsCalculated"/> входить у порівняння. Число
    /// 15, введене людиною, і число 15, пораховане формулою, — РІЗНІ стани:
    /// зріз позначає другий як недоступний для правки (<c>CalculatedCell</c>),
    /// і пропустити перехід між ними означало б лишити комірку назавжди
    /// «людською» з першої ж збіжності.
    /// </remarks>
    public static bool AreEqual(CellValueData? current, CellValueData candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (current is null)
        {
            return false;
        }

        return current.IsEmpty == candidate.IsEmpty
            && current.IsCalculated == candidate.IsCalculated
            && current.ValueNumeric == candidate.ValueNumeric
            && current.ValueDate == candidate.ValueDate
            && current.ValueBool == candidate.ValueBool
            && current.ValueRegistryEntryId == candidate.ValueRegistryEntryId
            && current.ValueUnitId == candidate.ValueUnitId
            && string.Equals(current.ValueString, candidate.ValueString, StringComparison.Ordinal);
    }
}
