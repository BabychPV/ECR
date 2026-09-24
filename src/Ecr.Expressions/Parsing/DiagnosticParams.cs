// src/Ecr.Expressions/Parsing/DiagnosticParams.cs

namespace Ecr.Expressions.Parsing;

/// <summary>
/// Підстановки для <see cref="ExpressionDiagnostic.MessageParams"/> — спільні
/// для парсера і зв'язувача (<c>TypeChecker</c>, <c>UnitChecker</c>,
/// <c>ReferenceResolver</c> і сусіди).
/// </summary>
/// <remarks>
/// ⛔ V-20 (третій раунд UX, 2026-09-24): діагностики зв'язувача несли готове
/// УКРАЇНСЬКЕ речення без ключа, і редактор виразів показував його як є за
/// будь-якої мови інтерфейсу. Тепер кожна несе ключ <c>expr.*</c> каталогу
/// (<c>09-seed.sql</c>), англійське речення — лише запасне.
/// </remarks>
public static class DiagnosticParams
{
    /// <summary>Словник підстановок із пар «ім'я → значення».</summary>
    /// <param name="pairs">Пари.</param>
    public static IReadOnlyDictionary<string, string> Of(params (string Name, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var result = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach (var (name, value) in pairs)
        {
            result[name] = value;
        }

        return result;
    }
}
