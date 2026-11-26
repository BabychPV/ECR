// src/Ecr.Scripting/ScriptRegistryScopes.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;

namespace Ecr.Scripting;

/// <summary>
/// MS × B179: обсяги читання довідників за періодом скомпільованим скриптом — для ребер графа залежностей
/// (<see cref="PeriodScope"/>), за тим самим правилом, що й для формул (<see cref="RegistryReadScopes"/>).
/// </summary>
/// <remarks>
/// <para>
/// Скрипт — одна одиниця читання довідників (<c>cfg.RegistryUse</c> з кодом «script»), тож обсяг довідника — об'єднання
/// (<see cref="PeriodScope.Join"/>) обсягів усіх його викликів <c>REG*</c> у виразах скрипта: операторах верхнього рівня
/// (включно з гілками <c>if</c> і тілами <c>foreach substance</c>), тілах власних функцій і агрегатах.
/// </para>
/// <para>
/// ⛔ Невпевнено — <see cref="PeriodScope.All"/> (зайвий перерахунок дешевший за пропущений). «Усе» дають:
/// довідник, згаданий не лише викликами <c>REG*</c> (<c>entry("R","K")</c> у виразі чи в умові <c>rows</c>); скрипт з
/// <c>import</c> (виклики бібліотек читають довідники, чиїх виразів тут немає); довідник без жодного впізнаного
/// виклику (код довідника є в <see cref="CompiledScript.RegistryCodes"/>, а вузла-виклику в AST немає).
/// </para>
/// </remarks>
public static class ScriptRegistryScopes
{
    /// <summary>Обсяг кожного довідника скрипта.</summary>
    /// <param name="script">Скомпільований скрипт.</param>
    /// <returns>Код довідника (без регістру) → обсяг; містить рівно <see cref="CompiledScript.RegistryCodes"/>.</returns>
    public static IReadOnlyDictionary<string, PeriodScope> Collect(CompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var result = new Dictionary<string, PeriodScope>(StringComparer.OrdinalIgnoreCase);

        // ⛔ Новий вид оператора, якого обхід не знає, міг би сховати виклик REG* і звузити обсяг (пропущений
        // перерахунок) — тоді «усе» для всіх довідників.
        if (script.Imports.Count > 0 || !AllStatements(script.Statements).All(IsKnown))
        {
            foreach (var code in script.RegistryCodes)
            {
                result[code] = PeriodScope.All;
            }

            return result;
        }

        var merged = new Dictionary<string, PeriodScope>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots(script))
        {
            foreach (var (code, scope) in RegistryReadScopes.Collect(root, ExpressionDialect.Methodology))
            {
                merged[code] = merged.TryGetValue(code, out var known) ? known.Join(scope) : scope;
            }
        }

        var viaEntry = new HashSet<string>(script.RegistryEntries.Select(e => e.RegistryCode), StringComparer.OrdinalIgnoreCase);
        foreach (var code in script.RegistryCodes)
        {
            var scope = !viaEntry.Contains(code) && merged.TryGetValue(code, out var known) ? known : PeriodScope.All;

            // ✎ D1 (REVIEW-sg-flaring): під `registry as of` рядок читає версію на ВЛАСНУ дату, будь-яку в межах періоду
            // документа, а не на кінець періоду, — тож обсяг не вужчий за рік (місяць → рік; «усе» лишається «усе»).
            result[code] = script.RegistryAsOf is null ? scope : scope.Join(PeriodScope.Year);
        }

        return result;
    }

    /// <summary>Корені виразів скрипта (речення registry as of, оператори, функції, агрегати) — для обходів перевірок.</summary>
    /// <param name="script">Скрипт.</param>
    public static IEnumerable<AstNode> Roots(CompiledScript script)
    {
        if (script.RegistryAsOf is { } asOf)
        {
            yield return asOf;
        }

        foreach (var statement in script.Statements)
        {
            foreach (var root in StatementRoots(statement))
            {
                yield return root;
            }
        }

        foreach (var function in script.Functions.Values)
        {
            yield return function.Body;
        }

        foreach (var aggregate in script.Aggregates)
        {
            if (aggregate.Value is { } value)
            {
                yield return value;
            }

            if (aggregate.Where is { } where)
            {
                yield return where;
            }
        }
    }

    private static IEnumerable<AstNode> StatementRoots(ScriptStatement statement)
    {
        switch (statement)
        {
            case VarStatement v:
                yield return v.Expr;
                break;
            case OutputStatement o:
                yield return o.Expr;
                break;
            case RejectStatement r:
                yield return r.Cond;
                break;
            case SkipStatement s:
                yield return s.Cond;
                break;
            case IfStatement i:
                yield return i.Cond;
                foreach (var root in BlockRoots(i.Then).Concat(i.Else is null ? [] : BlockRoots(i.Else)))
                {
                    yield return root;
                }

                break;
            case ForeachSubstanceStatement f:
                foreach (var root in BlockRoots(f.Body))
                {
                    yield return root;
                }

                break;
            default:
                break;
        }
    }

    private static IEnumerable<AstNode> BlockRoots(ScriptBlock block) => block.Statements.SelectMany(StatementRoots);

    /// <summary>Оператори, чиї вирази обходить <see cref="StatementRoots"/>.</summary>
    internal static bool IsKnown(ScriptStatement statement) =>
        statement is VarStatement or OutputStatement or RejectStatement or SkipStatement or IfStatement or ForeachSubstanceStatement;

    private static IEnumerable<ScriptStatement> AllStatements(IEnumerable<ScriptStatement> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            IEnumerable<ScriptStatement> nested = statement switch
            {
                IfStatement i => i.Then.Statements.Concat(i.Else?.Statements ?? []),
                ForeachSubstanceStatement f => f.Body.Statements,
                _ => [],
            };
            foreach (var inner in AllStatements(nested))
            {
                yield return inner;
            }
        }
    }
}
