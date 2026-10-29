using Ecr.Domain.Entities.Configuration;

namespace Ecr.Domain.Services;

/// <summary>Група дерева заголовків за кодами (без Id): вхід чистої перевірки.</summary>
public sealed record HeaderGroupNode(string Code, string? ParentCode);

/// <summary>Колонка таблиці та її група (код) — лише живі (не видалені) колонки.</summary>
public sealed record HeaderColumnPlacement(string Code, int Ordinal, string? GroupCode);

/// <summary>Порушення правила груп заголовків: ключ каталогу, аргументи, текст.</summary>
public sealed record HeaderGroupViolation(string MessageKey, IReadOnlyDictionary<string, string> Args, string Message);

/// <summary>
/// Чисті правила дерева груп заголовків (<c>ECR-TMPL-4229</c>): унікальність коду, батько в тій самій таблиці,
/// без циклів, глибина ≤ 3, колонки групи (разом з нащадками) — СУМІЖНІ за <c>Ordinal</c>.
/// </summary>
public static class ColumnHeaderGroupRules
{
    /// <summary>Перевіряє дерево й розміщення колонок; порожній список — усе гаразд.</summary>
    public static IReadOnlyList<HeaderGroupViolation> Validate(
        string tableCode, IReadOnlyList<HeaderGroupNode> groups, IReadOnlyList<HeaderColumnPlacement> columns)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(columns);

        var result = new List<HeaderGroupViolation>();
        var byCode = new Dictionary<string, HeaderGroupNode>(StringComparer.Ordinal);

        foreach (var g in groups)
        {
            if (!byCode.TryAdd(g.Code, g))
            {
                result.Add(new HeaderGroupViolation(
                    "err.ECR-TMPL-4229.duplicateGroupCode",
                    new Dictionary<string, string> { ["tableCode"] = tableCode, ["groupCode"] = g.Code },
                    $"Група {g.Code} таблиці {tableCode} зустрічається двічі."));
            }
        }

        foreach (var g in byCode.Values)
        {
            if (g.ParentCode is { } p && !byCode.ContainsKey(p))
            {
                result.Add(new HeaderGroupViolation(
                    "err.ECR-TMPL-4229.parentNotFound",
                    new Dictionary<string, string>
                    {
                        ["tableCode"] = tableCode, ["groupCode"] = g.Code, ["parentCode"] = p,
                    },
                    $"Батьківської групи {p} для {g.Code} немає в таблиці {tableCode}."));
                continue;
            }

            // Підйом по батьках: цикл — повторний візит; глибина — довжина ланцюга.
            var depth = 1;
            var seen = new HashSet<string>(StringComparer.Ordinal) { g.Code };
            var cursor = g;
            var cyclic = false;
            var broken = false;
            while (cursor.ParentCode is { } up)
            {
                if (!byCode.TryGetValue(up, out var parent))
                {
                    broken = true; // його власне порушення — parentNotFound — вже враховане
                    break;
                }

                if (!seen.Add(up))
                {
                    cyclic = true;
                    break;
                }

                depth++;
                cursor = parent;
            }

            if (cyclic)
            {
                result.Add(new HeaderGroupViolation(
                    "err.ECR-TMPL-4229.cycle",
                    new Dictionary<string, string> { ["tableCode"] = tableCode, ["groupCode"] = g.Code },
                    $"Група {g.Code} таблиці {tableCode} входить у цикл."));
                continue;
            }

            if (broken)
            {
                continue;
            }

            if (depth > ColumnHeaderGroupDef.MaxDepth)
            {
                result.Add(new HeaderGroupViolation(
                    "err.ECR-TMPL-4229.depthExceeded",
                    new Dictionary<string, string>
                    {
                        ["tableCode"] = tableCode, ["groupCode"] = g.Code,
                        ["maxDepth"] = ColumnHeaderGroupDef.MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    $"Група {g.Code} таблиці {tableCode} глибша за {ColumnHeaderGroupDef.MaxDepth} рівні."));
            }
        }

        foreach (var c in columns)
        {
            if (c.GroupCode is { } gc && !byCode.ContainsKey(gc))
            {
                result.Add(new HeaderGroupViolation(
                    "err.ECR-TMPL-4229.unknownGroup",
                    new Dictionary<string, string>
                    {
                        ["tableCode"] = tableCode, ["columnCode"] = c.Code, ["groupCode"] = gc,
                    },
                    $"Колонка {c.Code} таблиці {tableCode} посилається на групу {gc}, якої немає в цій таблиці."));
            }
        }

        // Суміжність: позиція колонки серед усіх живих колонок за Ordinal (з Code для стабільності).
        var ordered = columns.OrderBy(c => c.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            // колонка належить своїй групі й усім її предкам
            var code = ordered[i].GroupCode;
            var hops = 0;
            while (code is not null && byCode.TryGetValue(code, out var node) && hops++ <= groups.Count)
            {
                if (!positions.TryGetValue(code, out var list))
                {
                    positions[code] = list = [];
                }

                list.Add(i);
                code = node.ParentCode;
            }
        }

        foreach (var (code, list) in positions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (list.Count > 0 && list[^1] - list[0] + 1 != list.Count)
            {
                result.Add(new HeaderGroupViolation(
                    "err.ECR-TMPL-4229.notContiguous",
                    new Dictionary<string, string> { ["tableCode"] = tableCode, ["groupCode"] = code },
                    $"Колонки групи {code} таблиці {tableCode} мають бути суміжні за порядком."));
            }
        }

        return result;
    }
}
