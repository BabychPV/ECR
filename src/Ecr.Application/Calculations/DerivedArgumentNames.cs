// src/Ecr.Application/Calculations/DerivedArgumentNames.cs
namespace Ecr.Application.Calculations;

/// <summary>
/// Імена «похідних» аргументів методології: їх підставляє адаптер події під час підготовки входу
/// обчислення, а не колонка таблиці (AN-5, <c>FlareEventArguments</c>).
/// </summary>
/// <remarks>
/// ⛔ Єдине джерело імен для двох місць: збирач входу (<c>CalculationInputBuilder</c> підставляє) і
/// структурна перевірка публікації (<c>ECR-CALC-0438</c> не вимагає для них колонку). Розбіжність
/// між ними дала б або мовчазний <c>null</c>, або хибну відмову публікації.
/// </remarks>
public static class DerivedArgumentNames
{
    /// <summary>Об'єм події.</summary>
    public const string Total = "Total";

    /// <summary>Тривалість, секунд.</summary>
    public const string Duration = "Duration";

    /// <summary>Режим факела: HP → 1, LP → 2, інше → 0.</summary>
    public const string FlareUnitMode = "FlareUnitMode";

    /// <summary>Ознака пілота: 1 або 0.</summary>
    public const string IsPilot = "IsPilot";

    /// <summary>Категорія події (назва факела).</summary>
    public const string Category = "Category";

    /// <summary>Усі похідні імена.</summary>
    public static IReadOnlyList<string> All { get; } = [Total, Duration, FlareUnitMode, IsPilot, Category];
}
