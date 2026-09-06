using System.Text.Json;
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
    /// <exception cref="DomainException">
    /// Немає жодної чинної версії — <c>ECR-CALC-0422</c>.
    /// </exception>
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

        // ⛔ Жодної чинної версії — це не порожній результат, а помилка
        // конфігурації: методологію прив'язали до таблиці, але вона не має
        // чим рахувати. Повернути null тихо означало б, що рядки просто не
        // порахуються, і нуль у звіті виглядатиме як виміряне значення.
        if (version is null)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Методологія {methodologyId} не має версії, чинної на {onDate:yyyy-MM-dd}.");
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
                    Text,
                    StringComparer.Ordinal));

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
            var winner = rules.FirstOrDefault(rule => Matches(rule.MatchJson, values));

            if (winner is not null)
            {
                matched.Add(new RowRuleMatch(rowKey, winner.Code));
            }
        }

        return matched;
    }

    /// <summary>Чи задовольняє рядок предикат правила.</summary>
    /// <remarks>
    /// Формат: плаский JSON-об'єкт «колонка → очікуване значення». Усі пари
    /// мусять збігтися — це кон'юнкція. Складніші форми свідомо не
    /// підтримуються: правило, яке неможливо прочитати очима, неможливо й
    /// перевірити на перетин із сусіднім (ФВ-13.9).
    /// </remarks>
    private static bool Matches(string matchJson, Dictionary<string, string?> values)
    {
        try
        {
            using var document = JsonDocument.Parse(matchJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var expected = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();

                if (!values.TryGetValue(property.Name, out var actual)
                    || !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // ⚠ Порожній об'єкт збігається з УСІМА рядками. Це легальне
            // правило «вся таблиця», і саме тому воно має найнижчий Priority:
            // інакше воно перекрило б усі точніші.
            return true;
        }
        catch (JsonException)
        {
            // Зламаний предикат не збігається ні з чим. Кинути звідси означало б
            // зупинити прогін цілої таблиці через одне правило; помилку ловить
            // перевірка публікації, а тут вона не має валити решту.
            return false;
        }
    }

    /// <summary>Значення комірки як текст для порівняння в предикаті.</summary>
    private static string? Text(CellRecord cell)
        => cell.Value.ValueString
           ?? cell.Value.ValueNumeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? cell.Value.ValueRegistryEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? cell.Value.ValueBool?.ToString();
}

/// <summary>Рядок і правило, яке його закрило.</summary>
/// <param name="RowKey">Ключ рядка документа.</param>
/// <param name="RuleCode">Код правила-переможця (<c>ФВ-13.4</c>).</param>
public sealed record RowRuleMatch(string RowKey, string RuleCode);
