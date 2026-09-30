// src/Ecr.Application/Calculations/MethodologyUnitChecks.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Calculations;

/// <summary>
/// Перевірка одиниці, яку конфігуратор методології ставить на константу,
/// вихід або формулу (B-01, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Доти неіснуючий <c>unitId</c> доходив до бази і падав на
/// <c>FK_MC_Unit</c> / <c>FK_MO_Unit</c> / <c>FK_MF_Unit</c> — 500
/// «зверніться до адміністратора» на описку в номері. Перевіряється ДО зміни
/// сутності знімком довідника (<see cref="IUnitCatalog"/> кешований, один
/// запит на всі одиниці), а не походом по одиниці.
/// </remarks>
public static class MethodologyUnitChecks
{
    /// <summary>Відмовляє, якщо одиниці з таким ідентифікатором немає.</summary>
    /// <param name="units">Довідник одиниць.</param>
    /// <param name="unitId">Одиниця з запиту; <c>null</c> — нічого перевіряти.</param>
    /// <param name="code">Код константи, виходу чи формули — для повідомлення.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c>, ключ <c>unknownUnit</c>.</exception>
    public static async Task RequireKnownAsync(
        IUnitCatalog units, int? unitId, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(units);

        if (unitId is not { } id)
        {
            return;
        }

        var catalogue = await units.GetAsync(ct).ConfigureAwait(false);
        if (catalogue.Units.Values.Any(u => u.Id == id))
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-CALC-0422",
            $"«{code}»: одиниці {id.ToString(CultureInfo.InvariantCulture)} у довіднику немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.unknownUnit",
                ["code"] = code,
                ["unitId"] = id.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// Зауваження <see cref="UnitChecker"/>, які відхиляють публікацію
    /// методології (ФВ-16.6, ФВ-16.7).
    /// </summary>
    /// <remarks>
    /// ⚠ Не всі. Відхиляє поєднання ВІДОМИХ різних одиниць без <c>CONVERT</c> і
    /// хибний <c>CONVERT</c>. Не відхиляють: добуток двох розмірних величин і
    /// ділення без похідної одиниці в довіднику (<c>productUndeclared</c>,
    /// <c>inverseUndeclared</c>, <c>derivedMissing</c>) — <c>CST.EF * !M</c>
    /// (кг/т × т) і є звичайною формою методології, а похідних для множення
    /// довідник не має взагалі; <c>convertSourceForm</c> — вихідна одиниця
    /// <c>CONVERT(@x, @xUnit, 'kg')</c> в методології є аргументом, а не
    /// колонкою, і рантайм звіряє її сам. Результат таких вузлів — «невідома
    /// одиниця», тобто далі він не перевіряється, а не вважається помилкою.
    /// </remarks>
    public static IReadOnlySet<string> BlockingKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "expr.unit.addNeedsConvert",
        "expr.unit.compareNeedsConvert",
        "expr.unit.branchesDiffer",
        "expr.unit.aggregateNeedsConvert",
        "expr.unit.dimensionMismatch",
        "expr.unit.convertDimensions",
        "expr.unit.convertArity",
        "expr.unit.convertTargetLiteral",
        "expr.unit.unknownUnit",
    };

    /// <summary>
    /// Перевіряє сумісність одиниць у формулах версії методології; відмовляє
    /// переліком усіх формул із несумісними одиницями.
    /// </summary>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <param name="context">Одиниці констант і формул версії.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-TMPL-4223</c> (ФВ-16.7) — величини в різних одиницях поєднані без
    /// явного <c>CONVERT</c>, або <c>CONVERT</c> неможливий.
    /// </exception>
    /// <remarks>
    /// ⛔ Доти <see cref="UnitChecker"/> стояв лише на публікації шаблону, а
    /// методологія з <c>CST.A + CST.B</c> (т + кг) публікувалася і рахувала
    /// число, помножене на тисячу, — рівно те, що ФВ-16.7 велить ловити до
    /// продуктиву, а не на звірці.
    /// ⚠ Ключ відмови — ключ першого зауваження (<c>expr.unit.*</c>, уже
    /// перекладені), формула — полем <c>formula</c>; повний перелік — у
    /// <c>diagnostics</c> з кодом формули в кожному.
    /// </remarks>
    public static void RequireCompatibleUnits(IReadOnlyList<ParsedFormula> formulas, IUnitContext context)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(context);

        var found = new List<(string Formula, ExpressionDiagnostic Diagnostic)>();
        var checker = new UnitChecker();

        foreach (var formula in formulas.Where(f => f.Root is not null))
        {
            var diagnostics = new List<ExpressionDiagnostic>();
            checker.Check(formula.Root!, context, diagnostics);

            found.AddRange(diagnostics
                .Where(d => d.MessageKey is not null && BlockingKeys.Contains(d.MessageKey))
                .Select(d => (formula.Code, d)));
        }

        if (found.Count == 0)
        {
            return;
        }

        var (first, diagnostic) = found[0];
        var names = string.Join(", ", found.Select(f => f.Formula).Distinct(StringComparer.OrdinalIgnoreCase));

        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in diagnostic.MessageParams ?? new Dictionary<string, string>())
        {
            details[name] = value;
        }

        details["messageKey"] = diagnostic.MessageKey;
        details["formula"] = first;
        details["formulas"] = names;
        details["count"] = found.Count.ToString(CultureInfo.InvariantCulture);
        details["diagnostics"] = found
            .Select(f => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["formula"] = f.Formula,
                ["code"] = f.Diagnostic.Code,
                ["message"] = f.Diagnostic.Message,
                ["position"] = f.Diagnostic.Position,
                ["messageKey"] = f.Diagnostic.MessageKey,
                ["messageParams"] = f.Diagnostic.MessageParams,
            })
            .ToList();

        throw new BusinessRuleException(
            ErrorCodes.UnitMismatch,
            $"Несумісні одиниці без явного CONVERT у формулах: {names}. "
            + string.Join(" | ", found.Select(f => $"«{f.Formula}»: {f.Diagnostic.Message}")),
            details);
    }
}
