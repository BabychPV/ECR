using Ecr.Expressions.Ast;

namespace Ecr.Scripting.Syntax;

/// <summary>Вираз синтаксичного дерева.</summary>
/// <param name="Span">Позиція всього виразу.</param>
public abstract record Expr(ScriptSpan Span);

/// <summary>Число.</summary>
public sealed record NumberExpr(decimal Value, ScriptSpan Span) : Expr(Span);

/// <summary>Рядок.</summary>
public sealed record StringExpr(string Value, ScriptSpan Span) : Expr(Span);

/// <summary>true/false.</summary>
public sealed record BoolExpr(bool Value, ScriptSpan Span) : Expr(Span);

/// <summary>null.</summary>
public sealed record NullExpr(ScriptSpan Span) : Expr(Span);

/// <summary>Ім'я.</summary>
public sealed record NameExpr(string Name, ScriptSpan Span) : Expr(Span);

/// <summary><c>row.X</c>.</summary>
public sealed record RowExpr(string Field, bool IsOptional, ScriptSpan Span) : Expr(Span);

/// <summary><c>key(row.X)</c>.</summary>
public sealed record KeyExpr(string Field, bool IsOptional, ScriptSpan Span) : Expr(Span);

/// <summary><c>period.P</c>.</summary>
public sealed record PeriodExpr(string Property, ScriptSpan Span) : Expr(Span);

/// <summary><c>header.N</c>.</summary>
public sealed record HeaderExpr(string Name, ScriptSpan Span) : Expr(Span);

/// <summary><c>entry("R","C")</c>.</summary>
public sealed record EntryExpr(string Registry, string Code, ScriptSpan Span) : Expr(Span);

/// <summary>Виклик функції.</summary>
public sealed record CallExpr(string Name, IReadOnlyList<Expr> Args, ScriptSpan NameSpan, ScriptSpan Span) : Expr(Span);

/// <summary>Виклик функції бібліотеки <c>Lib.f(...)</c>.</summary>
public sealed record LibCallExpr(string Library, string Name, IReadOnlyList<Expr> Args, ScriptSpan NameSpan, ScriptSpan Span) : Expr(Span);

/// <summary>Агрегат (§13.1): <c>Sum(expr where cond)</c> або <c>Count(Alias where cond)</c>.</summary>
public sealed record AggregateExpr(AggregateKind Kind, Expr Arg, Expr? Where, ScriptSpan NameSpan, ScriptSpan Span) : Expr(Span);

/// <summary><c>x.Name</c>.</summary>
public sealed record MemberExpr(Expr Target, string Name, ScriptSpan NameSpan, ScriptSpan Span) : Expr(Span);

/// <summary><c>x[k1, k2]</c>.</summary>
public sealed record IndexExpr(Expr Target, IReadOnlyList<Expr> Keys, ScriptSpan Span) : Expr(Span);

/// <summary>Унарна операція.</summary>
public sealed record UnaryExpr(UnaryOperator Op, Expr Operand, ScriptSpan Span) : Expr(Span);

/// <summary>Бінарна операція.</summary>
public sealed record BinaryExpr(BinaryOperator Op, Expr Left, Expr Right, ScriptSpan Span) : Expr(Span);

/// <summary><c>c ? a : b</c>.</summary>
public sealed record TernaryExpr(Expr Cond, Expr WhenTrue, Expr WhenFalse, ScriptSpan Span) : Expr(Span);

/// <summary><c>x in (a, b)</c>.</summary>
public sealed record InExpr(Expr Value, IReadOnlyList<Expr> Items, ScriptSpan Span) : Expr(Span);

/// <summary>Згадка одиниці.</summary>
public sealed record UnitRef(string Code, ScriptSpan Span);

/// <summary>Значення константи в синтаксичному дереві.</summary>
public abstract record ConstValueSyntax(ScriptSpan Span);

/// <summary>Скалярна константа.</summary>
public sealed record ConstScalarSyntax(object Value, bool IsNumber, ScriptSpan Span) : ConstValueSyntax(Span);

/// <summary>Мапа.</summary>
public sealed record ConstMapSyntax(IReadOnlyList<ConstEntrySyntax> Entries, ScriptSpan Span) : ConstValueSyntax(Span);

