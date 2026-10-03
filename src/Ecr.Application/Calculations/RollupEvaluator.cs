// src/Ecr.Application/Calculations/RollupEvaluator.cs
using System.Globalization;
using Ecr.Application.Templates;

namespace Ecr.Application.Calculations;

/// <summary>Рядок таблиці для зв'язків (D-230): значення вже прочитані, база не потрібна.</summary>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Numbers">Числові значення колонок (<c>null</c> — порожньо).</param>
/// <param name="Texts">Текстові значення колонок (для ключів зіставлення).</param>
public sealed record RelationRow(
    string RowKey,
    IReadOnlyDictionary<string, decimal?> Numbers,
    IReadOnlyDictionary<string, string?> Texts)
{
    /// <summary>Значення колонки як ключ зіставлення: текст, інакше число (інваріантно); порожнє — <c>null</c>.</summary>
    /// <param name="column">Код колонки.</param>
    /// <returns>Ключ або <c>null</c>, якщо порожньо.</returns>
    public string? KeyOf(string column)
    {
        if (Texts.TryGetValue(column, out var t) && !string.IsNullOrEmpty(t))
        {
            return t;
        }

        return Numbers.TryGetValue(column, out var n) && n is not null
            ? CanonicalNumber(n.Value)
            : null;
    }

    /// <summary>Канонічний числовий ключ: без хвостових нулів масштабу (аудит L7-07).</summary>
    /// <remarks>
    /// ⛔ Збережене число приходить із <c>decimal(34,16)</c> як <c>5.0000000000000000</c>, а
    /// результат формули цього ж прогону — як <c>5</c>; рядкове порівняння їх розводило.
    /// </remarks>
    /// <param name="value">Число.</param>
    /// <returns>Інваріантний текст без хвостових нулів; нуль — <c>"0"</c>.</returns>
    public static string CanonicalNumber(decimal value)
        => value == 0m ? "0" : value.ToString("0.############################", CultureInfo.InvariantCulture);
}

/// <summary>Запис Rollup у комірку приймача.</summary>
/// <param name="TargetRowKey">Рядок приймача.</param>
/// <param name="TargetColumn">Колонка приймача.</param>
/// <param name="Value">Значення; <c>null</c> — порожнє джерело.</param>
public sealed record RollupWrite(string TargetRowKey, string TargetColumn, decimal? Value);

/// <summary>Результат Rollup.</summary>
/// <param name="Writes">Що записати (по запису на рядок приймача).</param>
/// <param name="SkippedReason">Чому нічого не обчислено (стабільний код) або <c>null</c>.</param>
public sealed record RollupResult(IReadOnlyList<RollupWrite> Writes, string? SkippedReason);

/// <summary>
/// Чиста функція Rollup (D-230): значення джерела → значення приймача. БЕЗ запису в БД і БЕЗ
/// залежностей від <c>RecalculationService</c>; схеми — ПРИПУЩЕННЯ, див. <c>RelationSpec.cs</c>.
/// </summary>
/// <remarks>
/// Правила (припущення, замінні в одному місці):
/// <list type="bullet">
/// <item>Рядок джерела іде в рядок приймача, якщо ВСІ пари ключів рівні (ординально; <c>null</c>/порожнє
/// ключове значення не дорівнює нічому, навіть іншому порожньому).</item>
/// <item>Порожній <c>keys</c>: усі рядки джерела йдуть до єдиного рядка приймача; якщо рядків приймача не
/// один — нічого не обчислюється (<c>SkippedReason = "ambiguousTarget"</c>), а не вгадується.</item>
/// <item>Дублі ключів у джерелі — норма: усі такі рядки входять в агрегат. Дублі ключів у приймачі: кожен
/// такий рядок отримує той самий агрегат групи.</item>
/// <item>Порожні значення (<c>null</c>) в агрегат не входять. Немає жодного значення: sum/avg/min/max →
/// <c>null</c>, count → 0.</item>
/// <item>Результат округлюється до <c>Scale</c> колонки приймача, AwayFromZero (як імпорт, D-109); count не
/// округлюється.</item>
/// </list>
/// </remarks>
public static class RollupEvaluator
{
    /// <summary>Обчислює Rollup для всіх рядків приймача.</summary>
    /// <param name="match">Зіставлення рядків.</param>
    /// <param name="spec">Налаштування Rollup.</param>
    /// <param name="source">Рядки джерела.</param>
    /// <param name="target">Рядки приймача.</param>
    /// <param name="targetScale">Scale колонки приймача (<c>null</c> — без округлення).</param>
    /// <returns>Записи для приймача.</returns>
    public static RollupResult Evaluate(
        RelationMatchSpec match, RollupSpec spec,
        IReadOnlyList<RelationRow> source, IReadOnlyList<RelationRow> target, byte? targetScale)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var writes = new List<RollupWrite>();

