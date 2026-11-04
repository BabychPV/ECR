// src/Ecr.Application/Calculations/MethodologyDerivedArguments.cs
using Ecr.Expressions.Binding;

namespace Ecr.Application.Calculations;

/// <summary>
/// Похідні аргументи (2026-11-02) для ОДНІЄЇ версії методології: переносить старі маркери <c>Name?</c> зі
/// списків <c>ArgumentsCsv</c> у вирази і виводить список із виразу.
/// </summary>
/// <remarks>
/// ⛔ Необов'язковість у рантаймі — ВЛАСТИВІСТЬ ВЕРСІЇ (<c>OptionalArguments.Of</c> об'єднує маркери всіх формул
/// версії). Тому маркер ставиться на кожне вживання імені в КОЖНІЙ формулі версії, а не лише там, де його
/// написали в списку: так похідні списки дають рівно ту саму множину, і числа опублікованих версій не
/// змінюються. Маркер імені, якого не вживає жодна формула версії, зникає — і це записується у звіт.
/// <para>
/// ⚠ Оголошені, але не вжиті НЕобов'язкові елементи списку (на Land — 7 формул) рантайм не читав ніколи
/// (лише попередження публікації); вони зникають і теж ідуть у звіт поформульно.
/// </para>
/// <para>
/// ⚠ Межа: для БІБЛІОТЕКИ рантайм об'єднує маркери лише потрібних формул замикання. Маркер у списку
/// бібліотечної формули, яка сама ім'я не вживає, для викликача зникне. На Land маркерів у бібліотеці
/// (<c>Common</c>) немає; такий випадок звіт показує як <see cref="VersionResult.DroppedOptional"/>.
/// </para>
/// </remarks>
public static class MethodologyDerivedArguments
{
    /// <summary>Формула версії до нормалізації.</summary>
    /// <param name="Id">Ідентифікатор (0 — пакет, ще не в базі).</param>
    /// <param name="Code">Код.</param>
    /// <param name="Expression">Вираз як є.</param>
    /// <param name="ArgumentsCsv">Збережений або поданий список (старий, введений руками).</param>
    public sealed record Input(int Id, string Code, string Expression, string? ArgumentsCsv);

    /// <summary>Формула після нормалізації.</summary>
    /// <param name="Id">Ідентифікатор.</param>
    /// <param name="Code">Код.</param>
    /// <param name="Expression">Вираз із маркерами.</param>
    /// <param name="ArgumentsCsv">Похідний список.</param>
    /// <param name="MarkersAdded">Скільки маркерів <c>?</c> поставлено у вираз.</param>
    /// <param name="DroppedDeclared">Елементи старого списку, яких немає у виразі (зникли).</param>
    /// <param name="ArgumentsChanged">Чи відрізняється похідний список від збереженого (хоч би форматом).</param>
    public sealed record Output(
        int Id,
        string Code,
        string Expression,
        string ArgumentsCsv,
        int MarkersAdded,
        IReadOnlyList<string> DroppedDeclared,
        bool ArgumentsChanged)
    {
        /// <summary>Чи змінився вираз.</summary>
        public bool ExpressionChanged => MarkersAdded > 0;
    }

    /// <summary>Результат версії.</summary>
    /// <param name="Formulas">Формули в порядку входу.</param>
    /// <param name="DroppedOptional">Необов'язкові імена версії, яких не вживає жодна її формула (маркер зник).</param>
    public sealed record VersionResult(IReadOnlyList<Output> Formulas, IReadOnlyList<string> DroppedOptional);

    /// <summary>Чи збережений список уже похідний від виразу (версії з такими формулами нормалізувати нема чого).</summary>
    /// <param name="expression">Вираз.</param>
    /// <param name="argumentsCsv">Збережений список.</param>
    /// <returns><c>true</c> — збігається з похідним.</returns>
    public static bool IsDerived(string expression, string? argumentsCsv)
        => string.Equals(argumentsCsv, ArgumentDeclarationChecker.DeriveCsv(expression), StringComparison.Ordinal);

    /// <summary>Нормалізує формули однієї версії.</summary>
    /// <param name="formulas">Усі формули версії.</param>
    /// <returns>Нормалізовані формули й звіт.</returns>
    public static VersionResult Normalize(IReadOnlyList<Input> formulas)
    {
        ArgumentNullException.ThrowIfNull(formulas);

        // Множина версії: маркери старих списків ∪ маркери, що вже стоять у виразах.
        var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var formula in formulas)
        {
            optional.UnionWith(ArgumentDeclarationChecker.OptionalNamesFromCsv(formula.ArgumentsCsv));
            optional.UnionWith(ArgumentDeclarationChecker.Derive(formula.Expression)
                .Where(a => a is { Kind: DeclaredArgumentKind.Argument, IsOptional: true })
                .Select(a => ArgumentDeclarationChecker.NormalizeName(a.Name)));
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<Output>(formulas.Count);

        foreach (var formula in formulas)
        {
            var expression = ArgumentDeclarationChecker.ApplyOptionalMarkers(formula.Expression, optional);
            var derived = ArgumentDeclarationChecker.Derive(expression);

            used.UnionWith(derived
                .Where(a => a.Kind == DeclaredArgumentKind.Argument)
                .Select(a => ArgumentDeclarationChecker.NormalizeName(a.Name)));

            var keys = derived.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = ArgumentDeclarationChecker.FromCsv(formula.ArgumentsCsv)
                .Where(a => !keys.Contains(a.Key))
                .Select(a => a.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var csv = ArgumentDeclarationChecker.Format(derived);

            outputs.Add(new Output(
                formula.Id,
                formula.Code,
                expression,
                csv,
                expression.Length - formula.Expression.Length,
                dropped,
                !string.Equals(csv, formula.ArgumentsCsv, StringComparison.Ordinal)));
        }

        return new VersionResult(
            outputs,
            [.. optional.Where(name => !used.Contains(name)).Order(StringComparer.OrdinalIgnoreCase)]);
    }
}
