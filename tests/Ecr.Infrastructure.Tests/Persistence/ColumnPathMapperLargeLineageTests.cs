using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ audit-filters-live D1: родовід видимих колонок документа Land (15 147 Id → 125 263 рядки) — форма запиту й
/// правильність на великому переліку.
/// </summary>
/// <remarks>
/// ⛔ На 7afb19c97 червоний: запит з'єднував <c>OPENJSON(@ids)</c> напряму (оцінка 50 рядків, вкладені цикли) — копія live
/// 1,7 с лише на родовід із 1,3 с усього запиту журналу; тепер Id — у табличну змінну одним розбором, а понад поріг —
/// хеш-з'єднання (0,52 с). Малий перелік (інспектор однієї комірки) — без підказки, пошуками за індексами.
/// </remarks>
[Collection("SqlServer")]
public sealed class ColumnPathMapperLargeLineageTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Великий_перелік_розбирається_один_раз_і_йде_хешем_малий_без_підказки()
    {
        var large = ColumnPathMapper.LineageSqlFor(ColumnPathMapper.HashJoinThreshold + 1);
        var small = ColumnPathMapper.LineageSqlFor(1);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(large, "OPENJSON", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
        Assert.Contains("INSERT INTO @src (Id) SELECT DISTINCT CAST([value] AS int) FROM OPENJSON(@ids)", large, StringComparison.Ordinal);
        Assert.Contains("FROM @src AS j", large, StringComparison.Ordinal);
        Assert.EndsWith("OPTION (HASH JOIN)", large, StringComparison.Ordinal);
        Assert.DoesNotContain("HASH JOIN", small, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Родовід_понад_поріг_дає_той_самий_результат_що_й_малий()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1, ct: CancellationToken.None);
        await using var db = builder.CreateContext();
        var mapper = new ColumnPathMapper(db);

        var small = await mapper.LineageAsync([.. doc.ColumnDefIds], doc.DocumentId, CancellationToken.None);
        int[] many = [.. doc.ColumnDefIds, .. Enumerable.Range(60_000_000, ColumnPathMapper.HashJoinThreshold + 10)];
        var large = await mapper.LineageAsync(many, doc.DocumentId, CancellationToken.None);

        foreach (var id in doc.ColumnDefIds)
        {
            Assert.Equal(small[id], large[id]);
        }

        Assert.Equal(many.Length, large.Count);
    }
}
