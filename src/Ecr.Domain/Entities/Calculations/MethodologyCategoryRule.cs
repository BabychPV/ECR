// src/Ecr.Domain/Entities/Calculations/MethodologyCategoryRule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Правило категорії константи версії методології (L-2, B13 §4.1, <c>calc.CategoryRule</c>):
/// ОДИН вираз на версію, що з рядка документа дає ключ категорії.
/// </summary>
/// <remarks>
/// ⛔ Не плутати з <see cref="MethodologyRule"/>: те — предикат ВІДБОРУ рядків документа, це — вибір
/// ВАРІАНТА константи (<c>Loc_BeforeMR_A</c>, <c>Diesel</c>, <c>Summer</c>) для рядка, який уже
/// відібрано. У AF такого правила немає в даних — категорію ставив C#-клас методології; тут воно
/// стає даними (вираз діалекту Methodology: <c>!ECW_Location</c>, <c>if(@Land_TypeFuel = …)</c>).
///
/// ⚠ Одне правило на версію (<c>UQ_CategoryRule_Version</c>): ТЗ не знає правил по кроках чи по
/// групах констант. Якщо замовник захоче — окрема міграція.
///
/// ⚠ Правило обчислюється раз на рядок, ПІСЛЯ Row-формул і ДО циклу речовин, тож Row-фаза
/// категорії не має (<c>category = null</c>).
/// </remarks>
public sealed class MethodologyCategoryRule : Entity<int>
{
    private MethodologyCategoryRule() { }

    /// <summary>Створює правило.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="expression">Вираз діалекту Methodology; результат — текст (ключ категорії).</param>
    /// <param name="utcNow">Момент створення з <c>IClock.UtcNow</c>.</param>
    /// <exception cref="DomainException">Порожній вираз — <c>ECR-CALC-0422</c>.</exception>
    public MethodologyCategoryRule(int methodologyVersionId, string expression, DateTime utcNow)
    {
        MethodologyVersionId = methodologyVersionId;
        Expression = Normalize(expression);
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>Версія методології, якій належить правило.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>Вираз, що з рядка документа дає ключ категорії.</summary>
    public string Expression { get; private set; } = null!;

    /// <summary>Коли правило створено (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Коли правило востаннє змінювали (UTC).</summary>
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Переписує вираз наявного правила.</summary>
    /// <param name="expression">Новий вираз.</param>
    /// <param name="utcNow">Момент правки з <c>IClock.UtcNow</c>.</param>
    /// <exception cref="DomainException">Порожній вираз — <c>ECR-CALC-0422</c>.</exception>
    /// <remarks>
    /// ⛔ Метод <c>internal</c>: єдиний вхід — <see cref="MethodologyVersion.SetCategoryRule"/>, бо
    /// правило не знає, опублікована його версія чи ні.
    /// </remarks>
    internal void Update(string expression, DateTime utcNow)
    {
        Expression = Normalize(expression);
        UpdatedAt = utcNow;
    }

    /// <summary>Вираз без крайніх пробілів; порожній не приймається.</summary>
    private static string Normalize(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new DomainException(
                "ECR-CALC-0422",
                "Правило категорії без виразу: порожнє правило не дає жодного ключа, а «правила немає» "
                + "записується видаленням правила.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.categoryRuleEmpty" });
        }

        return expression.Trim();
    }
}
