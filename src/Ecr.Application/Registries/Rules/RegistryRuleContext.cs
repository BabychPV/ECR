// src/Ecr.Application/Registries/Rules/RegistryRuleContext.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Registries.Rules;

/// <summary>
/// Контекст обчислення правила довідника: дані — лише зі знімка довідників, документа немає
/// (FEATURE-REGISTRY-TABLES §6, «Діалект» і «Область»).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Правило перевіряє ДАНІ довідника, а не рахує документ (той самий принцип, що
/// <c>ValidationEvaluationContext</c>): комірок, аргументів, констант, формул і шапки тут немає —
/// кожне таке звернення дає <c>#REF</c>. Публікація опису такі посилання вже відхиляє
/// (<see cref="RegistryRuleCompiler"/>), тож сюди вони доходять лише зі старих, ще не
/// перезбережених правил — і дають порушення з кодом помилки, а не тиху «істину».
/// </para>
/// <para>
/// ⚠ <c>THIS</c> і <c>ROW.</c> верхнього рівня розв'язуються НЕ контекстом, а переписуванням дерева
/// (<see cref="Bind"/>): область рядка обчислювача (<c>RegistryRowScope</c>) внутрішня для
/// <c>Ecr.Expressions</c>, а обчислювач на <c>ThisNode</c> дає <c>#VALUE</c>. Переписане дерево
/// складене з тих самих вузлів, що пише людина: <c>THIS</c> → id запису (так він і живе в рантаймі,
/// §5.3), <c>ROW.a.b</c> → <c>REGFIELD(id, 'a.b')</c> — один шлях читання поля для правил і формул.
/// </para>
/// </remarks>
/// <param name="snapshot">Знімок довідників, завантажений ПІСЛЯ запису в тій самій транзакції.</param>
/// <param name="date">Бізнес-дата знімка.</param>
internal sealed class RegistryRuleContext(IRegistrySnapshot snapshot, DateOnly date) : IEvaluationContext
{
    /// <inheritdoc />
    public IRegistrySnapshot? Registries => snapshot;

    /// <inheritdoc />
    public PeriodContext Period { get; } = new(date, date, CalendarMode.Actual, date.Year, 0);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        => [ExpressionValue.Error(ExpressionErrors.BadReference)];

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
        => [ExpressionValue.Error(ExpressionErrors.BadReference)];

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => snapshot.GetField(registryEntryId, fieldCode);

    /// <inheritdoc />
    /// <remarks>Каталогу одиниць у правилі немає: <c>CONVERT</c> дає <c>#UNIT</c>, а не вигадане число.</remarks>
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => ExpressionValue.Error(ExpressionErrors.BadUnit);

    /// <summary>
    /// Дерево правила для одного запису: <c>THIS</c> → id запису, <c>ROW.a.b</c> верхнього рівня →
    /// <c>REGFIELD(id, 'a.b')</c>. <c>ROW.</c> усередині агрегату чи <c>REGONE</c> належить ЇХНІЙ
    /// області (§5.2) і не чіпається.
    /// </summary>
    /// <param name="root">Розібране дерево (з кешу парсера — не змінюється, будується нове).</param>
    /// <param name="entryId">Запис, що перевіряється.</param>
    public static AstNode Bind(AstNode root, long entryId) => BindNode(root, entryId, rowScopeDepth: 0);

    private static AstNode BindNode(AstNode node, long entryId, int rowScopeDepth)
    {
        switch (node)
        {
            case ThisNode:
                return EntryLiteral(entryId, node.Position);

            case RowFieldNode row when rowScopeDepth == 0:
                return new FunctionNode(
                    "REGFIELD",
                    [EntryLiteral(entryId, row.Position), new LiteralNode(string.Join('.', row.Path), ExpressionValueType.Text)])
                {
                    Position = row.Position,
                };

            case UnaryNode unary:
                return unary with { Operand = BindNode(unary.Operand, entryId, rowScopeDepth) };

            case BinaryNode binary:
                return binary with
                {
                    Left = BindNode(binary.Left, entryId, rowScopeDepth),
                    Right = BindNode(binary.Right, entryId, rowScopeDepth),
                };

            case ConditionalNode conditional:
                return conditional with
                {
                    Condition = BindNode(conditional.Condition, entryId, rowScopeDepth),
                    WhenTrue = BindNode(conditional.WhenTrue, entryId, rowScopeDepth),
                    WhenFalse = BindNode(conditional.WhenFalse, entryId, rowScopeDepth),
                };

            case FunctionNode function:
                var opensRow = RegistryForms.RowScopeNames.Contains(function.Name);
                var arguments = new AstNode[function.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = BindNode(function.Arguments[i], entryId, opensRow && i > 0 ? rowScopeDepth + 1 : rowScopeDepth);
                }

                return function with { Arguments = arguments };

            default:
                return node;
        }
    }

    private static LiteralNode EntryLiteral(long entryId, int position)
        => new((decimal)entryId, ExpressionValueType.Number) { Position = position };
}
