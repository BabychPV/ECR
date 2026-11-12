// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Snapshots.cs
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// V-2 (аудит 2026-11): «Версії» й «Порівняти» через межу переносу проєкту. Зріз подання лишається на аркуші й колонках
/// СТАРОЇ версії (доказ, перенос його не чіпає); читач перекладає Id за кодами на версію документа.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    /// <remarks>
    /// Зрізи №1 (C2 = 41) і №2 (C2 = 42, як живі дані) знято на вихідній версії; потім перенос <c>OnlyLabels</c> (ті самі
    /// коди, інші Id). Без фіксу: (а) зрізи невидимі (старий <c>SheetDefId</c> поза межами поточної версії) — 404 або
    /// нуль змін; (б) кожна непорожня комірка — «нова»; (в) перелік порожній чи зі старим <c>sheetDefId</c>.
    /// Мутація: у <c>CompareDocumentVersionsHandler.LoadAsync</c> повернути <c>SubmissionPayload.Read(found.PayloadJson)</c>
    /// без <c>SubmissionSnapshotTranslation.Cells</c> → червоніють (а) і (б).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.22")]
    public async Task Версії_і_порівняння_після_переносу_перекладаються_за_кодами()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, extraPermission: ListDocumentVersionsHandler.Permission)
            .ConfigureAwait(true);
        var v1 = await AddSourceSnapshotAsync(s, c2: "41", at: new DateTime(2026, 1, 16, 10, 0, 0, DateTimeKind.Utc))
            .ConfigureAwait(true);
        var v2 = await AddSourceSnapshotAsync(s, c2: "42", at: new DateTime(2026, 1, 16, 11, 0, 0, DateTimeKind.Utc))
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var migrate = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(migrate.StatusCode == HttpStatusCode.OK, $"{migrate.StatusCode}: {app.ErrorsText}");

        var id = s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture);

        // (а) Між двома зрізами до переносу — рівно одна зміна C2: 41 → 42.
        var between = await GetJsonAsync(client, app, $"/api/v1/documents/{id}/compare?from={v1}&to={v2}").ConfigureAwait(true);
        var change = Assert.Single(between.GetProperty("changes").EnumerateArray());
        Assert.Equal("41", change.GetProperty("oldValue").GetString());
        Assert.Equal("42", change.GetProperty("newValue").GetString());
        Assert.Equal("C2_" + s.Tag, change.GetProperty("columnCode").GetString());

        // (б) Зріз №2 проти поточного стану (ті самі значення на нових колонках) — змін немає.
        var current = await GetJsonAsync(client, app, $"/api/v1/documents/{id}/compare?from={v2}&to=current").ConfigureAwait(true);
        Assert.Empty(current.GetProperty("changes").EnumerateArray());
        Assert.Empty(current.GetProperty("addedRows").EnumerateArray());
        Assert.Empty(current.GetProperty("removedRows").EnumerateArray());

        // (в) Перелік знає обидва зрізи — на аркуші поточної версії.
        var versions = await GetJsonAsync(
            client, app, $"/api/v1/documents/{id}/versions?periodKey={s.Doc.PeriodKey.Value}").ConfigureAwait(true);
        var listed = versions.EnumerateArray().ToList();
        Assert.Equal([v2, v1], listed.Select(v => v.GetProperty("versionId").GetInt64()).ToList());
        Assert.All(listed, v => Assert.Equal(s.TargetSheetDefId, v.GetProperty("sheetDefId").GetInt32()));
    }

    /// <summary>Зріз подання на ВИХІДНІЙ версії: C1 = «a», C2 = <paramref name="c2"/> першого рядка (Id колонок джерела).</summary>
    private async Task<long> AddSourceSnapshotAsync(Scenario s, string c2, DateTime at)
    {
        var row = s.Doc.RowIds[0];
        var payload = JsonSerializer.Serialize(new object[]
        {
            new { row, column = s.Doc.ColumnDefIds[0], value = "a" },
            new { row, column = s.Doc.ColumnDefIds[1], value = c2 },
        });

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var snapshot = new SubmissionSnapshot(
            s.Doc.DocumentId, s.Doc.SheetDefId, s.Doc.PeriodKey.Value, s.Doc.TemplateVersionId,
            "{}", numericMode: null, calendarMode: null, payload,
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)), at, s.UserId);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return snapshot.Id;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, EcrApiFactory app, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {response.StatusCode}: {body} {app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
