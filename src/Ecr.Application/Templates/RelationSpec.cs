// src/Ecr.Application/Templates/RelationSpec.cs
using System.Globalization;
using System.Text.Json;

namespace Ecr.Application.Templates;

// ⚠ D-230 «ВИДИ ЗВ'ЯЗКІВ» (ФВ-2.12). У ТЗ немає схеми MatchJson/MapJson — це ПРИПУЩЕННЯ,
// прийняте рішенням людини 2026-10-01 («реалізувати зараз») як нейтральний дефолт.
// Факт замовника тут НЕ вигадано: коли замовник назве власну схему, міняється лише цей
// файл (парсер) і вичислювачі `RollupEvaluator`/`CheckEvaluator`; колонки БД, API і
// редактор зв'язків (рядки JSON) лишаються як є. Опис — у `d230-behavior.md`.

/// <summary>Пара ключових колонок зіставлення рядків.</summary>
/// <param name="Source">Колонка таблиці-джерела.</param>
/// <param name="Target">Колонка таблиці-приймача.</param>
public sealed record RelationKey(string Source, string Target);

/// <summary>
/// Зіставлення рядків: рівність усіх пар ключових колонок. Порожній перелік —
/// «одна ціль — усі джерела»: приймач має рівно один рядок, і до нього йдуть усі
/// рядки джерела.
/// </summary>
/// <param name="Keys">Ключові пари.</param>
public sealed record RelationMatchSpec(IReadOnlyList<RelationKey> Keys);

/// <summary>Агрегат Rollup.</summary>
public enum RollupAggregate
{
    /// <summary>Сума.</summary>
    Sum,

    /// <summary>Середнє.</summary>
    Avg,

    /// <summary>Мінімум.</summary>
    Min,

    /// <summary>Максимум.</summary>
    Max,

    /// <summary>Кількість непорожніх значень.</summary>
    Count,
}

/// <summary>Налаштування Rollup (<c>MapJson</c>).</summary>
/// <param name="SourceColumn">Колонка джерела, що агрегується.</param>
/// <param name="TargetColumn">Колонка приймача, куди йде результат.</param>
/// <param name="Aggregate">Агрегат.</param>
public sealed record RollupSpec(string SourceColumn, string TargetColumn, RollupAggregate Aggregate);

/// <summary>Вид допуску Check.</summary>
public enum CheckToleranceKind
{
    /// <summary>Абсолютний: |left − right| ≤ tolerance.</summary>
    Abs,

    /// <summary>Відносний: |left − right| ≤ tolerance × |right|.</summary>
    Rel,
}

/// <summary>Серйозність знахідки Check.</summary>
public enum CheckSeverity
{
    /// <summary>Інформація (<c>ValidationSeverity.Info</c>).</summary>
    Info,

    /// <summary>Попередження (<c>ValidationSeverity.Warning</c>).</summary>
    Warn,

    /// <summary>Помилка, блокує подання (<c>ValidationSeverity.Error</c>).</summary>
    Block,
}

/// <summary>Налаштування Check (<c>MapJson</c>).</summary>
/// <param name="Left">Колонка таблиці-джерела.</param>
/// <param name="Right">Колонка таблиці-приймача.</param>
/// <param name="Tolerance">Допуск, ≥ 0.</param>
/// <param name="ToleranceKind">Вид допуску.</param>
/// <param name="Severity">Серйозність.</param>
public sealed record CheckSpec(
    string Left, string Right, decimal Tolerance, CheckToleranceKind ToleranceKind, CheckSeverity Severity);

/// <summary>Причина відмови розбору/перевірки схеми.</summary>
/// <param name="Reason">Короткий стабільний код причини (латиниця).</param>
/// <param name="Detail">Людське пояснення (англійською) для параметра відмови.</param>
public sealed record RelationSpecFailure(string Reason, string Detail);

/// <summary>Результат розбору: або значення, або відмова.</summary>
/// <typeparam name="T">Тип схеми.</typeparam>
/// <param name="Value">Значення.</param>
/// <param name="Failure">Відмова.</param>
public readonly record struct RelationSpecResult<T>(T? Value, RelationSpecFailure? Failure) where T : class
{
    /// <summary>Чи розбір вдався.</summary>
    public bool IsOk => Failure is null;
}

/// <summary>Типізований розбір <c>MatchJson</c>/<c>MapJson</c> за припущеними схемами D-230.</summary>
/// <remarks>
/// Невідомі додаткові поля ігноруються (сумісність уперед). Імена агрегатів, видів
/// допуску й серйозності не залежать від регістру.
/// </remarks>
public static class RelationSpecParser
{
    /// <summary>Розбирає <c>MatchJson</c>: <c>{"keys":[{"source":"A","target":"B"}]}</c>; <c>{}</c> — без ключів.</summary>
    /// <param name="matchJson">Текст JSON.</param>
    /// <returns>Схема або відмова.</returns>
    public static RelationSpecResult<RelationMatchSpec> ParseMatch(string? matchJson)
    {
        if (!TryRoot(matchJson, "MatchJson", out var doc, out var fail))
        {
            return Fail<RelationMatchSpec>(fail!);
        }

        using (doc)
        {
            var keys = new List<RelationKey>();
            if (doc!.RootElement.TryGetProperty("keys", out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    return Fail<RelationMatchSpec>("matchKeysShape", "MatchJson.keys must be an array.");
                }

                foreach (var item in arr.EnumerateArray())
                {
                    var s = Str(item, "source");
                    var t = Str(item, "target");
                    if (s is null || t is null)
                    {
                        return Fail<RelationMatchSpec>(
                            "matchKeysShape",
                            "Every MatchJson.keys item must be {\"source\":\"<column>\",\"target\":\"<column>\"} with non-empty strings.");
                    }

                    if (keys.Any(k => k.Source == s && k.Target == t))
                    {
                        return Fail<RelationMatchSpec>("matchKeysShape", $"MatchJson.keys has a duplicate pair {s} -> {t}.");
                    }

                    keys.Add(new RelationKey(s, t));
                }
            }

            return new RelationSpecResult<RelationMatchSpec>(new RelationMatchSpec(keys), null);
        }
    }