        if (match.Keys.Count == 0)
        {
            if (target.Count != 1)
            {
                return new RollupResult([], "ambiguousTarget");
            }

            writes.Add(Write(target[0], spec, source, targetScale));
            return new RollupResult(writes, null);
        }

        var groups = new Dictionary<string, List<RelationRow>>(StringComparer.Ordinal);
        foreach (var row in source)
        {
            var key = TupleKey(match.Keys.Select(k => row.KeyOf(k.Source)));
            if (key is null)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add(row);
        }

        foreach (var row in target)
        {
            var key = TupleKey(match.Keys.Select(k => row.KeyOf(k.Target)));
            IReadOnlyList<RelationRow> rows = key is not null && groups.TryGetValue(key, out var found) ? found : [];
            writes.Add(Write(row, spec, rows, targetScale));
        }

        return new RollupResult(writes, null);
    }

    /// <summary>Агрегує значення з округленням.</summary>
    /// <param name="aggregate">Агрегат.</param>
    /// <param name="values">Значення (<c>null</c> ігнорується).</param>
    /// <param name="targetScale">Scale приймача.</param>
    /// <returns>Результат; <c>null</c> — немає значень для sum/avg/min/max.</returns>
    public static decimal? Aggregate(RollupAggregate aggregate, IEnumerable<decimal?> values, byte? targetScale)
    {
        ArgumentNullException.ThrowIfNull(values);
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();

        if (aggregate == RollupAggregate.Count)
        {
            return present.Count;
        }

        if (present.Count == 0)
        {
            return null;
        }

        decimal result;
        try
        {
            result = aggregate switch
            {
                RollupAggregate.Sum => present.Sum(),
                RollupAggregate.Avg => present.Sum() / present.Count,
                RollupAggregate.Min => present.Min(),
                RollupAggregate.Max => present.Max(),
                _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
            };
        }
        catch (OverflowException)
        {
            // ⚠ Сума поза decimal — не число, а не впалий перерахунок (аудит L7-03): приймач порожній.
            return null;
        }

        return targetScale is null ? result : Math.Round(result, targetScale.Value, MidpointRounding.AwayFromZero);
    }

    private static RollupWrite Write(
        RelationRow targetRow, RollupSpec spec, IReadOnlyList<RelationRow> sources, byte? scale)
        => new(
            targetRow.RowKey, spec.TargetColumn,
            Aggregate(
                spec.Aggregate,
                sources.Select(r => ValueOf(r, spec)),
                scale));

    /// <summary>Значення рядка для агрегату; для count рахується й непорожній текст (маркер 1).</summary>
    private static decimal? ValueOf(RelationRow row, RollupSpec spec)
    {
        if (row.Numbers.TryGetValue(spec.SourceColumn, out var v) && v is not null)
        {
            return v;
        }

        return spec.Aggregate == RollupAggregate.Count
               && row.Texts.TryGetValue(spec.SourceColumn, out var t) && !string.IsNullOrEmpty(t)
            ? 1m
            : null;
    }

    /// <summary>Складений ключ; <c>null</c>, якщо будь-яка частина порожня. Довжина-префікс не дає зіткнень на кшталт "a|b"+"c".</summary>
    private static string? TupleKey(IEnumerable<string?> parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (p is null)
            {
                return null;
            }

            sb.Append(p.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(p).Append('|');
        }

        return sb.ToString();
    }
}
