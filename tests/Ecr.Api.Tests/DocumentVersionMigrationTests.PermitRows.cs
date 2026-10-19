// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.PermitRows.cs
using System.Net;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// PM-10c: перенос документа на нову версію шаблону не губить рядки з дозволу (<c>P&lt;id&gt;</c>, <c>RowDefId = null</c>,
/// останнє значення комірок - від дозволу).
/// </summary>
/// <remarks>
/// Характеризаційний тест поточної поведінки: перенос лишає рядки без <c>RowDefId</c> на місці й переносить їхні комірки
/// на колонки цільової версії за кодом. Оновлення наприкінці доводить, що походження <c>Permit</c> пережило перенос
/// (інакше ліміт другого рядка вважався б чужою правкою й не оновився).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PM-10c")]
    public async Task Рядки_з_дозволу_P_id_переживають_перенос_версії_з_ключами_комірками_і_походженням_Permit_а_оновлення_не_дублює_їх()
    {
        var s = await ArrangeAsync(Target.KeepsMixedMode).ConfigureAwait(true);
        var key = s.Doc.PeriodKey;
        var now = new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);
        var permitKeys = new[] { "P880001", "P880002" };
        var substances = new[] { "NOx", "SO2" };
        var limits = new[] { 12.5m, 3m };

        // Документ на вихідній версії: дві рядки шаблону (R1, R2) і два рядки з дозволу, як їх лишає PM-10.
        long firstRowId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            firstRowId = await new BulkCellLoader(sql.ConnectionString, 1000)
                .ReserveIdsAsync("doc.TableRowSeq", permitKeys.Length, default).ConfigureAwait(true);
            for (var i = 0; i < permitKeys.Length; i++)
            {
                db.TableRows.Add(new TableRow(key, firstRowId + i, s.Doc.TableInstanceId, RowKey.Create(permitKeys[i]), 3 + i, now));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);

            for (var i = 0; i < permitKeys.Length; i++)
            {
                db.CellValues.Add(new CellValue(
                    new CellAddress(key, firstRowId + i, s.Doc.ColumnDefIds[0]), s.Doc.TableDefId, new CellValueData { ValueString = substances[i] }));
                db.CellValues.Add(new CellValue(
                    new CellAddress(key, firstRowId + i, s.Doc.ColumnDefIds[1]), s.Doc.TableDefId, new CellValueData { ValueNumeric = limits[i] }));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);

            var documentId = s.Doc.DocumentId;
            var periodKey = key.Value;
            foreach (var (rowKey, rowId, column, oldValue, newValue) in AuditedCells(s, permitKeys, firstRowId, substances, limits))
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow)
                    VALUES ({now}, {periodKey}, {documentId}, {rowId}, {rowKey}, {column}, {oldValue}, {newValue}, 0, N'Permit', 0, 0)
                    """).ConfigureAwait(true);
            }
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var migrated = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(migrated.StatusCode == HttpStatusCode.OK, await migrated.Content.ReadAsStringAsync().ConfigureAwait(true));

        // Рядки: ті самі Id і ключі, RowDefId порожній, не видалені, у тому самому екземплярі.
        await using (var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var rows = await check.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == key.Value && r.TableInstanceId == s.Doc.TableInstanceId && EF.Functions.Like(r.RowKeyValue, "P%"))
                .OrderBy(r => r.Id).Select(r => new { r.Id, r.RowKeyValue, r.RowDefId, r.IsDeleted }).ToListAsync().ConfigureAwait(true);
            Assert.Equal(
                [(firstRowId, "P880001", (int?)null, false), (firstRowId + 1, "P880002", (int?)null, false)],
                rows.Select(r => (r.Id, r.RowKeyValue, r.RowDefId, r.IsDeleted)));

            // Комірки: значення збережені, колонки вже цільової версії.
            var cells = await check.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == key.Value && (c.TableRowId == firstRowId || c.TableRowId == firstRowId + 1))
                .OrderBy(c => c.TableRowId).ThenBy(c => c.ColumnDefId).ToListAsync().ConfigureAwait(true);
            Assert.Equal(4, cells.Count);
            Assert.All(cells, c => Assert.Equal(s.TargetTableDefId, c.TableDefId));
            Assert.Equal(
                [substances[0], null, substances[1], null],
                cells.Select(c => c.ColumnDefId == s.TargetColumns["C1"] ? c.ValueString : null));
            Assert.Equal(
                [limits[0], limits[1]],
                cells.Where(c => c.ColumnDefId == s.TargetColumns["C2"]).Select(c => c.ValueNumeric));

            // Походження змін лишилося Permit (журнал перенос не переписує).
            var permitAudit = await check.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM aud.CellChange WHERE DocumentId = {s.Doc.DocumentId} AND Origin = N'Permit'").SingleAsync().ConfigureAwait(true);
            Assert.Equal(4, permitAudit);
        }

        // Оновлення з дозволу після переносу: ключі P880001/2 уже є - дублів немає, додається лише P880003; ліміт другого
        // рядка (остання зміна - від дозволу) оновлюється, тобто походження Permit справді пережило перенос.
        var result = await RefreshAfterMigrationAsync(s, now).ConfigureAwait(true);
        Assert.Equal((1, 1), (result.Added, result.Updated));
        Assert.Empty(result.RemovedFromPermit);
        Assert.DoesNotContain(result.Warnings, w => w.Case == "cellNotFromPermit");

        await using var final = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var keys = await final.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == key.Value && r.TableInstanceId == s.Doc.TableInstanceId && !r.IsDeleted)
            .Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["P880001", "P880002", "P880003"], keys.Where(k => k.StartsWith('P')).Order(StringComparer.Ordinal));
        Assert.Equal(
            99m,
            (await final.CellValues.AsNoTracking()
                .SingleAsync(c => c.PeriodKeyValue == key.Value && c.TableRowId == firstRowId + 1 && c.ColumnDefId == s.TargetColumns["C2"])
                .ConfigureAwait(true)).ValueNumeric);
    }

    private static IEnumerable<(string RowKey, long RowId, int Column, string? Old, string New)> AuditedCells(
        Scenario s, string[] keys, long firstRowId, string[] substances, decimal[] limits)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            yield return (keys[i], firstRowId + i, s.Doc.ColumnDefIds[0], null, substances[i]);
            yield return (keys[i], firstRowId + i, s.Doc.ColumnDefIds[1], null, limits[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Оновлення з дозволу на цільовій версії: рядки P880001/2 (як були) і новий P880003; ліміт P880002 став 99.</summary>
    private async Task<PermitRefreshResult> RefreshAfterMigrationAsync(Scenario s, DateTime now)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == s.TargetTableDefId)
            .ToDictionaryAsync(c => c.Id, c => c.Code).ConfigureAwait(false);
        var keyColumn = s.TargetColumns["C1"];
        var limitColumn = s.TargetColumns["C2"];

        var binding = new PermitSheetBinding(
            EcrCode.Create("PMC" + s.Tag), s.TargetVersionId, s.TargetTableDefId, 1, 1,
            PermitBindingMode.Rows, PermitSelectionRule.ByFacilityPeriodEnd, null);
        PermitSheetBindingColumn[] columns =
        [
            new(PermitColumnRole.RowKey, EcrCode.Create("Substance"), keyColumn, 1),
            new(PermitColumnRole.Limit, EcrCode.Create("Limit"), limitColumn, 2),
        ];
        foreach (var column in columns)
        {
            binding.AddColumn(column);
        }

        var view = new PermitBindingView(
            binding, columns, s.TargetTableCode, "PMC", "Emissions", null,
            new Dictionary<int, string> { [keyColumn] = codes[keyColumn], [limitColumn] = codes[limitColumn] });

        PlannedRow Row(string rowKey, long entryId, string substance, decimal limit)
            => new(
                rowKey, entryId,
                new Dictionary<string, object?> { [codes[keyColumn]] = substance, [codes[limitColumn]] = limit },
                [codes[keyColumn], codes[limitColumn]]);

        var plan = new PermitBindingPlan(
            new PermitResolution(1, "N1", null, null, null, []),
            new PermitRowPlan([Row("P880001", 880001, "NOx", 12.5m), Row("P880002", 880002, "SO2", 99m), Row("P880003", 880003, "CO", 7m)], []),
            []);

        var service = new PermitBindingService(
            db, new BulkCellLoader(sql.ConnectionString, 1000), Substitute.For<IPermitBindingStore>(), Substitute.For<IPermitTypeStore>(),
            Substitute.For<IPermitResolver>(), Substitute.For<IRegistrySnapshotLoader>(), Substitute.For<IDocumentHeaderStore>(),
            Substitute.For<IRegistryStore>(), Substitute.For<IPeriodStore>(), new NormalizedCellStore(db), new AuditWriter(db),
            new TestClock(now));

        return await service
            .RefreshAsync(s.Doc.DocumentId, s.Doc.PeriodKey, s.Doc.TableInstanceId, view, plan, default)
            .ConfigureAwait(false);
    }
}