    /// <summary>Розбирає Rollup <c>MapJson</c>: <c>{"sourceColumn","targetColumn","aggregate"}</c>.</summary>
    /// <param name="mapJson">Текст JSON.</param>
    /// <returns>Схема або відмова.</returns>
    public static RelationSpecResult<RollupSpec> ParseRollup(string? mapJson)
    {
        if (!TryRoot(mapJson, "MapJson", out var doc, out var fail))
        {
            return Fail<RollupSpec>(fail!);
        }

        using (doc)
        {
            var root = doc!.RootElement;
            var src = Str(root, "sourceColumn");
            var tgt = Str(root, "targetColumn");
            var agg = Str(root, "aggregate");
            if (src is null || tgt is null || agg is null)
            {
                return Fail<RollupSpec>(
                    "mapMissingField",
                    "Rollup MapJson needs non-empty \"sourceColumn\", \"targetColumn\" and \"aggregate\".");
            }

            if (!Enum.TryParse<RollupAggregate>(agg, ignoreCase: true, out var aggregate)
                || !Enum.IsDefined(aggregate) || int.TryParse(agg, out _))
            {
                return Fail<RollupSpec>("aggregateUnknown", $"Unknown aggregate \"{agg}\"; allowed: sum, avg, min, max, count.");
            }

            return new RelationSpecResult<RollupSpec>(new RollupSpec(src, tgt, aggregate), null);
        }
    }

    /// <summary>
    /// Розбирає Check <c>MapJson</c>:
    /// <c>{"left","right","tolerance":"0","toleranceKind":"abs|rel","severity":"Warn|Block|Info"}</c>.
    /// Типові значення: допуск 0, вид <c>abs</c>, серйозність <c>Warn</c>.
    /// </summary>
    /// <param name="mapJson">Текст JSON.</param>
    /// <returns>Схема або відмова.</returns>
    public static RelationSpecResult<CheckSpec> ParseCheck(string? mapJson)
    {
        if (!TryRoot(mapJson, "MapJson", out var doc, out var fail))
        {
            return Fail<CheckSpec>(fail!);
        }

        using (doc)
        {
            var root = doc!.RootElement;
            var left = Str(root, "left");
            var right = Str(root, "right");
            if (left is null || right is null)
            {
                return Fail<CheckSpec>("mapMissingField", "Check MapJson needs non-empty \"left\" and \"right\".");
            }

            var tolerance = 0m;
            if (root.TryGetProperty("tolerance", out var tol))
            {
                var ok = tol.ValueKind switch
                {
                    JsonValueKind.Number => tol.TryGetDecimal(out tolerance),
                    JsonValueKind.String => decimal.TryParse(
                        tol.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out tolerance),
                    _ => false,
                };
                if (!ok || tolerance < 0m)
                {
                    return Fail<CheckSpec>("toleranceInvalid", "\"tolerance\" must be a number >= 0 (invariant culture).");
                }
            }

            var kind = CheckToleranceKind.Abs;
            if (root.TryGetProperty("toleranceKind", out var tk))
            {
                if (tk.ValueKind != JsonValueKind.String
                    || !Enum.TryParse(tk.GetString(), ignoreCase: true, out kind)
                    || !Enum.IsDefined(kind) || int.TryParse(tk.GetString(), out _))
                {
                    return Fail<CheckSpec>("toleranceKindUnknown", "\"toleranceKind\" must be abs or rel.");
                }
            }

            var severity = CheckSeverity.Warn;
            if (root.TryGetProperty("severity", out var sv))
            {
                if (sv.ValueKind != JsonValueKind.String
                    || !Enum.TryParse(sv.GetString(), ignoreCase: true, out severity)
                    || !Enum.IsDefined(severity) || int.TryParse(sv.GetString(), out _))
                {
                    return Fail<CheckSpec>("severityUnknown", "\"severity\" must be Info, Warn or Block.");
                }
            }

            return new RelationSpecResult<CheckSpec>(new CheckSpec(left, right, tolerance, kind, severity), null);
        }
    }

    private static bool TryRoot(string? json, string field, out JsonDocument? doc, out RelationSpecFailure? failure)
    {
        doc = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            failure = new RelationSpecFailure("mapMissing", $"{field} is required for this relation kind.");
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            failure = new RelationSpecFailure("invalidJson", $"{field} is not valid JSON: {ex.Message}");
            return false;
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            doc = null;
            failure = new RelationSpecFailure("notObject", $"{field} must be a JSON object.");
            return false;
        }

        return true;
    }

    private static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!.Trim()
            : null;

    private static RelationSpecResult<T> Fail<T>(RelationSpecFailure f) where T : class => new(null, f);

    private static RelationSpecResult<T> Fail<T>(string reason, string detail) where T : class
        => new(null, new RelationSpecFailure(reason, detail));
}
