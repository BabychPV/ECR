using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Область рядка довідника: контекст, у якому <c>ROW.</c> означає запис
/// <see cref="EntryId"/>; решту делегує зовнішньому контексту
/// (FEATURE-REGISTRY-TABLES §5.2, §5.4).
/// </summary>
/// <remarks>
/// ⚠ Декоратор, а не змінюване поле контексту: область — властивість ОДНОГО
/// спуску обчислювача, і вкладені <c>REGONE</c>/<c>REGSUM</c>/… отримують кожен
/// свою, не відновлюючи нічого в <c>finally</c>. Зовнішній контекст лишається
/// незмінним, тож внутрішній <c>ROW</c> затіняє зовнішній (§5.2) без жодного
/// окремого механізму: найближча область — це сам контекст.
///
/// ⚠ Делегує ВСЕ, зокрема члени інтерфейсу з типовою реалізацією
/// (<see cref="GetRegistryField"/>, <see cref="Registries"/>): без явного
/// делегування вони мовчки взяли б замовчування інтерфейсу, і всередині умови
/// агрегату <c>REGFIELD</c> і вкладений пошук давали б <c>#REF</c>.
///
/// ⛔ Жодного стану поза двома полями конструктора: обчислювач створює область
/// на КОЖЕН рядок, дерево виразу з кешу розбору (<c>CAL-05</c>) про неї не знає.
/// </remarks>
/// <param name="inner">Зовнішній контекст.</param>
/// <param name="entryId">Запис, що перебирається.</param>
internal sealed class RegistryRowScope(IEvaluationContext inner, long entryId) : IEvaluationContext
{
    /// <summary>Запис області.</summary>
    public long EntryId { get; } = entryId;

    /// <inheritdoc />
    public IRegistrySnapshot? Registries => inner.Registries;

    /// <inheritdoc />
    public PeriodContext Period => inner.Period;

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference) => inner.Read(reference);

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
        => inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name) => inner.GetArgument(name);

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => inner.GetConstant(name);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name) => inner.GetFormulaResult(name);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name) => inner.GetHeader(name);

    /// <inheritdoc />
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => inner.GetRegistryField(registryEntryId, fieldCode);

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => inner.Convert(value, fromUnitCode, toUnitCode);
}
