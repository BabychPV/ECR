using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// A <c>{placeholder}</c> of a refusal template fed by a value that may be <c>null</c> — the key choice must depend on
/// that value (the template that names it is used only when it is there).
/// </summary>
/// <remarks>
/// ⛔ F13 p220 re-check (09.11): the j4 gap refusal read «Move the start date to <b>{suggestedFrom}</b> or add a permit
/// for those days». <c>PermitHistoryRules.Gap</c> started returning <c>SuggestedFrom = null</c> (no start date closes the
/// gap) and the handler kept <c>["suggestedFrom"] = gap.SuggestedFrom</c> under the «move» template. The placeholder
/// guard (<see cref="MessageKeyRatchetTests"/>) saw the field NAMED and stayed green: it checks names, not values. Here:
/// a value <c>x.Member</c> whose <c>Member</c> is declared only nullable in <c>src</c> (<c>string? Member</c>, …), fed to
/// a placeholder of the template, requires the <c>messageKey</c> expression to mention <c>Member</c> (e.g.
/// <c>gap.SuggestedFrom is null ? "…NoMove" : "…permitGap"</c>).
/// ⚠ Text heuristic: only <c>ident.Member</c> values and members never declared non-nullable anywhere in <c>src</c>.
/// </remarks>
public sealed partial class NullablePlaceholderValueTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Finding", "F13-p220-j4-recheck")]
    public void Плейсхолдер_із_можливим_null_лише_під_ключем_що_від_нього_залежить()
    {
        var files = SourceTree.Production();
        var failures = Check(files, Templates(), out var checkedSites);
        var open = failures.Where(f => !Reviewed.Keys.Any(r => f.Contains(r, StringComparison.Ordinal))).ToList();
        var stale = Reviewed.Keys.Where(r => !failures.Any(f => f.Contains(r, StringComparison.Ordinal))).ToList();

        Assert.NotEqual(0, checkedSites);
        Assert.True(open.Count == 0, string.Join(Environment.NewLine, open));
        Assert.True(stale.Count == 0, "Reviewed entries no longer found (remove them): " + string.Join(", ", stale));
    }

    /// <summary>The guard turns red on the shape of the F13 defect and green on its fix.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_червоніє_на_формі_дефекту_F13()
    {
        const string sample = """
            namespace Sample;
            internal sealed record GapText(string Days, string? SuggestedFrom);
            internal static class Throws
            {
                public static void Defect(GapText gap) =>
                    throw new BusinessRuleException("ECR-X", "Речення.", new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.X-4223.gap",
                        ["days"] = gap.Days,
                        ["suggestedFrom"] = gap.SuggestedFrom,
                    });

                public static void Fixed(GapText gap) =>
                    throw new BusinessRuleException("ECR-X", "Речення.", new Dictionary<string, object?>
                    {
                        ["messageKey"] = gap.SuggestedFrom is null ? "err.X-4223.gapNoMove" : "err.X-4223.gap",
                        ["days"] = gap.Days,
                        ["suggestedFrom"] = gap.SuggestedFrom,
                    });
            }
            """;
        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["err.X-4223.gap"] = "{days}: move the start to {suggestedFrom}.",
            ["err.X-4223.gapNoMove"] = "{days}: add a permit.",
        };

        var failures = Check([new SourceFile("src/Sample.cs", sample)], templates, out var checkedSites);

        Assert.Equal(2, checkedSites);
        var failure = Assert.Single(failures);
        Assert.Contains("src/Sample.cs:", failure, StringComparison.Ordinal);
        Assert.Contains("{suggestedFrom}", failure, StringComparison.Ordinal);
        Assert.Contains("SuggestedFrom", failure, StringComparison.Ordinal);
    }

    /// <summary>Sites read by a person: the member is nullable by type, but never null on this path (reason given).</summary>
    private static readonly Dictionary<string, string> Reviewed = new(StringComparer.Ordinal)
    {
        ["HeaderValueReader.cs:86: err.ECR-HDR-0422.ambiguousSeparator — {asGroup}"] =
            "an Ambiguous reading always carries both readings (CultureNumberReader.Read)",
        ["HeaderValueReader.cs:86: err.ECR-HDR-0422.ambiguousSeparator — {asDecimal}"] =
            "an Ambiguous reading always carries both readings (CultureNumberReader.Read)",
        ["EntityFieldMapLifecycleHandlers.cs:232: err.ECR-INT-0422.pendingUnitNotInCatalog — {unitCode}"] =
            "under PendingSourceUnitId is { }: EntityFieldMap sets the pending id and code together (PauseForSourceUnitChange)",
    };

    private static List<string> Check(IReadOnlyList<SourceFile> files, Dictionary<string, string> templates, out int checkedSites)
    {
        var nullable = new HashSet<string>(StringComparer.Ordinal);
        var nonNullable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match m in MemberType().Matches(file.Text))
            {
                (m.Groups["q"].Success ? nullable : nonNullable).Add(m.Groups["name"].Value);
            }
        }

        nullable.ExceptWith(nonNullable);

        var failures = new List<string>();
        checkedSites = 0;
        foreach (var file in files)
        {
            foreach (Match key in KeyAssignment().Matches(file.Text))
            {
                var keyExpr = key.Groups["expr"].Value;
                var keys = KeyLiteral().Matches(keyExpr).Select(m => m.Groups[1].Value).Where(templates.ContainsKey).ToList();
                if (keys.Count == 0)
                {
                    continue;
                }

                checkedSites++;
                var block = Block(file.Text, key.Index);
                foreach (Match value in Supplied().Matches(block))
                {
                    var placeholder = value.Groups["key"].Value;
                    var member = value.Groups["member"].Value;
                    if (!nullable.Contains(member) || keyExpr.Contains(member, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var k in keys.Where(k => templates[k].Contains($"{{{placeholder}}}", StringComparison.Ordinal)))
                    {
                        failures.Add(
                            $"{file.Path}:{Line(file.Text, key.Index)}: {k} — {{{placeholder}}} gets «{value.Groups["expr"].Value}» "
                            + $"({member} may be null) but the key does not depend on {member}: a raw {{{placeholder}}} would reach the user.");
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>The dictionary initializer around <paramref name="index"/>: from the previous <c>{</c> to its closing <c>}</c>.</summary>
    private static string Block(string text, int index)
    {
        var open = text.LastIndexOf('{', index);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        return text[open..];
    }

    private static Dictionary<string, string> Templates()
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in SeedTemplate().Matches(SeedSql.Text))
        {
            templates[m.Groups[1].Value] = m.Groups[2].Value;
        }

        Assert.NotEmpty(templates);
        return templates;
    }

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>A member/parameter declaration of a simple type: <c>string? Name</c> (nullable) or <c>string Name</c>.</summary>
    [GeneratedRegex(@"\b(?:string|DateOnly|DateTime|DateTimeOffset|int|long|decimal)(?<q>\?)?\s+(?<name>[A-Z]\w*)\b(?=\s*(?:[,)]|\{|;|=))")]
    private static partial Regex MemberType();

    [GeneratedRegex(@"\[""messageKey""\]\s*=\s*(?<expr>[^,\r\n]+)")]
    private static partial Regex KeyAssignment();

    [GeneratedRegex(@"""(err\.[^""]+)""")]
    private static partial Regex KeyLiteral();

    /// <summary><c>["placeholder"] = ident.Member</c> — the value is the member as is (no fallback).</summary>
    [GeneratedRegex(@"\[""(?<key>\w+)""\]\s*=\s*(?<expr>[A-Za-z_]\w*\.(?<member>[A-Z]\w*))\s*[,}]")]
    private static partial Regex Supplied();

    [GeneratedRegex(@"\(\s*N'(err\.[^']+)'\s*,\s*N'en'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedTemplate();
}
