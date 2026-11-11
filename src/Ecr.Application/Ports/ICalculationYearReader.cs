// src/Ecr.Application/Ports/ICalculationYearReader.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Сирі факти для огляду «Calculation results — усі місяці року» (CALC-RESULTS.md §3): числа актуальних прогонів
/// і розраховані комірки формул шаблону за кілька періодів документа одним набором запитів.
/// </summary>
/// <remarks>
/// ⛔ Набір запитів на ДОКУМЕНТ × РІК (або документ × рік × таблицю), а не на місяць і не на рядок.
/// Вибір актуального прогону — те саме правило, що й у <see cref="ICalculationResultStore.ReadCurrentAsync"/>
/// (власний документний прогін періоду має перевагу над проєктним), застосоване до кожного періоду окремо.
/// <para>
/// ⚠ Лише гаряча схема: рядки й комірки періодів, перенесених в архів (<c>itg.ArchiveRun</c>), тут не читаються —
/// для них огляд покаже числа методологій без підписів рядків і без формул шаблону.
/// </para>
/// </remarks>
public interface ICalculationYearReader
{
    /// <summary>Читає факти року.</summary>
    /// <exception cref="Errors.BusinessRuleException">
    /// <c>ECR-CALC-0422</c>: чисел понад стелю — показати їх частково означало б показати хибні суми.
    /// </exception>
    public Task<CalculationYearFacts> ReadYearAsync(CalculationYearReadRequest request, CancellationToken ct);

    /// <summary>Рядки екземплярів таблиць (ключ, порядок) — господарі чисел до D-401 у режимі однієї таблиці.</summary>
    /// <param name="periodKeys">Періоди (відсікання партицій).</param>
    /// <param name="tableInstanceIds">Екземпляри.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<YearRow>> ReadRowsAsync(
        IReadOnlyList<int> periodKeys, IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct);
}

/// <summary>Що читати.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKeys">Періоди року (місяці), для відсікання партицій.</param>
/// <param name="TableInstanceIds">
/// Екземпляри таблиць, з яких читаються рядки, підписи й комірки формул; у режимі <paramref name="SliceResults"/> — ще
/// й числа методологій (плюс числа без екземпляра, до D-401).
/// </param>
/// <param name="LabelColumnIds">Колонки підпису динамічного рядка (назва, код); порожньо — підписи не читаються.</param>
/// <param name="IncludeRows">Чи читати рядки екземплярів (порядок обладнання, господарі чисел до D-401).</param>
/// <param name="SliceResults">Числа методологій — лише екземплярів <paramref name="TableInstanceIds"/> (і без екземпляра).</param>
/// <param name="Aggregate">
/// Лише наявність числа в місяці, без значень (зведення): SQL групує числа й комірки формул до виходу з маскою місяців
/// замість тисяч рядків на кожен місяць; <c>Value</c> у фактах тоді = 0 і в відповідь не йде.
/// </param>
public sealed record CalculationYearReadRequest(
    long DocumentId,
    IReadOnlyList<int> PeriodKeys,
    IReadOnlyCollection<long> TableInstanceIds,
    IReadOnlyCollection<int> LabelColumnIds,
    bool IncludeRows,
    bool SliceResults,
    bool Aggregate);

/// <summary>Факти року.</summary>
/// <param name="Runs">Актуальний прогін кожного періоду, що має числа документа (власний документний або проєктний).</param>
/// <param name="ResultKeys">
/// Ключі чисел методологій УСЬОГО документа (без значень і рядків): для стану прогону місяця вузькому читачу — він не
/// залежить від того, яку таблицю читає запит.
/// </param>
/// <param name="Results">Числа методологій актуальних прогонів (лише виходи, без проміжних).</param>
/// <param name="FormulaCells">Розраховані комірки формул шаблону (<c>IsCalculated = 1</c>, числові).</param>
/// <param name="Rows">Рядки екземплярів таблиць (ключ, визначення, порядок).</param>
/// <param name="LabelCells">Значення колонок-підписів рядків.</param>
/// <param name="Entries">Записи довідників, на які посилаються речовини й підписи.</param>
/// <param name="Versions">Версії методологій, що дали числа.</param>
public sealed record CalculationYearFacts(
    IReadOnlyList<YearRun> Runs,
    IReadOnlyList<YearResultKey> ResultKeys,
    IReadOnlyList<YearResult> Results,
    IReadOnlyList<YearFormulaCell> FormulaCells,
    IReadOnlyList<YearRow> Rows,
    IReadOnlyList<YearLabelCell> LabelCells,
    IReadOnlyList<YearEntry> Entries,
    IReadOnlyList<YearMethodologyVersion> Versions)
{
    /// <summary>Порожні факти.</summary>
    public static CalculationYearFacts Empty { get; } = new([], [], [], [], [], [], [], []);
}

/// <summary>Актуальний прогін періоду.</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="RunId">Прогін.</param>
/// <param name="FinishedAt">Коли завершився (UTC).</param>
public sealed record YearRun(int PeriodKey, long RunId, DateTime? FinishedAt);

/// <summary>Ключ чисел методології (період, екземпляр, версія, вихід) — те, від чого залежить звуження доступу.</summary>
public sealed record YearResultKey(int PeriodKey, long? TableInstanceId, int MethodologyVersionId, string OutputCode);

/// <summary>Число методології.</summary>
public sealed record YearResult(
    int PeriodKey,
    int MethodologyVersionId,
    long? TableInstanceId,
    string? SourceRowKey,
    string OutputCode,
    long? SubstanceEntryId,
    int UnitId,
    decimal Value);

/// <summary>Розрахована комірка формули шаблону.</summary>
public sealed record YearFormulaCell(int PeriodKey, long TableInstanceId, string RowKey, int ColumnDefId, decimal Value);

/// <summary>Рядок екземпляра таблиці.</summary>
public sealed record YearRow(long TableInstanceId, string RowKey, int? RowDefId, int Ordinal);

/// <summary>Значення колонки-підпису рядка.</summary>
public sealed record YearLabelCell(
    int PeriodKey, long TableInstanceId, string RowKey, int ColumnDefId, string? Text, long? EntryId, decimal? Number);

/// <summary>Запис довідника.</summary>
public sealed record YearEntry(long Id, string Code, LocalizedText DisplayL10n);

/// <summary>Версія методології.</summary>
public sealed record YearMethodologyVersion(
    int MethodologyVersionId, int MethodologyId, string MethodologyCode, LocalizedText NameL10n, string Version);
