// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.TableRenameHistory.cs
using System.Globalization;
using System.Net;
using Ecr.Application.Audit;
using Ecr.Application.Documents;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B72: історія й журнали таблиці, яку перенос версії ПЕРЕЙМЕНУВАВ мапою <c>tableRenames</c> (OLD → NEW), не обриваються.
/// Окремої схеми для мапи таблиць немає: перейменування таблиці зберігається парами ВСІХ її колонок у <c>cfg.ColumnRename</c>
/// (R-2), а читачі журналів ідуть за ними — родовід колонок (V-4/B73) і родовід таблиць (B102).
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server; період 2099xx, час журналу — відносно годинника. Записи «до переносу»
/// лягають на Id СТАРОЇ таблиці й колонок (журнал незмінний), читання — після переносу, з Id НОВОЇ версії.
/// Мутація (B72-фікс): у <c>CompareDocumentVersionsHandler.LoadAsync</c> повернути синхронний
/// <c>SubmissionSnapshotTranslation.Cells</c> (лише коди) → червоніє крок (6) <c>/compare</c> (кожна комірка NEW —
/// «зміна від порожнього»); кроки (1)–(5) зелені й без фіксу (їх уже покривають R-2, B73, B102).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    /// <summary>Тестовий період далеко від реальних (правило: 2099xx).</summary>
    private const int TableRenameHistoryPeriod = 209907;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Finding", "B72")]
    public async Task B72_Журнали_перейменованої_таблиці_після_переносу_показують_записи_до_нього()
    {
        var s = await ArrangeAsync(Target.TableRenamed, periodKey: TableRenameHistoryPeriod).ConfigureAwait(true);
        await GrantAsync(s, GetCellChangesHandler.Permission, ListDocumentVersionsHandler.Permission).ConfigureAwait(true);

        var oldC2 = s.Doc.ColumnDefIds[1];
        var newC2 = s.TargetColumns["C2"];
        var before = DateTime.UtcNow.AddHours(-2);
        var (rowKey, addedRowKey) = await AuditBeforeTransferAsync(s, before).ConfigureAwait(true);
        var snapshot = await AddSourceSnapshotAsync(s, c2: "42", at: before).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var migrate = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        Assert.True(migrate.StatusCode == HttpStatusCode.OK, $"{migrate.StatusCode}: {app.ErrorsText}");
        await RefreshMigrationMarkerAsync(app).ConfigureAwait(true);

        var id = s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture);
        var window = "from=" + Uri.EscapeDataString(before.AddDays(-1).ToString("o", CultureInfo.InvariantCulture))
                     + "&to=" + Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o", CultureInfo.InvariantCulture));
        var period = TableRenameHistoryPeriod.ToString(CultureInfo.InvariantCulture);

        // (1) Журнал змін комірок документа (/audit/cells): правка до переносу лежить на старій C2, видна через родовід NEW.C2.
        var cells = await GetJsonAsync(client, app, $"/api/v1/audit/cells?{window}&documentId={id}&periodKey={period}")
            .ConfigureAwait(true);
        var listed = cells.GetProperty("items").EnumerateArray().ToList();
        var edit = Assert.Single(listed, i => i.GetProperty("newValue").GetString() == "42");
        Assert.Equal(oldC2, edit.GetProperty("columnDefId").GetInt32());
        Assert.Equal(listed.Count, cells.GetProperty("totalCount").GetInt32());

        // (2) Історія ОДНІЄЇ комірки (інспектор): адреса — NEW.C2 поточної версії.
        var history = await GetJsonAsync(
                client, app,
                $"/api/v1/audit/cells?{window}&documentId={id}&periodKey={period}&rowKey={Uri.EscapeDataString(rowKey)}&columnDefId={newC2}")
            .ConfigureAwait(true);
        Assert.Contains(history.GetProperty("items").EnumerateArray(), i => i.GetProperty("newValue").GetString() == "42");

        // (3) Підсумок журналу — те саме число, що й перелік.
        var summary = await GetJsonAsync(client, app, $"/api/v1/audit/cells/summary?{window}&documentId={id}").ConfigureAwait(true);
        Assert.Equal(listed.Count, summary.GetProperty("total").GetInt64());

        // (4) CSV-експорт журналу — та сама правка.
        using (var csv = await client.GetAsync(
                   new Uri($"/api/v1/audit/cells/export.csv?{window}&documentId={id}&periodKey={period}", UriKind.Relative))
                   .ConfigureAwait(true))
        {
            var text = await csv.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(csv.StatusCode == HttpStatusCode.OK, $"{csv.StatusCode}: {text} {app.ErrorsText}");
            Assert.Contains(
                text.Split('\n'),
                line => line.Contains("," + oldC2.ToString(CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal)
                        && line.Contains(",42,", StringComparison.Ordinal));
        }

        // (5) Журнал дій аркуша (панель History): і правка значення, і «додано рядок» (aud.RowChange на OLD TableDefId).
        var activity = await GetJsonAsync(
                client, app, $"/api/v1/documents/{id}/sheets/{s.TargetSheetDefId}/activity?periodKey={period}")
            .ConfigureAwait(true);
        var items = activity.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(
            items,
            i => i.GetProperty("kind").GetString() == SheetActivityKinds.CellEdit && i.GetProperty("newValue").GetString() == "42");
        Assert.Contains(
            items,
            i => i.GetProperty("kind").GetString() == SheetActivityKinds.RowChange
                 && i.GetProperty("rowKey").GetString() == addedRowKey
                 && i.GetProperty("action").GetString() == SheetActivityKinds.RowAdded);

        // (6) Порівняння версій: зріз до переносу (колонки OLD) проти поточного стану (ті самі значення в NEW) — змін немає.
        var compare = await GetJsonAsync(client, app, $"/api/v1/documents/{id}/compare?from={snapshot}&to=current")
            .ConfigureAwait(true);
        Assert.Empty(compare.GetProperty("changes").EnumerateArray());
        Assert.Empty(compare.GetProperty("addedRows").EnumerateArray());
        Assert.Empty(compare.GetProperty("removedRows").EnumerateArray());
    }

    /// <summary>Додає ролі користувача сценарію ще права (журнал аудиту, версії документа).</summary>
    private async Task GrantAsync(Scenario s, params string[] permissions)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var roleId = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.UserId == s.UserId).Select(a => a.RoleId).SingleAsync().ConfigureAwait(false);
        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(roleId, permission));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Журнал до переносу — на Id СТАРОЇ версії: правка C2 першого рядка (→ 42) і «додано рядок» другого рядка таблиці OLD.
    /// </summary>
    /// <returns>Ключі першого (правка) і другого (додано) рядків.</returns>
    private async Task<(string RowKey, string AddedRowKey)> AuditBeforeTransferAsync(Scenario s, DateTime at)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var keys = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.RowKeyValue).ConfigureAwait(false);
        var rowKey = keys[s.Doc.RowIds[0]];
        var addedRowKey = keys[s.Doc.RowIds[1]];

        var periodKey = s.Doc.PeriodKey.Value;
        var documentId = s.Doc.DocumentId;
        var row = s.Doc.RowIds[0];
        var added = s.Doc.RowIds[1];
        var column = s.Doc.ColumnDefIds[1];
        var table = s.Doc.TableDefId;
        var instance = s.Doc.TableInstanceId;
        var user = s.UserId;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                        ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow)
            VALUES ({at}, {periodKey}, {documentId}, {row}, {rowKey}, {column}, NULL, N'42', {user}, N'UserEdit', 0, 0)
            """).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.RowChange (ChangedAt, DocumentId, PeriodKey, TableInstanceId, TableDefId, TableRowId, RowKey, Action,
                                       ChangedByUserId, Origin, CorrelationId)
            VALUES ({at.AddMinutes(1)}, {documentId}, {periodKey}, {instance}, {table}, {added}, {addedRowKey}, 1,
                    {user}, N'UserEdit', NULL)
            """).ConfigureAwait(false);

        return (rowKey, addedRowKey);
    }
}
