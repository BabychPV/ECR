// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Snapshots.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Api.Startup;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        await RefreshMigrationMarkerAsync(app).ConfigureAwait(true);

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

    /// <remarks>
    /// V-3 (аудит 2026-11): аркуш подано (зріз = живі дані), перенос LP-32 перемапив стан на новий <c>SheetDefId</c>, а
    /// зріз лишив на старому. Затвердження без правок нового зрізу не знімає (D-442). Без фіксу
    /// <c>ApprovalSnapshotRefresh</c> не знаходив зрізу на новому аркуші → «аркуш змінено» → другий зріз від імені
    /// погоджувача. Мутація: у <c>RefreshAsync</c> повернути <c>: null</c> замість <c>MigratedSheetPayloadAsync</c> → 2 зрізи.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    public async Task Затвердження_після_переносу_без_правок_не_знімає_нового_зрізу()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, submitted: true, extraPermission: ListDocumentVersionsHandler.Permission)
            .ConfigureAwait(true);

        // Зріз подання — рівно живі дані аркуша (порожня комірка C3 будівника прибрана, щоб «живе» читання й зріз
        // не розходилися лише формою порожнечі — предмет тесту інший).
        long snapshotId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await db.CellValues
                .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId) && c.IsEmpty)
                .ExecuteDeleteAsync().ConfigureAwait(true);
            var cells = await db.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId))
                .ToListAsync().ConfigureAwait(true);
            var payload = SubmissionPayload.Write(cells.Select(c => new CellRecord(
                new CellAddress(s.Doc.PeriodKey, c.TableRowId, c.ColumnDefId),
                c.TableDefId,
                new CellValueData
                {
                    ValueNumeric = c.ValueNumeric,
                    ValueString = c.ValueString,
                    ValueDate = c.ValueDate,
                    ValueBool = c.ValueBool,
                    ValueRegistryEntryId = c.ValueRegistryEntryId,
                    ValueUnitId = c.ValueUnitId,
                })));
            var snapshot = new SubmissionSnapshot(
                s.Doc.DocumentId, s.Doc.SheetDefId, s.Doc.PeriodKey.Value, s.Doc.TemplateVersionId,
                "{}", numericMode: null, calendarMode: null, payload,
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)), new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc), 1);
            db.SubmissionSnapshots.Add(snapshot);
            await db.SaveChangesAsync().ConfigureAwait(true);
            snapshotId = snapshot.Id;
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var migrate = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(migrate.StatusCode == HttpStatusCode.OK, $"{migrate.StatusCode}: {app.ErrorsText}");

        await RefreshMigrationMarkerAsync(app).ConfigureAwait(true);

        using (var approve = await client.PostAsJsonAsync(
                   new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/approve", UriKind.Relative),
                   new { sheetDefId = s.TargetSheetDefId, periodKey = s.Doc.PeriodKey.Value, approved = true })
                   .ConfigureAwait(true))
        {
            var body = await approve.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(approve.StatusCode == HttpStatusCode.NoContent, $"{approve.StatusCode}: {body} {app.ErrorsText}");
        }

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var snapshots = await db.SubmissionSnapshots.AsNoTracking()
                .Where(x => x.DocumentId == s.Doc.DocumentId && x.PeriodKey == s.Doc.PeriodKey.Value)
                .Select(x => x.Id)
                .ToListAsync().ConfigureAwait(true);
            Assert.Equal([snapshotId], snapshots);

            var state = await db.ApprovalStates.AsNoTracking()
                .SingleAsync(a => a.DocumentId == s.Doc.DocumentId).ConfigureAwait(true);
            Assert.Equal(DocumentStatus.Approved, state.Status);
        }
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
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {response.StatusCode}: {body} {app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Після переносу маркер «проєкт переноситься» сходить із кешу фільтра лише з наступним оновленням
    /// (<c>MigratingProjectsRefresher</c>, раз на 2 с): до того будь-який запит до документа — 409
    /// <c>structureChanging</c>. Тест оновлює кеш сам (той самий <c>RefreshAsync</c>, що й фонова служба) —
    /// детерміновано, без очікування за часом.
    /// </summary>
    private static async Task RefreshMigrationMarkerAsync(EcrApiFactory app)
    {
        var refresher = app.Services.GetServices<IHostedService>().OfType<MigratingProjectsRefresher>().Single();
        await refresher.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
