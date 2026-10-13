// src/Ecr.Calculations/OptionalArguments.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;

namespace Ecr.Calculations;

/// <summary>
/// D-OA: необов'язкові аргументи версії — маркер <c>Name?</c> у <c>ArgumentsCsv</c> будь-якої формули версії
/// чи бібліотеки замикання. Та сама правда, що й у перевірці публікації
/// (<c>MethodologyPublishChecks.OptionalArguments</c>): імена нормалізує
/// <see cref="ArgumentDeclarationChecker"/>, регістр ігнорується.
/// </summary>
/// <remarks>
/// ⚠ Рантайм НЕ змінює <c>MethodologyEvaluationContext.GetArgument</c>: аргумент без комірки лишається
/// <c>#ARG</c> (другий рубіж 0438). Необов'язковому аргументу просто додається ключ зі значенням
/// <c>null</c> — рівно те, що вже дає наявна порожня комірка.
/// </remarks>
internal static class OptionalArguments
{
    /// <summary>Об'єднання маркерів формул версії та бібліотек замикання.</summary>
    /// <param name="formulas">Формули версії.</param>
    /// <param name="libraries">Замикання бібліотек (може бути <c>null</c>).</param>
    /// <returns>Нормалізовані імена без урахування регістру; порожня множина — маркерів немає.</returns>
    public static IReadOnlySet<string> Of(
        IReadOnlyList<MethodologyFormula> formulas, CalculationLibraries? libraries)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var formula in formulas)
        {
            Collect(formula, result);
        }

        if (libraries is not null)
        {
            foreach (var version in libraries.Versions)
            {
                foreach (var formula in version.Formulas)
                {
                    Collect(formula, result);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Додає в словник аргументів <c>null</c> для кожного необов'язкового імені, якого там немає.
    /// Наявна комірка (навіть порожня) не чіпається.
    /// </summary>
    /// <param name="arguments">Словник аргументів рядка.</param>
    /// <param name="optional">Множина з <see cref="Of"/>.</param>
    public static void Fill(IDictionary<string, ExpressionValue> arguments, IReadOnlySet<string> optional)
    {
        foreach (var name in optional)
        {
            arguments.TryAdd(name, ExpressionValue.Null);
        }
    }

    private static void Collect(MethodologyFormula formula, HashSet<string> result)
    {
        if (formula.ArgumentsCsv is { Length: > 0 } csv && csv.Contains(ArgumentDeclarationChecker.OptionalMarker))
        {
            result.UnionWith(ArgumentDeclarationChecker.OptionalNamesFromCsv(csv));
        }
    }
}
