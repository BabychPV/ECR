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

        return Resolve(candidates, methodologyVersionId, code, category, substanceEntryId, onDate);
    }

    /// <summary>
    /// Те саме правило вибору над уже прочитаними кандидатами — без походу в
    /// сховище.
    /// </summary>
    /// <param name="candidates">Усі константи версії з цим кодом (порядок — за <c>Id</c>).</param>
    /// <param name="methodologyVersionId">Версія методології (для тексту помилки).</param>
    /// <param name="code">Код константи.</param>
    /// <param name="category">Категорія; <c>null</c> — без категорії.</param>
    /// <param name="substanceEntryId">Речовина; <c>null</c> — спільна константа.</param>
    /// <param name="onDate">Дата періоду для темпорального вибору.</param>
    /// <returns>Значення константи; <c>null</c> — кандидата немає.</returns>
    /// <remarks>
    /// ⛔ Саме цим шляхом іде прогін (аудит P1). Константи версії читаються
    /// ОДНИМ запитом у <c>GenericCalculationModule.PrepareAsync</c>, а тут
    /// лише вибираються в пам'яті. Доти кожен рядок × речовина × код ходив у
    /// базу окремим <c>SELECT</c>: 500 рядків × 20 речовин × 30 констант —
    /// 300 тис. запитів на одну прив'язку по відповідь, яка в межах версії не
    /// змінюється. Правило вибору одне для обох шляхів — копії немає.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Модуль отримує резолвер залежністю; статичний метод лишив би її непрочитаною (CS9113).")]
    public ResolvedConstant? Resolve(
        IReadOnlyList<MethodologyConstant> candidates, int methodologyVersionId, string code,
        string? category, long? substanceEntryId, DateOnly onDate)
    {
        ArgumentNullException.ThrowIfNull(candidates);

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
        //
        // ⛔ Третього кроку «тоді будь-яка» НЕМАЄ (аудит A1). Тут стояло
        //    `?? valid`: коли для речовини B не було ні власної, ні загальної
        //    константи, повертався весь набір — тобто константи ЧУЖИХ речовин,
        //    і за рівно одного такого кандидата перевірка неоднозначності
        //    нижче мовчала. Прогін для B множив на коефіцієнт A без жодної
        //    помилки. Тепер — «кандидатів немає» → `null` → формула читає
        //    `#REF` (`MethodologyEvaluationContext.GetConstant`).
        //    Прогін без речовини (`substanceEntryId = null`) бере лише загальні
        //    константи: з кількох речовинних вибрати «свою» нема за чим, а
        //    єдину речовинну підставити означало б ту саму ваду.
        var bySubstance = Narrow(valid, c => c.SubstanceEntryId == substanceEntryId)
                          ?? Narrow(valid, c => c.SubstanceEntryId is null);

        if (bySubstance is null)
        {
            return null;
        }

        // ⚠ Без категорії (`category = null`) звуження немає, як і було: так
        //    кличе рушій (`GenericCalculationModule`), і константи корпусу
        //    несуть категорію-мітку («default») навіть там, де вона одна.
        //    Задана категорія — так само без «тоді будь-яка»: константа
        //    категорії «K1» до «K2» не застосовується (аудит A1).
        //
        // ✎ L-2 (`calc.CategoryRule`): категорію рядка тепер задає правило версії. AF
        //    зберігає спільні константи методології не `null`, а буквально
        //    `Category = "Common"` (k1…k5 вибору категорії, межі), тож за заданої
        //    категорії кандидатами «без звуження» є і вони. Точний збіг виграє; заповнювачів
        //    для категорій, яких у AF немає, не створюється — немає значення = `#REF`.
        var byCategory = category is null
            ? bySubstance
            : Narrow(bySubstance, c => string.Equals(c.Category, category, StringComparison.Ordinal))
              ?? Narrow(bySubstance, c => c.Category is null
                  || string.Equals(c.Category, MethodologyConstant.CommonCategory, StringComparison.Ordinal));

        if (byCategory is null)
        {
            return null;
        }

        // ⛔ Кілька кандидатів на одну дату — помилка конфігурації, а не привід
        //    узяти перший. «Перший ліпший» тут означає, що число звіту залежить
        //    від порядку рядків у таблиці — і змінюється від переіндексації.
        if (byCategory.Count > 1)
        {
            // Категорії-кандидати: без них «16 кандидатів» не каже, ЩО саме не
            // вибрано (Land Demo RC9, L-2: константи AF 5.1/5.2/6.1 мають
            // категорії, а рушій категорію не передає).
            var categories = string.Join(
                ", ",
                byCategory.Select(c => c.Category ?? "—").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code}» версії {methodologyVersionId} має {byCategory.Count} кандидатів "
                + $"на {onDate:yyyy-MM-dd}: вибір неоднозначний (категорії: {categories}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.constantAmbiguous",
                    ["code"] = code,
                    ["count"] = byCategory.Count,
                    ["categories"] = categories,
                    ["date"] = onDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                });
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
                + $"але містить «{chosen.TextValue ?? "—"}»: значення не є числом.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.constantNotNumeric",
                    ["code"] = code,
                });
        }

        // ⛔ Мітка категорії у виразі — помилка публікації, а не значення. Тут
        // вона трапляється з тієї самої причини і відхиляється так само.
        if (!chosen.IsAllowedInExpression)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code}» версії {methodologyVersionId} — мітка категорії "
                + "і у виразах не бере участі.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.constantIsCategoryLabel",
                    ["code"] = code,
                });
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
