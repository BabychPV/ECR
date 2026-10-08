// src/Ecr.Application/Ports/ICalculationResultStore.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис результатів прогону розрахунку.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Calculations.CalculationOutputWriter</c> був типізований напряму на
/// <c>EcrDbContext</c> і <c>BulkCellLoader</c>.
///
/// Пише <b>тільки</b> в <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c>.
/// У <c>doc.CellValue</c> результати методологій не потрапляють ніколи (D-69):
/// інакше нічний перерахунок писав би десятки мільйонів рядків у партиції
/// документів і роздував <c>aud.CellChange</c>.
/// </remarks>
public interface ICalculationResultStore
{
    /// <summary>
    /// Пише результати пакетно (<c>SqlBulkCopy</c>). <c>SaveChanges</c> у циклі
    /// заборонений: бюджет річного перерахунку — 10 хвилин (ПРД-13).
    /// </summary>
    public Task WriteResultsAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct);

    /// <summary>
    /// Пише трейс — лише те, що передбачає <paramref name="traceLevel"/>.
    /// Керуємо тим, <b>що</b> пишемо, а не скільки зберігаємо (ЗБР-3).
    /// </summary>
    public Task WriteTraceAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel, CancellationToken ct);

    /// <summary>
    /// Визначає поточні зрізи <c>rpt.*</c>, які цей прогін зробив застарілими
    /// (ФВ-10.5): вони побудовані раніше, ніж прогін став актуальним, і досі
    /// не були застарілими через інший прогін.
    /// </summary>
    /// <param name="calculationRunId">Прогін, щойно зроблений актуальним.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Зрізи, що застаріли саме зараз; порожньо — таких немає.</returns>
    /// <remarks>
    /// ⛔ «Застарілий» — це СТАН, а не дія над зрізом: метод нічого не пише в
    /// <c>rpt.ReportSnapshot</c>. Ні рядки, ні сума, ні статус зрізу не
    /// змінюються, тож поданий зріз лишається іммутабельним (ФВ-9.17), а
    /// регуляторна вʼюха віддає ті самі числа. Ознаку читач отримує в
    /// <see cref="ReportSnapshotSummary.IsStale"/>; викликач пише перелік у
    /// журнал у ТІЙ САМІЙ транзакції, що й перемикання актуальності.
    /// </remarks>
    public Task<IReadOnlyList<InvalidatedReportSnapshot>> InvalidateReportSnapshotsAsync(
        long calculationRunId, CancellationToken ct);

    /// <summary>
    /// Робить прогін актуальним: попередній перестає бути таким **у тій самій
    /// транзакції** (ФВ-9.11).
    /// </summary>
    /// <param name="calculationRunId">Прогін, який стає актуальним.</param>
    /// <param name="modulesProfileJson">Профіль по модулях; пишеться завжди.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Прапорець живе на ПРОГОНІ, а не на кожному результаті: у схемі
    /// <c>calc.CalculationResult</c> колонки <c>IsCurrent</c> немає, і це
    /// правильно — інакше «перемикання актуального прогону» означало б
    /// оновити десятки мільйонів рядків, і «одна транзакція» з вимоги стала б
    /// блокуванням партиції на хвилини. Результат актуальний тоді, коли
    /// актуальний його прогін.
    /// <para>
    /// Дві половини — зняти зі старого і поставити новому — мусять бути
    /// нероздільні: між ними існує стан, у якому актуальних прогонів нуль або
    /// два, і звіт, побудований у цю мить, не має правильної відповіді.
    /// Викликач відкриває транзакцію; реалізація спершу знімає актуальність
    /// зі старіших, потім ставить новому.
    /// </para>
    /// <para>
    /// ⛔ Прогін, СТАРІШИЙ за вже актуальний прогін тієї ж області (менший Id),
    /// актуальним не стає: завершується станом <c>Superseded</c> із причиною
    /// <c>jobs.calculationRunSupersededByNewer</c>, без винятку.
    /// </para>
    /// </remarks>
    public Task SwitchCurrentRunAsync(
        long calculationRunId, string modulesProfileJson, CancellationToken ct);

    /// <summary>
    /// Переносить у прогін <paramref name="calculationRunId"/> результати методологій, яких цей прогін
    /// НЕ перераховував (RC14, приймальна №10, P2-4): прогін області одного аркуша стає актуальним для
    /// ВСЬОГО документа й періоду, тож без переносу результати інших аркушів зникли б із
    /// <see cref="ReadCurrentAsync"/>.
    /// </summary>
    /// <param name="calculationRunId">Новий прогін (ще не актуальний).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="methodologyIds">Методології (не версії), чиї результати переносяться.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки результатів перенесено.</returns>
    /// <remarks>
    /// ⚠ Джерело — ті самі рядки, що віддасть <see cref="ReadCurrentAsync"/> ДО перемикання актуальності,
    /// тож викликати треба до <see cref="SwitchCurrentRunAsync"/> у тій самій транзакції. Рядки лише
    /// додаються в контекст — зберігає викликач. Трейс (кроки, входи) не переноситься: він лишається за
    /// прогоном, що його порахував.
    /// </remarks>
    public Task<int> CarryOverResultsAsync(
        long calculationRunId, long documentId, IReadOnlyCollection<int> methodologyIds, CancellationToken ct);

    /// <summary>
    /// Числа <b>актуального</b> прогону для документа й періоду.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Результати в порядку рядка й коду виходу; порожньо — прогону немає.</returns>
    /// <remarks>
    /// ⛔ Читання живе в тому самому порту, що й запис, бо предмет один:
    /// <c>calc.CalculationResult</c> партиційована за періодом, а «актуальність»
    /// живе на ПРОГОНІ (<c>CalculationRun.CurrentStatus</c>), не на рядку. Другий
    /// порт мав би повторити обидва ці знання — і саме там вони й розійшлися б.
    ///
    /// ⛔ Береться саме актуальний прогін, а не останній за часом. Прогін, який
    /// упав, лишає по собі частину рядків; віддати їх означало б показати в
    /// звіті числа, половина яких порахована старою версією методології.
    ///
    /// ⚠ Без цього методу результат методології не було видно НІДЕ (<c>D-69</c>:
    /// у <c>doc.CellValue</c> він не потрапляє). Перерахунок завершувався
    /// успіхом, число лягало в базу — і єдиним способом його побачити був
    /// <c>SELECT</c>.
    /// </remarks>
    public Task<IReadOnlyList<CalculationResultRow>> ReadCurrentAsync(
        long documentId, int periodKey, CancellationToken ct);
}

