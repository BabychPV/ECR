// src/Ecr.Infrastructure/Jobs/QuartzSourceMonths.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ PERF-06b (<c>D-572</c>, live Quartz/InProcess): злиття місяців правки річного/квартального джерела (<c>sourceMonths</c>
/// payload <see cref="RecalculationRequest"/>) у режимі Quartz — те саме правило, що черга в базі
/// (<c>DbJobQueue.MergePressFieldsSql</c>): обидві постановки з місяцями — об'єднання; хоч одна без місяців — увесь рік.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Доти Quartz місяців не ніс узагалі (<c>CalculationTrigger.RequestAsync</c> їх відкидав): злиття в задачу, що чекає,
/// лишало payload ПЕРШОЇ постановки, і місяці другої правки загубилися б. Тому на live (Quartz) правка одного місяця HSE400
/// будила всі відкриті періоди Land року (W15z M1). Тепер payload задачі, що чекає, живе в <see cref="QuartzPayloadMerges"/>
/// (межа злиття — взяття адаптером під тим самим замком), а злиття з ВИКОНУВАНОЮ накопичує місяці для її перепостановки
/// (<see cref="AccumulateDirty"/>), а не лишає payload останнього злиття.
/// </para>
/// <para>⚠ Решта полів — з першої постановки (як у черзі в базі): ціль та сама, тіло однакове, крім місяців.</para>
/// </remarks>
public static class QuartzSourceMonths
{
    /// <summary>Властивість місяців у payload (Web-серіалізація <see cref="RecalculationRequest.SourceMonths"/>).</summary>
    public const string Property = "sourceMonths";

    private static readonly Lock Gate = new();

    private static readonly Dictionary<string, string> Dirty = new(StringComparer.Ordinal);

    /// <summary>Чи payload несе місяці правки.</summary>
    /// <param name="json">Тіло задачі.</param>
    public static bool Has(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject root && root[Property] is JsonArray;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Зливає постановку <paramref name="addJson"/> у тіло <paramref name="baseJson"/>: обидва з місяцями — об'єднання за
    /// зростанням; у <paramref name="baseJson"/> місяців немає — він лишається (увесь рік); у <paramref name="addJson"/>
    /// немає — місяці з <paramref name="baseJson"/> знято (увесь рік). Тіло, що не читається, — без місяців (увесь рік).
    /// </summary>
    /// <param name="baseJson">Тіло задачі, що чекає (чи накопичене).</param>
    /// <param name="addJson">Тіло нової постановки.</param>
    /// <returns>Злите тіло.</returns>
    public static string Merge(string baseJson, string addJson)
    {
        ArgumentNullException.ThrowIfNull(baseJson);
        ArgumentNullException.ThrowIfNull(addJson);

        JsonObject? target;
        try
        {
            target = JsonNode.Parse(baseJson) as JsonObject;
        }
        catch (JsonException)
        {
            target = null;
        }

        if (target is null)
        {
            return Widen(addJson);
        }

        if (target[Property] is not JsonArray into)
        {
            return baseJson;
        }

        JsonArray? from;
        try
        {
            from = JsonNode.Parse(addJson) is JsonObject source ? source[Property] as JsonArray : null;
        }
        catch (JsonException)
        {
            from = null;
        }

        if (from is null)
        {
            target.Remove(Property);
            return target.ToJsonString();
        }

        var months = new SortedSet<int>();
        foreach (var node in into.Concat(from))
        {
            if (node is JsonValue value && value.TryGetValue<int>(out var month))
            {
                months.Add(month);
            }
            else
            {
                // Невідомий елемент — увесь рік (консервативно).
                target.Remove(Property);
                return target.ToJsonString();
            }
        }

        target[Property] = new JsonArray([.. months.Select(m => (JsonNode)JsonValue.Create(m))]);
        return target.ToJsonString();
    }

    /// <summary>
    /// Злиття з ВИКОНУВАНОЮ задачею <paramref name="runningKey"/>: місяці накопичуються (<see cref="Merge"/>) для її
    /// перепостановки після завершення.
    /// </summary>
    /// <param name="runningKey">Планувальник і задача (<c>ім'я|JobId</c>).</param>
    /// <param name="json">Тіло постановки, що злилася.</param>
    /// <returns>Накопичене тіло.</returns>
    public static string AccumulateDirty(string runningKey, string json)
    {
        lock (Gate)
        {
            // ⚠ Решта полів — ОСТАННЬОГО злиття (контракт перепостановки: «з payload останньої постановки»), місяці — усіх.
            var merged = Dirty.TryGetValue(runningKey, out var known) ? MonthsOf(known, into: json) : json;
            Dirty[runningKey] = merged;
            return merged;
        }
    }

    /// <summary>Накопичене тіло перепостановки; <c>null</c> — нічого не злилося.</summary>
    /// <param name="runningKey">Планувальник і задача.</param>
    public static string? PeekDirty(string runningKey)
    {
        lock (Gate)
        {
            return Dirty.GetValueOrDefault(runningKey);
        }
    }

    /// <summary>Перепостановку виконано — накопичене більше не потрібне.</summary>
    /// <param name="runningKey">Планувальник і задача.</param>
    /// <param name="used">Тіло, яке поставлено: знімається лише воно (нове злиття за цей час лишається).</param>
    public static void ForgetDirty(string runningKey, string used)
    {
        lock (Gate)
        {
            if (Dirty.TryGetValue(runningKey, out var known) && string.Equals(known, used, StringComparison.Ordinal))
            {
                Dirty.Remove(runningKey);
            }
        }
    }

    /// <summary>
    /// Тіло <paramref name="into"/> (останнє) з місяцями, злитими з <paramref name="earlier"/> за правилом <see cref="Merge"/>.
    /// </summary>
    private static string MonthsOf(string earlier, string into)
    {
        if (!Has(earlier))
        {
            return Widen(into);
        }

        // Злиття «раніше → останнє» дає ті самі місяці, що «останнє → раніше»; поля беремо з останнього.
        var months = Merge(earlier, into);
        JsonObject? target;
        JsonNode? merged;
        try
        {
            target = JsonNode.Parse(into) as JsonObject;
            merged = (JsonNode.Parse(months) as JsonObject)?[Property];
        }
        catch (JsonException)
        {
            return Widen(into);
        }

        if (target is null)
        {
            return into;
        }

        target.Remove(Property);
        if (merged is JsonArray array)
        {
            target[Property] = array.DeepClone();
        }

        return target.ToJsonString();
    }

    private static string Widen(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return json;
        }

        root.Remove(Property);
        return root.ToJsonString();
    }
}
