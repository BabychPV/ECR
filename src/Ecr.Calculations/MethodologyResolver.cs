using Ecr.Domain.Entities.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Підбирає версію методології, чинну на дату періоду, і зіставляє її з
/// рядками документа за правилами (ФВ-13.3).
/// </summary>
public sealed class MethodologyResolver(IMethodologyStore store, ICellStore cells, IRowStore rows)
{
    /// <summary>Знаходить чинну версію методології на дату.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="onDate">Дата періоду, а не «сьогодні» (ФВ-9.3).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Дескриптор чинної версії; <c>null</c> — методологія на цю дату ЩЕ НЕ
    /// ДІЄ (усі опубліковані версії починаються пізніше).
    /// </returns>
    /// <exception cref="DomainException">
    /// Немає жодної опублікованої версії взагалі — <c>ECR-CALC-0422</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ F-04 (четвертий раунд UX), рішення: період РАНІШЕ за першу чинну
    /// версію — не помилка, а «методологія тут не застосовується». Доти він
    /// валив ВЕСЬ перерахунок документа («Методологія 2 не має версії, чинної на
    /// 2025-12-31»), разом із методологіями, які для цього періоду чинні, і
    /// разом із формулами шаблону — тобто один новий метод робив неможливим
    /// перерахунок кожного історичного періоду. Чинна на дату версія —
    /// властивість ЧАСУ (ФВ-9.3): методологія, запроваджена з 2026-01-01, для
    /// грудня 2025 просто ще не існувала, і правильний результат там —
    /// відсутність числа, а не відмова. Оркестратор уже пропускає <c>null</c>
    /// (<c>CalculationOrchestrator.RunAsync</c>), а сітка показує порожню
    /// комірку, не нуль (D-69: результат читається за посиланням).
    ///
    /// ⚠ Методологія, прив'язана до таблиці, але без ЖОДНОЇ опублікованої
    /// версії, — інше: це незавершена конфігурація, і мовчазний пропуск сховав
    /// би її назавжди. Там відмова лишається, але англійським текстом і з
    /// ключем (текст винятку потрапляє в журнал задачі як є).
    /// </remarks>
    public async Task<MethodologyDescriptor?> ResolveVersionAsync(
        int methodologyId, DateOnly onDate, CancellationToken ct)
    {
        var versions = await store.GetPublishedVersionsAsync(methodologyId, ct).ConfigureAwait(false);

        // ⛔ Правило вибору береться з домену
        // (`MethodologyVersionKey.Currency`), а не повторюється тут. Тут
        // стояло `OrderByDescending(v => v.EffectiveFrom).FirstOrDefault()`,
        // тобто за рівних дат відповідь визначав порядок рядків, який поверне
        // SQL Server, — рівно дефект чинної системи (`H-24d-4`), відтворений
        // у нашому коді. Правило: пізніша дата → старша версія → більший Id.
        var version = versions
            .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= onDate)
            .OrderByDescending(
                v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id),
                MethodologyVersionKey.Currency)
            .FirstOrDefault();

        // ⛔ Опублікованої версії немає ЗОВСІМ — помилка конфігурації:
        // методологію прив'язали до таблиці, але вона не має чим рахувати.
        if (!versions.Any(v => v.EffectiveFrom is not null))
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Methodology {methodologyId.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + "is bound to a table but has no published version to calculate with.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.noPublishedVersion",
                    ["methodologyId"] = methodologyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⚠ F-04: версії є, але всі починаються ПІЗНІШЕ — на цю дату
        // методологія ще не діє, і цей період вона не рахує (див. remarks).
        if (version is null)
        {
            return null;
        }

        return new MethodologyDescriptor(
            methodologyId,
            version.Id,
            version.Version,
            version.Version,
            version.Level,
            version.NumericMode,
            version.CalendarMode,
            version.TraceLevel);
    }

    /// <summary>Визначає, які рядки документа обробляє методологія.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці документа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ключі рядків, що підпали під правила.</returns>
    /// <remarks>
    /// ⚠ <c>MatchJson</c> — **структурований** предикат, а не вираз. Діалект
    /// методологій посилань на комірки документів не має (02b §3.4), а третій
    /// діалект не створюється (D-92). Тому предикат обчислюється в пам'яті над
    /// значеннями рядка (Q-026).
    /// <para>
    /// Правила впорядковані за <c>Priority</c>, і **перший збіг виграє**
    /// (ФВ-13.4). Матриця покриття будується на тому самому механізмі: рядки,
    /// що не закрилися жодним правилом, — це те, що вона показує.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> MatchRowsAsync(
        int methodologyVersionId, long tableInstanceId, CancellationToken ct)
    {
        var matches = await MatchRowsWithRulesAsync(methodologyVersionId, tableInstanceId, ct)
            .ConfigureAwait(false);

        return [.. matches.Select(m => m.RowKey)];
    }

    /// <summary>
    /// Те саме, але з <b>назвою правила</b>, яке рядок закрило.
    /// </summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці документа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Саме тут «перший збіг виграє» (<c>ФВ-13.4</c>) стає <b>видимим</b>.
    /// Доки метод повертав самі ключі рядків, правило-переможець ніде не
    /// з'являлося: порядок за <c>Priority</c> не міняв відповіді, і вимогу
    /// неможливо було ані перевірити, ані порушити. Тепер видно, ЯКЕ
    /// правило спрацювало, і це те, що показує матриця покриття
    /// (<c>ФВ-13.9</c>) і чого бракує в поясненні розрахунку.
    /// </remarks>
    /// <returns>Пари «рядок → код правила», що його закрило.</returns>
    public async Task<IReadOnlyList<RowRuleMatch>> MatchRowsWithRulesAsync(
        int methodologyVersionId, long tableInstanceId, CancellationToken ct)
    {
        var rules = await store.GetRulesAsync(methodologyVersionId, ct).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return [];
        }

        var instance = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var rowIds = await rows
            .GetRowIdsAsync(tableInstanceId, new PeriodKey(instance.PeriodKey), ct)
            .ConfigureAwait(false);

        var slice = await cells.ReadSliceAsync(tableInstanceId, ct).ConfigureAwait(false);

        // Значення рядка: колонка → рядкове подання. Порівняння в предикаті
        // текстове навмисно — MatchJson описує коди довідників і ознаки, а не
        // числові діапазони.
        var byRow = slice
            .GroupBy(c => c.Address.TableRowId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    c => c.Address.ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    c => MethodologyRuleMatcher.Text(c.Value),
                    StringComparer.Ordinal));

        var compiled = MethodologyRuleMatcher.Compile(rules);
        var matched = new List<RowRuleMatch>();

        foreach (var (rowKey, rowId) in rowIds)
        {
            var values = byRow.TryGetValue(rowId, out var found)
                ? found
                : new Dictionary<string, string?>(StringComparer.Ordinal);

            // ⛔ ПЕРШИЙ збіг, а не «будь-який»: правила вже впорядковані
            // сховищем за `Priority`, і перебирати далі означало б
            // дозволити загальному правилу «вся таблиця» мовчки перекрити
            // точніше, поставлене перед ним.
            // Класифікація спільна з матрицею покриття (ФВ-13.9).
            var winner = MethodologyRuleMatcher.Classify(compiled, values).Winner;

            if (winner is not null)
            {
                matched.Add(new RowRuleMatch(rowKey, winner.Code));
            }
        }

        return matched;
    }
}

/// <summary>Рядок і правило, яке його закрило.</summary>
/// <param name="RowKey">Ключ рядка документа.</param>
/// <param name="RuleCode">Код правила-переможця (<c>ФВ-13.4</c>).</param>
public sealed record RowRuleMatch(string RowKey, string RuleCode);
