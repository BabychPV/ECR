using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Calculations;

/// <summary>
/// Резолвить константи методології за категорією, речовиною і датою.
/// </summary>
/// <remarks>
/// Саме тут живуть **контекстні коефіцієнти** — щільність, теплотворність,
/// молярна маса. Вони залежать від речовини й умов і змінюються з часом, тому
/// не є конверсіями одиниць і в <c>uom.Conversion</c> потрапити не можуть
/// (ФВ-16.5). Це розмежування — головне, що не дає числам «попливти» глобально.
/// </remarks>
public sealed class ConstantResolver(IConstantStore constants)
{
    /// <summary>Значення константи з одиницею.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="category">Категорія; <c>null</c> — без категорії.</param>
    /// <param name="substanceEntryId">Речовина; <c>null</c> — спільна константа.</param>
    /// <param name="onDate">Дата періоду для темпорального вибору.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<(decimal Value, int UnitId)?> ResolveAsync(
        int methodologyVersionId, string code, string? category, long? substanceEntryId,
        DateOnly onDate, CancellationToken ct)
    {
        var candidates = await constants
            .GetCandidatesAsync(methodologyVersionId, code, ct)
            .ConfigureAwait(false);

        // 1. Темпоральний фільтр — ПЕРШИЙ. Константа, чинна не в цю дату,
        //    не є кандидатом узагалі; звужувати за нею після вибору за
        //    речовиною означало б інколи не знаходити нічого там, де
        //    правильний варіант існує.
        var valid = candidates
            .Where(c => (c.ValidFrom is null || onDate >= c.ValidFrom)
                        && (c.ValidTo is null || onDate <= c.ValidTo))
            .ToList();

        if (valid.Count == 0)
        {
            return null;
        }

        // 2. ⚠ Точний збіг за речовиною виграє над загальним. Інакше
        //    коефіцієнт емісії ХСК застосувався б і до завислих речовин:
        //    число вийшло б правдоподібне і невірне втричі.
        var bySubstance = Narrow(valid, c => c.SubstanceEntryId == substanceEntryId)
                          ?? Narrow(valid, c => c.SubstanceEntryId is null)
                          ?? valid;

        var byCategory = category is null
            ? bySubstance
            : Narrow(bySubstance, c => string.Equals(c.Category, category, StringComparison.Ordinal))
              ?? Narrow(bySubstance, c => c.Category is null)
              ?? bySubstance;

        // ⛔ Кілька кандидатів на одну дату — помилка конфігурації, а не привід
        //    узяти перший. «Перший ліпший» тут означає, що число звіту залежить
        //    від порядку рядків у таблиці — і змінюється від переіндексації.
        if (byCategory.Count > 1)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code}» версії {methodologyVersionId} має {byCategory.Count} кандидатів "
                + $"на {onDate:yyyy-MM-dd}: вибір неоднозначний.");
        }

        var chosen = byCategory[0];
        return (chosen.Value, chosen.UnitId);
    }

    /// <summary>Звуження, яке не робиться, якщо нічого не залишає.</summary>
    /// <remarks>
    /// Порожній результат тут означає «за цією ознакою кандидатів немає», а не
    /// «кандидатів немає взагалі»: далі пробується загальніша ознака.
    /// </remarks>
    private static List<MethodologyConstant>? Narrow(
        List<MethodologyConstant> candidates, Func<MethodologyConstant, bool> predicate)
    {
        var narrowed = candidates.Where(predicate).ToList();
        return narrowed.Count > 0 ? narrowed : null;
    }
}