/// <summary>Елемент мапи.</summary>
public sealed record ConstEntrySyntax(string Key, ScriptSpan KeySpan, ConstValueSyntax Value);

/// <summary>Елемент верхнього рівня або оператор.</summary>
public abstract record ItemSyntax(ScriptSpan Span);

/// <summary><c>import X;</c>.</summary>
public sealed record ImportSyntax(string Name, ScriptSpan Span) : ItemSyntax(Span);

/// <summary><c>rows from …;</c>.</summary>
public sealed record RowsSyntax(IReadOnlyList<TableRef> Tables, MatchNode? Where, MatchNode? Active, ScriptSpan Span) : ItemSyntax(Span);

/// <summary>
/// <c>registry as of &lt;вираз&gt;;</c> (DESIGN-sg-flaring §5.2a): усі читання довідників рядка — на дату цього виразу.
/// </summary>
/// <param name="Date">Вираз дати; обчислюється на КОЖЕН рядок.</param>
/// <param name="Span">Позиція всього речення.</param>
public sealed record RegistryAsOfSyntax(Expr Date, ScriptSpan Span) : ItemSyntax(Span);

/// <summary><c>table Alias = T;</c>.</summary>
public sealed record TableSyntax(string Alias, ScriptSpan AliasSpan, TableRef Table, ScriptSpan Span) : ItemSyntax(Span);

/// <summary><c>param X;</c> (лише бібліотека): ім'я змінної імпортера.</summary>
public sealed record ParamDeclSyntax(string Name, ScriptSpan NameSpan, ScriptSpan Span) : ItemSyntax(Span);

/// <summary><c>const</c>.</summary>
public sealed record ConstSyntax(string Name, ScriptSpan NameSpan, UnitRef? Unit, ConstValueSyntax Value, ScriptSpan Span) : ItemSyntax(Span);

/// <summary>Параметр функції.</summary>
public sealed record ParamSyntax(string Name, ScriptSpan Span);

/// <summary><c>function</c>.</summary>
public sealed record FunctionSyntax(string Name, ScriptSpan NameSpan, IReadOnlyList<ParamSyntax> Params, Expr Body, ScriptSpan Span) : ItemSyntax(Span);

/// <summary>Оператор.</summary>
public abstract record StatementSyntax(ScriptSpan Span) : ItemSyntax(Span);

/// <summary><c>var</c>.</summary>
public sealed record VarSyntax(bool IsVisible, string Name, ScriptSpan NameSpan, UnitRef? Unit, Expr Init, ScriptSpan Span) : StatementSyntax(Span);

/// <summary><c>output</c>.</summary>
public sealed record OutputSyntax(string Code, ScriptSpan CodeSpan, string? PerVar, ScriptSpan PerSpan, UnitRef Unit, Expr Init, ScriptSpan Span) : StatementSyntax(Span);

/// <summary><c>reject when</c>.</summary>
public sealed record RejectSyntax(Expr Cond, ScriptSpan Span) : StatementSyntax(Span);

/// <summary><c>skip when</c>.</summary>
public sealed record SkipSyntax(Expr Cond, ScriptSpan Span) : StatementSyntax(Span);

/// <summary>Блок.</summary>
public sealed record BlockSyntax(IReadOnlyList<StatementSyntax> Statements, ScriptSpan Span);

/// <summary><c>if</c>.</summary>
public sealed record IfSyntax(Expr Cond, BlockSyntax Then, BlockSyntax? Else, ScriptSpan Span) : StatementSyntax(Span);

/// <summary>Код речовини в <c>foreach substance</c>.</summary>
public sealed record SubstanceCodeSyntax(string Code, ScriptSpan Span);

/// <summary><c>foreach substance S in (...)</c>.</summary>
public sealed record ForeachSubstanceSyntax(string VarName, ScriptSpan VarSpan, IReadOnlyList<SubstanceCodeSyntax> Codes, BlockSyntax Body, ScriptSpan Span) : StatementSyntax(Span);

/// <summary>Програма: імпорти й елементи в порядку тексту.</summary>
/// <param name="Imports">Імпорти.</param>
/// <param name="Items">Решта елементів.</param>
public sealed record ProgramSyntax(IReadOnlyList<ImportSyntax> Imports, IReadOnlyList<ItemSyntax> Items);
