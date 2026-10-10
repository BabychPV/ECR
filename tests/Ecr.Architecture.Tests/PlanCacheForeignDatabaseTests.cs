using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Тести читають кеш планів лише над планами СВОЄЇ бази (<c>Z8-01</c>).
/// </summary>
/// <remarks>
/// ⛔ <c>sys.dm_exec_sql_text</c>/<c>sys.dm_exec_query_plan</c> для плану
/// чужої бази відкривають ту базу. Якщо інший прогін на тому самому інстансі
/// саме переводить її в <c>SINGLE_USER</c>, видаляє чи створює, тест падає з
/// Msg 924 без жодної вади в продукті. Фільтр за <c>dbid</c> у тому самому
/// <c>WHERE</c> не допомагає: порядок обчислення <c>APPLY</c> і <c>WHERE</c>
/// не гарантований. Виправлено в <c>204689a96</c>
/// (<c>WritePathPlanCacheTests</c>, <c>TvpBatchWriteTests</c>), а потім той
/// самий запит з'явився ще в чотирьох тестах.
/// <para>
/// Правило: аргумент цих функцій під <c>APPLY</c> береться лише з табличної
/// змінної (<c>FROM @own AS o</c>), куди наперед відібрано плани своєї бази.
/// Готовий помічник — <see cref="PlanCache"/>.
/// </para>
/// </remarks>
public sealed partial class PlanCacheForeignDatabaseTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тести_не_відкривають_чужі_плани_через_dm_exec_sql_text_чи_dm_exec_query_plan()
    {
        var checkedCalls = new List<string>();
        var offenders = new List<string>();

        foreach (var path in SourceTree.Walk(Path.Combine(SourceTree.Root, "tests"))
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.EndsWith(nameof(PlanCacheForeignDatabaseTests) + ".cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(path);
            var relative = Path.GetRelativePath(SourceTree.Root, path).Replace('\\', '/');

            foreach (Match call in ApplyRegex().Matches(text))
            {
                var alias = call.Groups["alias"].Value;
                var line = text.AsSpan(0, call.Index).Count('\n') + 1;
                checkedCalls.Add($"{relative}:{line}");

                var boundToTableVariable = Regex.IsMatch(
                    text,
                    $@"@\w+\s+(?:AS\s+)?{Regex.Escape(alias)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));

                if (!boundToTableVariable)
                {
                    offenders.Add(
                        $"{relative}:{line}: {call.Value.Trim()} — «{alias}» не з табличної змінної своїх планів; "
                        + "візьміть PlanCache.PlansAsync/LatestPlanAsync з Ecr.TestKit");
                }
            }
        }

        // ⚠ Порожній знаменник доводив би не відсутність порушень, а
        // відсутність перевірки: PlanCache і PlanCountAsync мусять знаходитися.
        Assert.Contains(checkedCalls, c => c.StartsWith("tests/Ecr.TestKit/PlanCache.cs:", StringComparison.Ordinal));
        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(
        @"(?:CROSS|OUTER)\s+APPLY\s+(?:sys\.)?dm_exec_(?:sql_text|query_plan|text_query_plan)\s*\(\s*(?<alias>\w+)\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplyRegex();
}
