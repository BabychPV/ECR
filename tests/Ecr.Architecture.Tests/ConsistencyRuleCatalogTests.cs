// tests/Ecr.Architecture.Tests/ConsistencyRuleCatalogTests.cs
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ 24.11 (аудит «Consistency issues»): правило нічної перевірки — це код у трьох місцях, які мають збігатися: перелік
/// задачі (<c>ConsistencyCheckJob.KnownRules</c>, ним сервер пише <c>checks</c> прогону), перелік клієнта
/// (<c>consistencyRules.ts</c> — фільтр і назви) і назва правила в каталозі (<c>consistency.rule.&lt;CODE&gt;</c>, en/ru/kz).
/// </summary>
/// <remarks>
/// Мутація: прибрати код із <c>consistencyRules.ts</c> або рядок <c>consistency.rule.*</c> із сіду — червоне.
/// </remarks>
public sealed class ConsistencyRuleCatalogTests
{
    private static readonly string[] Languages = ["en", "ru", "kz"];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-7.7")]
    public void Правила_задачі_клієнта_й_каталогу_збігаються()
    {
        var client = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Web", "src", "features", "consistency", "consistencyRules.ts"));
        var listed = Regex.Match(client, @"ConsistencyRuleCodes = \[(?<body>[^\]]*)\]", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Groups["body"].Value;
        var clientCodes = Regex.Matches(listed, "'([A-Z_]+)'", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(ConsistencyCheckJob.KnownRules, clientCodes);

        var seed = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "seed"),
                    "*.sql",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var missing = ConsistencyCheckJob.KnownRules
            .SelectMany(code => Languages.Select(lang => (code, lang)))
            .Where(p => !seed.Contains($"(N'consistency.rule.{p.code}', N'{p.lang}',", StringComparison.Ordinal))
            .Select(p => $"consistency.rule.{p.code} [{p.lang}]")
            .ToList();

        Assert.True(missing.Count == 0, "Немає назви правила в сіді: " + string.Join(", ", missing));
    }
}
