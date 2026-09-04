namespace Ecr.DataGen;

/// <summary>
/// Заміри гейта BR-07 — шість критеріїв із <c>tz/04</c> §4.3.
/// </summary>
/// <remarks>
/// ⚠ Найважливіший і найлегший для пропуску — замір №6: **125 RPS в одну
/// партицію**. Пік у ECR не розподілений: усі користувачі в останні дні
/// періоду б'ють в один період. Рівномірне навантаження на 12 партицій
/// нічого не доводить.
/// </remarks>
public sealed class GateBenchmark
{
    /// <summary>Виконує всі заміри і друкує звіт.</summary>
    public Task<GateResult> RunAsync(string connectionString, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — шість замірів:\n" +
            "1) ReadSliceAsync 500×60        → ціль p95 < 600 мс\n" +
            "2) ApplyAsync 100 комірок       → ціль p95 < 150 мс\n" +
            "3) агрегація по періоду (rollup) → < 500 мс на вже матеріалізованих значеннях;\n" +
            "   ⛔ індексована вʼюха неприпустима (ФВ-0.3)\n" +
            "4) повний цикл архівації року   → без блокування робочих запитів\n" +
            "5) розмір doc.CellValue після PAGE → оцінка ГБ/рік для sizing\n" +
            "6) 125 RPS в ОДНУ партицію, 15 хв → без ескалації блокувань, p95 у бюджеті\n" +
            "Заміри виконувати в режимі EditionMode = Standard: бюджет має витримуватися " +
            "на базовій редакції, Enterprise дає запас, а не умову (АРХ-7).");
}

/// <summary>Результат гейта.</summary>
/// <param name="Passed">Чи пройдено всі шість критеріїв.</param>
/// <param name="Measurements">Заміри: назва → значення.</param>
/// <param name="Failures">Критерії, які не пройдено, з фактичними числами.</param>
public sealed record GateResult(
    bool Passed,
    IReadOnlyDictionary<string, double> Measurements,
    IReadOnlyList<string> Failures);
