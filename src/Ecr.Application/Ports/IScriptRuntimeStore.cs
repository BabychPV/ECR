namespace Ecr.Application.Ports;

/// <summary>Методологія, що читає таблицю (<c>calc.MethodologyReadTable</c>).</summary>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="MethodologyId">Методологія-читач.</param>
public sealed record ReadTableReader(string TableCode, int MethodologyId);

/// <summary>Ребро залежності «читач → джерело результатів» (<c>calc.MethodologyDependency</c>).</summary>
/// <param name="FromMethodologyId">Методологія, що посилається.</param>
/// <param name="ToMethodologyId">Методологія, на яку посилаються.</param>
public sealed record MethodologyEdge(int FromMethodologyId, int ToMethodologyId);

/// <summary>
/// Читання, потрібні рушієві скриптів методологій і трекінгові застарілості, що не лягають у
/// <see cref="IMethodologyStore"/> (MS, план §13.1, §13.9).
/// </summary>
public interface IScriptRuntimeStore
{
    /// <summary>Коди шаблонів за версіями шаблонів (для <c>rows from Шаблон.Таблиця</c>).</summary>
    /// <param name="templateVersionIds">Версії шаблонів.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версія шаблону → код шаблону; невідомої версії в результаті немає.</returns>
    public Task<IReadOnlyDictionary<int, string>> GetTemplateCodesAsync(
        IReadOnlyCollection<int> templateVersionIds, CancellationToken ct);

    /// <summary>
    /// Коди таблиць, які читають ОПУБЛІКОВАНІ або виведені з обігу версії названих методологій
    /// (<c>calc.MethodologyReadTable</c>).
    /// </summary>
    /// <param name="methodologyIds">Методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Різні коди таблиць; порожньо — ці методології нічого не читають.</returns>
    public Task<IReadOnlyList<string>> GetReadTableCodesAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct);

    /// <summary>
    /// Транзитивне замикання «методологія → методології-джерела» за <c>calc.MethodologyDependency</c>
    /// (від читача до того, чиї результати він читає). Самі вхідні методології теж у результаті.
    /// </summary>
    /// <param name="methodologyIds">Початкові методології.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> GetSourceClosureAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct);

    /// <summary>
    /// Транзитивне замикання «методологія → її читачі» (хто читає її результати) за <c>calc.MethodologyDependency</c>.
    /// Самі вхідні методології теж у результаті.
    /// </summary>
    /// <param name="methodologyIds">Початкові методології.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> GetReaderClosureAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct);

    /// <summary>
    /// Методології, чиї ОПУБЛІКОВАНІ або виведені з обігу версії читають названі таблиці, — з кодом таблиці, яку
    /// кожна читає (для перерахунку: правка в таблиці тягне за собою читачів).
    /// </summary>
    /// <param name="tableCodes">Коди таблиць (без урахування регістру).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ReadTableReader>> GetReadersOfTablesAsync(
        IReadOnlyCollection<string> tableCodes, CancellationToken ct);

    /// <summary>Усі ребра <c>calc.MethodologyDependency</c> (читач → джерело); таблиця мала, граф будується в пам'яті.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<MethodologyEdge>> GetDependencyEdgesAsync(CancellationToken ct);

    /// <summary>Таблиці (<c>TableDefId</c>), до яких активно прив'язані виходи названих методологій.</summary>
    /// <param name="methodologyIds">Методології.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> GetBoundTableDefIdsAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct);
}
