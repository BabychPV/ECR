using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Розв'язана константа: **або** число з одиницею, **або** текст.
/// </summary>
/// <remarks>
/// ⚠ Одиниці в текстової константи немає навмисно: вимір — властивість числа,
/// і <c>null</c> тут не «невідомо», а «не застосовне» (ФВ-16.1).
/// </remarks>
/// <param name="Number">Число; <c>null</c> — константа текстова.</param>
/// <param name="Text">Текст; <c>null</c> — константа числова.</param>
/// <param name="UnitId">Одиниця числа; <c>null</c> для тексту.</param>
public sealed record ResolvedConstant(decimal? Number, string? Text, int? UnitId);

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
    /// <returns>Значення константи; <c>null</c> — на цю дату жодного кандидата немає.</returns>
    /// <remarks>
    /// ⛔ Повертається <b>або число, або текст</b> (директива ПК-1 №05, поправка
    /// 2-біс). Раніше тип був <c>(decimal, int)</c>, і ~90 текстових констант,
    /// ужитих у порівняннях
    /// (<c>if(@Land_Category = CST.k1_CategorySelection_, …)</c>), не мали як
    /// доїхати до виразу взагалі.
    /// </remarks>
    public async Task<ResolvedConstant?> ResolveAsync(
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
        //
        // ⛔ Питає ДОМЕН (`MethodologyConstant.IsValidOn`), а не повторює його
        //    умову. Копія, що стояла тут, була закритим інтервалом
        //    (`onDate <= c.ValidTo`) — на день довшим за модель `[from, to)`
        //    (крок I.10, директива ПК-1 №05 §7).
        var valid = candidates
            .Where(c => c.IsValidOn(onDate))
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

        // ⛔ Нерозібране число до рантайму доїхати не мало: публікація його
        // відхиляє. Якщо все ж доїхало — рядок пройшов повз публікацію (масова
        // вставка імпортера), і мовчазний нуль тут дав би 16 формул із
        // правдоподібними числами замість однієї зрозумілої відмови.
        if (chosen.Kind == ConstantKind.Numeric && chosen.Value is null)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code}» версії {methodologyVersionId} оголошена числовою, "
                + $"але містить «{chosen.TextValue ?? "—"}»: значення не є числом.");
        }

        // ⛔ Мітка категорії у виразі — помилка публікації, а не значення. Тут
        // вона трапляється з тієї самої причини і відхиляється так само.
        if (!chosen.IsAllowedInExpression)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code}» версії {methodologyVersionId} — мітка категорії "
                + "і у виразах не бере участі.");
        }

        return chosen.Kind == ConstantKind.Numeric
            ? new ResolvedConstant(chosen.Value, null, chosen.UnitId)
            : new ResolvedConstant(null, chosen.TextValue, null);
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