/// <summary>Які результати попереднього актуального прогону перенести в новий (RC14, P2-4).</summary>
/// <param name="DocumentId">Документ прогону.</param>
/// <param name="MethodologyIds">Методології, яких прогін області не перераховував.</param>
public sealed record ResultCarryOver(long DocumentId, IReadOnlyCollection<int> MethodologyIds);

/// <summary>Один рядок актуального результату розрахунку.</summary>
/// <param name="MethodologyVersionId">Версія, що дала число: без неї його неможливо пояснити.</param>
/// <param name="SourceRowKey">Рядок документа; <c>null</c> — рівень таблиці.</param>
/// <param name="OutputCode">Код виходу методології.</param>
/// <param name="Value">Значення; <c>decimal</c>, ніколи <c>float</c> (<c>D-30</c>).</param>
/// <param name="UnitId">Одиниця результату — обов'язкова (ФВ-16.6).</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> — вихід без речовини.</param>
public sealed record CalculationResultRow(
    int MethodologyVersionId,
    string? SourceRowKey,
    string OutputCode,
    decimal Value,
    int UnitId,
    long? SubstanceEntryId);

/// <summary>Зріз, який прогін зробив застарілим (ФВ-10.5).</summary>
/// <param name="SnapshotId">Зріз.</param>
/// <param name="ReportVersionId">Версія звіту.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="TemplateVersionId">Версія шаблону проєкту — ключ журналу структурних змін.</param>
/// <param name="PeriodKey">Період зрізу; <c>null</c> — увесь рік.</param>
/// <param name="Status">Статус даних зрізу (D-65); не змінюється.</param>
/// <param name="BuiltAt">Коли зріз побудовано (UTC).</param>
public sealed record InvalidatedReportSnapshot(
    long SnapshotId,
    int ReportVersionId,
    int ProjectId,
    int TemplateVersionId,
    int? PeriodKey,
    string Status,
    DateTime BuiltAt);
