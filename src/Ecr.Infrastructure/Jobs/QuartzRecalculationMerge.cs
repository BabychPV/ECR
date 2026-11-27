// src/Ecr.Infrastructure/Jobs/QuartzRecalculationMerge.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ Огляд M4/M5 (D-577, live — Quartz): злиття постановки перерахунку документа в задачу цілі, що ЧЕКАЄ, — ті самі правила,
/// що в черзі в базі (<c>DbJobQueue.CoalescePayloadSql</c>), а не «тіло першої постановки».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Місяці правки джерела — <see cref="QuartzSourceMonths.Merge"/> (PERF-06b).</item>
/// <item><c>sourceHop</c> — МЕНША з двох (немає — 0): задача ланки 7, що поглинула правку ланки 1, інакше впиралась би в стелю
/// ланцюжка джерел раніше.</item>
/// <item><c>approverSheets</c> (M5, D-442) — ПЕРЕТИН: аркуш без позначки «після подання» лише тоді, коли кожна злита правка — правка
/// погоджувача цього аркуша; постановка без поля знімає звільнення (консервативно — позначка).</item>
/// <item><c>skipIfFresh</c> — лише коли обидві його мають: злитий запит без нього (натискання, хук джерела, збір) має виконатись.</item>
/// </list>
/// </remarks>
public static class QuartzRecalculationMerge
{
    private const string Hop = "sourceHop";
    private const string Approver = "approverSheets";
    private const string SkipIfFresh = "skipIfFresh";

    /// <summary>Злиття (тіло задачі, що чекає; тіло нової постановки) → нове тіло.</summary>
    /// <param name="baseJson">Тіло задачі, що чекає.</param>
    /// <param name="addJson">Тіло нової постановки.</param>
    /// <returns>Злите тіло.</returns>
    public static string Merge(string baseJson, string addJson)
    {
        ArgumentNullException.ThrowIfNull(baseJson);
        ArgumentNullException.ThrowIfNull(addJson);

        var merged = QuartzSourceMonths.Merge(baseJson, addJson);
        JsonObject? target;
        JsonObject? add;
        try
        {
            target = JsonNode.Parse(merged) as JsonObject;
            add = JsonNode.Parse(addJson) as JsonObject;
        }
        catch (JsonException)
        {
            return merged;
        }

        if (target is null || add is null)
        {
            return merged;
        }

        var hop = Math.Min(IntOf(target[Hop]), IntOf(add[Hop]));
        if (hop != IntOf(target[Hop]))
        {
            target[Hop] = hop;
        }

        if (target[Approver] is JsonArray mine)
        {
            var theirs = add[Approver] is JsonArray other
                ? other.Select(IntOf).ToHashSet()
                : [];
            var kept = mine.Select(IntOf).Where(theirs.Contains).Distinct().Order().ToList();
            target.Remove(Approver);
            if (kept.Count > 0)
            {
                target[Approver] = new JsonArray([.. kept.Select(id => (JsonNode)JsonValue.Create(id))]);
            }
        }

        if (BoolOf(target[SkipIfFresh]) && !BoolOf(add[SkipIfFresh]))
        {
            target[SkipIfFresh] = false;
        }

        return target.ToJsonString();
    }

    private static int IntOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static bool BoolOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
