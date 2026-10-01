// src/Ecr.Application/Integration/SourceEvents/SourceEventTemplateOrder.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>
/// Порядок братніх шаблонів подій (HSE301, рішення людини 2026-10-01): <c>Auto</c>, <c>Auto_Day</c>, <c>Manual</c>,
/// <c>Manual_Day</c> — це ті самі події з тими самими атрибутами, а легасі об'єднує їх <c>UNION ALL</c>
/// без дедуплікації (унікальність лише за ID). Подія з одним ID у кількох шаблонах лягає в рядок ОДИН раз:
/// виграє шаблон із меншим рангом.
/// </summary>
public static class SourceEventTemplateOrder
{
    /// <summary>Ранг шаблону за суфіксом: <c>_Auto</c> 0, <c>_Auto_Day</c> 1, <c>_Manual</c> 2, <c>_Manual_Day</c> 3, решта 4.</summary>
    /// <param name="template">Ім'я шаблону.</param>
    public static int Rank(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return 4;
        }

        var name = template.Trim();
        if (name.EndsWith("_Auto_Day", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.EndsWith("_Manual_Day", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (name.EndsWith("_Auto", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return name.EndsWith("_Manual", StringComparison.OrdinalIgnoreCase) ? 2 : 4;
    }

    /// <summary>Чи подія <paramref name="winner"/> має пріоритет над <paramref name="other"/> (менший ранг, далі ім'я).</summary>
    /// <param name="winner">Шаблон-кандидат на перемогу.</param>
    /// <param name="other">Другий шаблон.</param>
    public static bool Outranks(string winner, string other)
    {
        var a = Rank(winner);
        var b = Rank(other);
        return a != b ? a < b : string.Compare(winner, other, StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>Чи атрибути однієї події в двох шаблонах розходяться (порівнюються лише спільні за іменем і областю).</summary>
    /// <param name="left">Подія першого шаблону.</param>
    /// <param name="right">Подія другого шаблону.</param>
    public static bool AttributesDiffer(SourceEvent left, SourceEvent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        foreach (var a in left.Attributes)
        {
            var b = right.Attributes.FirstOrDefault(x => x.Scope == a.Scope
                                                         && string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            if (b is not null && (a.ValueNumeric != b.ValueNumeric || !string.Equals(a.ValueString, b.ValueString, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
