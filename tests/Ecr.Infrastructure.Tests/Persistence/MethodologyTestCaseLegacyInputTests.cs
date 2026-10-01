// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyTestCaseLegacyInputTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Тест золотого набору, записаний до перевірки форми входу (<c>"{}"</c>), читається з
/// порожнім списком аргументів, а не з <c>null</c> (аудит ent3, P2).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати заміну <c>Arguments = []</c> у
/// <see cref="MethodologyStore.GetTestCasesAsync"/> → тест червоний (<c>Arguments</c> — <c>null</c>,
/// і симуляція падала б 500 на <c>input.Arguments.ToDictionary</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyTestCaseLegacyInputTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порожній_вхід_читається_з_порожнім_списком_аргументів()
    {
        var versionId = await SeedVersionAsync("{}");
        await using var db = sql.CreateContext();

        var testCases = await new MethodologyStore(db).GetTestCasesAsync(versionId, CancellationToken.None);

        var testCase = Assert.Single(testCases);
        Assert.NotNull(testCase.Input.Arguments);
        Assert.Empty(testCase.Input.Arguments);
    }

    /// <summary>Чернетка версії з одним тестом, чий вхід записано в обхід перевірки обробника.</summary>
    private async Task<int> SeedVersionAsync(string inputJson)
    {
        await using var db = sql.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"LEG_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Legacy {tag}" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.MethodologyTestCases.Add(version.AddTestCase("t1", inputJson, "{\"tons\":1}", 0m));
        await db.SaveChangesAsync();

        return version.Id;
    }
}
