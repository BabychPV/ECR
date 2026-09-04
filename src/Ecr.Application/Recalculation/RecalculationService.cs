using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Інкрементний перерахунок формул шаблону.
/// </summary>
/// <remarks>
/// Результати **формул шаблону** матеріалізуються в <c>doc.CellValue</c> з
/// <c>IsCalculated = 1</c>. Результати **методологій** сюди не потрапляють —
/// вони живуть у <c>calc.CalculationResult</c> (D-69). Плутати ці два шляхи
/// не можна.
/// </remarks>
public sealed class RecalculationService(
    ICellStore cellStore,
    IMetadataCache metadata,
    IFormulaEngine formulaEngine)
{
    /// <summary>Перераховує залежне піддерево.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="dirty">Змінені комірки.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RecalculateAsync(long documentId, PeriodKey periodKey, DirtySet dirty, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) за зворотним індексом cfg.FormulaDependency знайти формули, залежні від seeds; " +
            "2) розкрити транзитивно, поки набір не перестане рости; " +
            "3) відсортувати за EvaluationOrder (він уже обчислений при Publish — не сортувати граф тут); " +
            "4) обчислити і записати з IsCalculated = 1 пакетно; " +
            "5) крос-аркушні rollup позначити брудними і перерахувати ВІДКЛАДЕНО (debounce ~300 мс), " +
            "а не синхронно — інакше зміна однієї комірки тягне ланцюг по всьому документу; " +
            "6) формули з IsSnapshot не перераховувати каскадом ніколи.");
}
