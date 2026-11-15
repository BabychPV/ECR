// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.RenameScope.cs
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Audit;
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B127: перейменування колонок (<c>cfg.ColumnRename</c>) спільні для шаблону, а перенос — на проєкт. Родовід документа
/// бере лише пари, застосовані переносом ЙОГО проєкту (<c>cfg.ColumnRenameProject</c>), і застарілі глобальні (записані
/// до B127). Сценарій: проєкт A при переносі OLD → NEW відкинув колонку <c>b</c> (без значень), а пару <c>b → z</c> має
/// ІНШИЙ проєкт тих самих версій шаблону.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server; періоди 2099xx, час журналу — відносно годинника. Пара <c>b → z</c>
/// додається після переносу A з одним із трьох власників: сам A («свій» — як у проєкту B, що її застосував), інший
/// проєкт («чужий»), без власника з <c>IsLegacyGlobal = 1</c> («застаріла»). Журнал (правка <c>b</c> = 77 до переносу)
/// і порівняння (зріз до переносу з <c>b</c> = 77 проти поточного, де <c>z</c> порожня) бачать <c>b</c> як <c>z</c> лише
/// для «своєї» і «застарілої». Мутації: прибрати <c>RenameInScope</c> з <c>LineageSql</c> або <c>ProjectOfAsync</c> → null
/// — червоніє «чужа»; прибрати <c>IsLegacyGlobal = 1</c> з предиката — червоніє «застаріла»; прибрати INSERT
/// <c>cfg.ColumnRenameProject</c> у <c>FinishSql</c> — червоніють усі (пари переносу A без прив'язки).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    /// <summary>Хто застосував пару <c>b → z</c>.</summary>
    public enum RenameOwner
    {
        /// <summary>Проєкт документа.</summary>
        Own,

        /// <summary>Інший проєкт тих самих версій.</summary>
        Foreign,

        /// <summary>Невідомо: рядок до B127 (<c>IsLegacyGlobal = 1</c>).</summary>
        Legacy,
    }

    [Theory]
    [InlineData(RenameOwner.Own, 0, true)]
    [InlineData(RenameOwner.Foreign, 1, false)]
    [InlineData(RenameOwner.Legacy, 2, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Finding", "B127")]
    public async Task B127_Журнал_і_порівняння_беруть_лише_перейменування_свого_проєкту(
        RenameOwner owner, int periodOffset, bool expectZ)
    {
        var period = 209910 + periodOffset;
        var s = await ArrangeAsync(
                Target.TableRenamedDropsC4AddsZ, periodKey: period, extraPermission: ListDocumentVersionsHandler.Permission)
            .ConfigureAwait(true);
        await GrantAsync(s, GetCellChangesHandler.Permission).ConfigureAwait(true);

        // b — порожня C4 таблиці OLD (перенос A її відкидає без втрат), z — нова колонка таблиці NEW.
        var b = s.Doc.ColumnDefIds[3];
        var z = s.TargetColumns["Z"];

        var before = DateTime.UtcNow.AddHours(-2);
        var (rowKey, _) = await AuditBeforeTransferAsync(s, before).ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var documentId = s.Doc.DocumentId;
            var row = s.Doc.RowIds[0];
            var at = before.AddMinutes(2);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                            ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow)
                VALUES ({at}, {period}, {documentId}, {row}, {rowKey}, {b}, NULL, N'77', {s.UserId}, N'UserEdit', 0, 0)
                """).ConfigureAwait(true);
        }

        var snapshot = await AddSnapshotWithBAsync(s, b, before).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var migrate = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        Assert.True(migrate.StatusCode == HttpStatusCode.OK, $"{migrate.StatusCode}: {app.ErrorsText}");
        await RefreshMigrationMarkerAsync(app).ConfigureAwait(true);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // Запис переносу: пари колонок таблиці OLD → NEW прив'язані до проєкту A; пари для b перенос A не писав.
            var linked = await db.ColumnRenameProjects.AsNoTracking()
                .Where(p => p.ProjectId == s.Doc.ProjectId).Select(p => p.FromColumnDefId).ToListAsync().ConfigureAwait(true);
            Assert.Contains(s.Doc.ColumnDefIds[1], linked);
            Assert.DoesNotContain(b, linked);

            db.ColumnRenames.Add(new ColumnRename(b, z));
            if (owner != RenameOwner.Legacy)
            {
                var project = owner == RenameOwner.Own
                    ? s.Doc.ProjectId
                    : (await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(period, columnCount: 1, rowCount: 1)
                        .ConfigureAwait(true)).ProjectId;
                db.ColumnRenameProjects.Add(new ColumnRenameProject(b, z, project));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
            if (owner == RenameOwner.Legacy)
            {
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE cfg.ColumnRename SET IsLegacyGlobal = 1 WHERE FromColumnDefId = {b} AND ToColumnDefId = {z}")
                    .ConfigureAwait(true);
            }
        }

        var id = s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture);
        var window = "from=" + Uri.EscapeDataString(before.AddDays(-1).ToString("o", CultureInfo.InvariantCulture))
                     + "&to=" + Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o", CultureInfo.InvariantCulture));
        var key = period.ToString(CultureInfo.InvariantCulture);

        // Журнал документа і журнал аркуша: правка b = 77 до переносу — лише якщо b → z у родоводі проєкту.
        var cells = await GetJsonAsync(client, app, $"/api/v1/audit/cells?{window}&documentId={id}&periodKey={key}")
            .ConfigureAwait(true);
        Assert.Equal(expectZ, cells.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("newValue").GetString() == "77"));
        Assert.Contains(cells.GetProperty("items").EnumerateArray(), i => i.GetProperty("newValue").GetString() == "42");

        // Історія ОДНІЄЇ комірки z (інспектор): родовід лише цієї колонки — той самий межовий випадок (M10).
        var history = await GetJsonAsync(
                client, app,
                $"/api/v1/audit/cells?{window}&documentId={id}&periodKey={key}&rowKey={Uri.EscapeDataString(rowKey)}&columnDefId={z}")
            .ConfigureAwait(true);
        Assert.Equal(expectZ, history.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("newValue").GetString() == "77"));

        var activity = await GetJsonAsync(client, app, $"/api/v1/documents/{id}/sheets/{s.TargetSheetDefId}/activity?periodKey={key}")
            .ConfigureAwait(true);
        Assert.Equal(
            expectZ,
            activity.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("kind").GetString() == SheetActivityKinds.CellEdit
                                                                    && i.GetProperty("newValue").GetString() == "77"));

        // Порівняння: зріз до переносу (b = 77) проти поточного (z порожня) — зміна z лише для свого/застарілого.
        var compare = await GetJsonAsync(client, app, $"/api/v1/documents/{id}/compare?from={snapshot}&to=current")
            .ConfigureAwait(true);
        var changes = compare.GetProperty("changes").EnumerateArray().ToList();
        if (expectZ)
        {
            var change = Assert.Single(changes);
            Assert.Equal($"Z_{s.Tag}", change.GetProperty("columnCode").GetString());
            Assert.Equal("77", change.GetProperty("oldValue").GetString());
        }
        else
        {
            Assert.Empty(changes);
        }
    }

    /// <summary>Зріз подання на ВИХІДНІЙ версії: C1 = «a», C2 = 42 (як живі дані) і b = 77 першого рядка.</summary>
    private async Task<long> AddSnapshotWithBAsync(Scenario s, int b, DateTime at)
    {
        var row = s.Doc.RowIds[0];
        var payload = JsonSerializer.Serialize(new object[]
        {
            new { row, column = s.Doc.ColumnDefIds[0], value = "a" },
            new { row, column = s.Doc.ColumnDefIds[1], value = "42" },
            new { row, column = b, value = "77" },
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
}
