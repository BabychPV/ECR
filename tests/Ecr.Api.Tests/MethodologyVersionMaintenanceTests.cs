// tests/Ecr.Api.Tests/MethodologyVersionMaintenanceTests.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Покриття, різниця й видалення версії методології справжнім HTTP (<c>BE-25</c>).
/// </summary>
/// <remarks>
/// 409 із доменного винятку дає правило суфікса <c>-0409</c> у
/// <c>ExceptionHandlingMiddleware</c> — обробниковий тест його не бачить.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyVersionMaintenanceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_видаляється_з_усім_вмістом_і_лишає_слід_у_журналі()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, DeleteMethodologyVersionHandler.Permission).ConfigureAwait(true);

        var response = await DeleteAsync(client, stand, stand.DraftId).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {app.ErrorsText}");

        await using var db = Db();
        Assert.False(await db.MethodologyVersions.AnyAsync(v => v.Id == stand.DraftId).ConfigureAwait(true));
        Assert.False(await db.MethodologyFormulas.AnyAsync(f => f.MethodologyVersionId == stand.DraftId).ConfigureAwait(true));
        Assert.True(await db.MethodologyVersions.AnyAsync(v => v.Id == stand.PublishedId).ConfigureAwait(true));

        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = {DeleteMethodologyVersionHandler.DeletedEventType} AND DetailsJson LIKE {"%\"methodologyVersionId\":" + stand.DraftId.ToString(CultureInfo.InvariantCulture) + ",%"}")
            .ToListAsync().ConfigureAwait(true);

        // Формула, константа, вихід і тест — чотири дочірні записи чернетки.
        Assert.Equal(4, JsonDocument.Parse(Assert.Single(events)).RootElement.GetProperty("children").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікована_версія_дає_409_з_причиною_і_лишається()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, DeleteMethodologyVersionHandler.Permission).ConfigureAwait(true);

        var response = await DeleteAsync(client, stand, stand.PublishedId).ConfigureAwait(true);
        var problem = await JsonAsync(response).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("Published", problem.GetProperty("reason").GetString());

        // Заголовок коду спільний для всіх його відмов: правило чотирьох очей
        // над відмовою видалення було б неправдою.
        var title = problem.GetProperty("title").GetString();
        Assert.Equal("Conflicting methodology state", title);
        Assert.DoesNotContain("eyes", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("draft", problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);

        await using var db = Db();
        Assert.True(await db.MethodologyVersions.AnyAsync(v => v.Id == stand.PublishedId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_з_архівним_результатом_розрахунку_дає_409()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        // `arc.CalculationResult` без зовнішнього ключа: саме тут база сама не зупинила б видалення.
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT arc.CalculationResult (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, OutputCode, Value, UnitId)
                VALUES (202001, {-stand.DraftId}, 0, {stand.DraftId}, 0, N'tons', 1, 1)
                """).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, DeleteMethodologyVersionHandler.Permission).ConfigureAwait(true);

        var response = await DeleteAsync(client, stand, stand.DraftId).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("UsedInCalculations", (await JsonAsync(response).ConfigureAwait(true)).GetProperty("reason").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лише_з_правом_перегляду_видалення_403()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, MethodologyCoverageHandler.Permission).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(client, stand, stand.DraftId).ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Покриття_і_різниця_читаються_а_чужа_версія_дає_404()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        var neighbour = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, MethodologyCoverageHandler.Permission).ConfigureAwait(true);

        var coverage = await GetAsync(client, $"{Base(stand)}/versions/{stand.DraftId}/coverage").ConfigureAwait(true);
        var output = Assert.Single(coverage.GetProperty("outputs").EnumerateArray());
        Assert.Equal("tons", output.GetProperty("code").GetString());
        Assert.Equal(stand.ColumnIds[0], Assert.Single(output.GetProperty("bindings").EnumerateArray()).GetProperty("columnDefId").GetInt32());
        Assert.Equal(stand.ColumnIds[1], Assert.Single(coverage.GetProperty("waitingBindings").EnumerateArray()).GetProperty("columnDefId").GetInt32());

        var diff = await GetAsync(client, $"{Base(stand)}/versions/{stand.DraftId}/diff?baseVersionId={stand.PublishedId}").ConfigureAwait(true);
        var item = Assert.Single(diff.GetProperty("items").EnumerateArray());
        Assert.Equal("Changed", item.GetProperty("change").GetString());
        Assert.Equal("@Fuel * 3", item.GetProperty("after").GetString());

        var foreign = await client.GetAsync(new Uri($"{Base(stand)}/versions/{neighbour.DraftId}/coverage", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private sealed record Stand(int MethodologyId, int PublishedId, int DraftId, IReadOnlyList<int> ColumnIds);

    /// <summary>
    /// Методологія: опублікована 1.0 і чернетка 2.0, що різняться одним виразом;
    /// вихід <c>tons</c> прив'язано до першої колонки, друга чекає на <c>ghost</c>.
    /// </summary>
    private async Task<Stand> ArrangeAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = Db();

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        var methodology = new Methodology(
            EcrCode.Create($"MV{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "BE-25" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var published = await VersionAsync(db, methodology.Id, "1.0", "@Fuel * 2", unit).ConfigureAwait(false);
        var draft = await VersionAsync(db, methodology.Id, "2.0", "@Fuel * 3", unit).ConfigureAwait(false);

        published.Publish(publishedByUserId: 2, "first", new DateOnly(2026, 1, 1), testsPassed: true, Now);
        db.CalculationBindings.Add(new CalculationBinding(document.TableDefId, document.ColumnDefIds[0], methodology.Id, "tons", "{}"));
        db.CalculationBindings.Add(new CalculationBinding(document.TableDefId, document.ColumnDefIds[1], methodology.Id, "ghost", "{}"));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(methodology.Id, published.Id, draft.Id, document.ColumnDefIds);
    }

    private static async Task<MethodologyVersion> VersionAsync(
        EcrDbContext db, int methodologyId, string number, string expression, int unit)
    {
        var version = new MethodologyVersion(methodologyId, number, CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyFormulas.Add(version.AddFormula(EcrCode.Create("tons"), expression, FormulaResultType.Number, unit));
        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("k1"), 1m, unit));
        db.MethodologyOutputs.Add(version.AddOutput(EcrCode.Create("tons"), unit, 1));
        db.MethodologyTestCases.Add(version.AddTestCase("case1", "{}", """{"tons":1}""", 0m));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return version;
    }

    private static string Base(Stand stand) => $"/api/v1/methodologies/{stand.MethodologyId}";

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Stand stand, int versionId)
        => client.DeleteAsync(new Uri($"{Base(stand)}/versions/{versionId}", UriKind.Relative));

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response).ConfigureAwait(false);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
