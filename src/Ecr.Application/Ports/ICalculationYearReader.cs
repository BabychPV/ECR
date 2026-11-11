// src/Ecr.Application/Ports/ICalculationYearReader.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Сирі факти для огляду «Calculation results — усі місяці року» (CALC-RESULTS.md §3): числа актуальних прогонів
/// і розраховані комірки формул шаблону за кілька періодів документа одним набором запитів.
/// </summary>
/// <remarks>
/// ⛔ Набір запитів на ДОКУМЕНТ × РІК, а не на місяць і не на рядок: бюджет — ≤ 1 с теплий для документа Land.
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
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKeys">Періоди року (місяці), для відсікання партицій.</param>
    /// <param name="tableInstanceIds">Екземпляри таблиць документа за ці періоди (рядки й комірки формул).</param>
    /// <param name="labelColumnIds">Колонки, з яких береться підпис динамічного рядка (назва, код).</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// <c>ECR-CALC-0422</c>: чисел понад стелю — показати їх частково означало б показати хибні суми.
    /// </exception>
    public Task<CalculationYearFacts> ReadYearAsync(
        long documentId,
        IReadOnlyList<int> periodKeys,
        IReadOnlyCollection<long> tableInstanceIds,
        IReadOnlyCollection<int> labelColumnIds,
        CancellationToken ct);
}

/// <summary>Факти року.</summary>
/// <param name="Runs">Актуальний прогін кожного періоду, що має числа документа (власний документний або проєктний).</param>
/// <param name="Results">Числа методологій актуальних прогонів (лише виходи, без проміжних).</param>
/// <param name="FormulaCells">Розраховані комірки формул шаблону (<c>IsCalculated = 1</c>, числові).</param>
/// <param name="Rows">Рядки екземплярів таблиць (ключ, визначення, порядок).</param>
/// <param name="LabelCells">Значення колонок-підписів рядків.</param>
/// <param name="Entries">Записи довідників, на які посилаються речовини й підписи.</param>
/// <param name="Versions">Версії методологій, що дали числа.</param>
public sealed record CalculationYearFacts(
    IReadOnlyList<YearRun> Runs,
    IReadOnlyList<YearResult> Results,
    IReadOnlyList<YearFormulaCell> FormulaCells,
    IReadOnlyList<YearRow> Rows,
    IReadOnlyList<YearLabelCell> LabelCells,
    IReadOnlyList<YearEntry> Entries,
    IReadOnlyList<YearMethodologyVersion> Versions)
{
    /// <summary>Порожні факти.</summary>
    public static CalculationYearFacts Empty { get; } = new([], [], [], [], [], [], []);
}

/// <summary>Актуальний прогін періоду.</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="RunId">Прогін.</param>
/// <param name="FinishedAt">Коли завершився (UTC).</param>
public sealed record YearRun(int PeriodKey, long RunId, DateTime? FinishedAt);

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
