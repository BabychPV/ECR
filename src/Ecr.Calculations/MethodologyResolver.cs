using System.Text.Json;
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

        // Максимальний EffectiveFrom ≤ дата. Кінця вікна не існує: версія
        // чинна до початку наступної, і саме тому вибір однозначний.
        var version = versions
            .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= onDate)
            .OrderByDescending(v => v.EffectiveFrom)
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

        var matched = new List<string>();

        foreach (var (rowKey, rowId) in rowIds)
        {
            var values = byRow.TryGetValue(rowId, out var found)
                ? found
                : new Dictionary<string, string?>(StringComparer.Ordinal);

            // Перший збіг виграє: правила вже впорядковані сховищем за
            // Priority, і перебирати далі означало б дозволити останньому
            // правилу мовчки перекрити виняток, поставлений першим.
            if (rules.Any(rule => Matches(rule.MatchJson, values)))
            {
                matched.Add(rowKey);
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
