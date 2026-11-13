// tests/Ecr.Api.Tests/PermitBindingPreviewReadScopeTests.MonthOwner.cs
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ⛔ B44 (після S-1): перегляд прив'язки дозволу для таблиці «місяці в колонках» (Q-17) накладає факт колонки з ІНШИМ
/// власником лише тоді, коли його показує зріз — область ролі читача мусить містити ПЕРІОД ВЛАСНИКА
/// (<c>GetTableSliceHandler.OwnerBlockedColumnsAsync</c>), а не лише період документа.
/// </summary>
/// <remarks>
/// Документ 202610 (якір року — календар 202610, 202611), колонка M11 належить екземпляру 202611: там 111, у самому
/// екземплярі 202610 — фізичне 11 (видно лише без маршрутизації). Той самий запит перегляду; відрізняється лише область
/// ролі читача (202610..202611 або лише 202610) і прапор <c>Collection:MonthColumnRouting</c>. Доказ без мутації доступу:
/// до фіксу читач лише з 202610 бачив у перегляді 111 з 202611, якого зріз йому не показує.
/// </remarks>
public sealed partial class PermitBindingPreviewReadScopeTests
{
    private const string MonthRoutingFlag = "ECR_Collection__MonthColumnRouting";
    private const int Oct = 202610;
    private const int Nov = 202611;
    private const decimal OwnerFact = 111m;
    private const decimal PhysicalFact = 11m;

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task B44_Місячна_колонка_чужого_власника_у_перегляді_лише_коли_область_ролі_містить_період_власника()
    {
        using var app = BootApp(routing: true);
        var stand = await ArrangeMonthAsync(app).ConfigureAwait(true);

        // Контроль: область 202610..202611 — зріз показує M11 власника 202611, перегляд теж.
        using var both = await MonthReaderAsync(app, stand, from: Oct, to: Nov).ConfigureAwait(true);
        Assert.Equal(OwnerFact, Number(await SliceCellAsync(both, stand).ConfigureAwait(true)));
        Assert.Equal(OwnerFact, Number(Cell(await PreviewAsync(both, app, stand).ConfigureAwait(true), stand.FactCode)));

        // Лише 202610: зріз маскує M11 (власник 202611 поза областю) — перегляд мусить так само.
        using var octOnly = await MonthReaderAsync(app, stand, from: Oct, to: Oct).ConfigureAwait(true);
        Assert.Null(await SliceCellAsync(octOnly, stand).ConfigureAwait(true));
        var row = await PreviewAsync(octOnly, app, stand).ConfigureAwait(true);
        Assert.Equal(Limit, Number(Cell(row, stand.LimitCode)));
        Assert.True(Cell(row, stand.FactCode) is null, $"факт місяця-власника поза областю ролі потрапив у перегляд: {row}");
        Assert.DoesNotContain("111", row.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Контроль «маршрутизацію вимкнено»: поведінка як до Q-17 (фізичне значення екземпляра документа, власника не питають)
    /// і жодного звернення до бази понад ту саму таблицю без місячної розкладки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task B44_Маршрутизацію_вимкнено_перегляд_як_раніше_і_без_зайвих_запитів()
    {
        using var app = BootApp(routing: false);
        var plain = await ArrangeAsync(app).ConfigureAwait(true);
        var stand = await ArrangeMonthAsync(app, permitRowEntryId: long.Parse(plain.RowKey[1..], System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(true);

        using var octOnly = await MonthReaderAsync(app, stand, from: Oct, to: Oct).ConfigureAwait(true);
        Assert.Equal(PhysicalFact, Number(await SliceCellAsync(octOnly, stand).ConfigureAwait(true)));
        var row = await PreviewAsync(octOnly, app, stand).ConfigureAwait(true);
        Assert.Equal(PhysicalFact, Number(Cell(row, stand.FactCode)));
        Assert.DoesNotContain("111", row.GetRawText(), StringComparison.Ordinal);

        // Число звернень перегляду: таблиця «місяці в колонках» без прапора = звичайна таблиця (S-1) з тим самим читачем.
        var month = await MeasurePreviewAsync(app, stand, await MonthReaderAsync(app, stand, from: Oct, to: Oct).ConfigureAwait(true)).ConfigureAwait(true);
        var flat = await MeasurePreviewAsync(app, plain, await MonthReaderAsync(app, plain, from: plain.Doc.PeriodKey.Value, to: plain.Doc.PeriodKey.Value).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(month.Total == flat.Total, $"без прапора місячна таблиця: {month.Total}, звичайна: {flat.Total}\n{month.Dump}\n---\n{flat.Dump}");
    }

    private EcrApiFactory BootApp(bool routing)
    {
        Environment.SetEnvironmentVariable(MonthRoutingFlag, routing ? "true" : "false");
        try
        {
            var app = new EcrApiFactory(sql);
            _ = app.Services;   // хост (і конфігурація роутера) збирається ТУТ, поки змінна середовища діє
            app.Server.PreserveExecutionContext = true;   // мітка заміру (AsyncLocal) доходить до потоку запиту
            return app;
        }
        finally
        {
            Environment.SetEnvironmentVariable(MonthRoutingFlag, null);
        }
    }

    private static async Task<(int Total, string Dump)> MeasurePreviewAsync(EcrApiFactory app, Stand stand, HttpClient reader)
    {
        using (reader)
        {
            _ = await PreviewAsync(reader, app, stand).ConfigureAwait(true);   // розігрів кешів

            var texts = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
            {
                if (Measuring.Value is not { Value: true })
                {
                    return false;
                }

                texts.Enqueue(command.CommandText ?? string.Empty);
                return true;
            });

            counter.Tally.Reset();
            var flag = new StrongBox<bool>(true);
            Measuring.Value = flag;
            try
            {
                _ = await PreviewAsync(reader, app, stand).ConfigureAwait(true);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            counter.AssertObserved();
            var dump = string.Join('\n', texts.Select((t, i) =>
            {
                var flat = string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return $"{i + 1,2}. {(flat.Length > 140 ? flat[..140] : flat)}";
            }));
            return (counter.Tally.Snapshot().Total, dump);
        }
    }

    /// <summary>Значення колонки факту в рядку дозволу зрізу; <c>null</c> — колонку замасковано.</summary>
    private static async Task<JsonElement?> SliceCellAsync(HttpClient client, Stand stand)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{stand.Doc.DocumentId}/tables/{stand.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        var row = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == stand.RowKey);
        return Cell(row.Clone(), stand.FactCode);
    }

    /// <summary>
    /// Документ 202610 з таблицею «місяці в колонках» (динамічні рядки), календар 202610 і 202611 (обидва Open), прив'язка
    /// <c>Rows</c> як у S-1; рядок дозволу є в обох екземплярах; M11: 111 у 202611 (власник), 11 у 202610 (фізичне).
    /// </summary>
    /// <param name="app">Застосунок.</param>
    /// <param name="permitRowEntryId">Рядок дозволу, уже засіяний <see cref="ArrangeAsync"/> цього ж тесту; <c>null</c> — випустити тип і засіяти.</param>
    private async Task<Stand> ArrangeMonthAsync(EcrApiFactory app, long? permitRowEntryId = null)
    {
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(false);
        if (permitRowEntryId is null)
        {
            await PublishTypeAsync(admin).ConfigureAwait(false);
        }

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Oct, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);
        await builder.OpenPeriodAsync(doc).ConfigureAwait(false);

        await using (var db = Context())
        {
            var limit = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]).ConfigureAwait(false);
            limit.SetReadOnly(true);
            limit.SetNumericFormat(12, 3);
            db.HeaderFieldDefs.Add(new HeaderFieldDef(
                doc.TemplateVersionId, EcrCode.Create("Facility"), Name("Facility"), 1, CellDataType.String));
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var binding = $"M{_tag}";
        var put = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{doc.TemplateVersionId}/permit-bindings/{binding}", UriKind.Relative),
            new
            {
                sheetTableDefId = doc.TableDefId,
                permitTypeCode = $"AIR{_tag}",
                permitTableCode = "Emissions",
                mode = "Rows",
                selectionRule = "ByFacilityPeriodEnd",
                headerFieldDefId = (int?)null,
                isActive = true,
                columns = new object[]
                {
                    new { role = "RowKey", permitFieldCode = "Substance", sheetColumnDefId = doc.ColumnDefIds[0] },
                    new { role = "Limit", permitFieldCode = "Limit", sheetColumnDefId = doc.ColumnDefIds[1] },
                },
            }).ConfigureAwait(false);
        Assert.True(put.StatusCode == HttpStatusCode.OK, $"{put.StatusCode}: {await put.Content.ReadAsStringAsync().ConfigureAwait(false)}");

        var rowEntryId = permitRowEntryId ?? await SeedPermitAsync().ConfigureAwait(false);
        var rowKey = $"P{rowEntryId}";
        var rowId = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", 1, default).ConfigureAwait(false);
        await using (var db = Context())
        {
            db.TableRows.Add(new TableRow(doc.PeriodKey, rowId, doc.TableInstanceId, RowKey.Create(rowKey), 1,
                new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // Екземпляр 202611 з тим самим рядком (RowKey) — власник місячної колонки M11.
        var november = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov)).ConfigureAwait(false);
        var m11 = doc.MonthColumnDefIds[10];
        await using (var db = Context())
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Nov).ConfigureAwait(false);
            period.AdvanceTo(PeriodState.Open, new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc));

            db.CellValues.Add(new CellValue(
                new CellAddress(new PeriodKey(Nov), november.RowIds[november.RowKeys.ToList().IndexOf(rowKey)], m11), doc.TableDefId,
                new CellValueData { ValueNumeric = OwnerFact }));
            db.CellValues.Add(new CellValue(
                new CellAddress(doc.PeriodKey, rowId, m11), doc.TableDefId, new CellValueData { ValueNumeric = PhysicalFact }));
            await db.SaveChangesAsync().ConfigureAwait(false);

            var codes = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id, c => c.Code).ConfigureAwait(false);
            return new Stand(doc, binding, rowKey, codes[doc.ColumnDefIds[1]], codes[m11]);
        }
    }

    /// <summary>
    /// Читач: <c>Template.View</c>/<c>Registry.View</c> окремою роллю; <c>Document.View</c> і грант <c>Read</c> на проєкт —
    /// у ролі з областю періодів <paramref name="from"/>..<paramref name="to"/> (D-214).
    /// </summary>
    private async Task<HttpClient> MonthReaderAsync(EcrApiFactory app, Stand stand, int from, int to)
    {
        var name = $"b44_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var catalog = new Role(EcrCode.Create($"C{Guid.NewGuid():N}"[..12]), Name("B44 catalog"));
            var narrowed = new Role(EcrCode.Create($"N{Guid.NewGuid():N}"[..12]), Name("B44 narrowed"));
            db.Roles.AddRange(catalog, narrowed);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(catalog.Id, "Template.View"));
            db.RolePermissions.Add(new RolePermission(catalog.Id, "Registry.View"));
            db.RolePermissions.Add(new RolePermission(narrowed.Id, "Document.View"));
            db.ResourceGrants.Add(new ResourceGrant(narrowed.Id, ResourceKind.Project, stand.Doc.ProjectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(catalog.Id, user.Id, principalSid: null));
            var assignment = new RoleAssignment(narrowed.Id, user.Id, principalSid: null);
            assignment.SetScope(RoleAssignmentScope.Create(
                [stand.Doc.ProjectId], periodFrom: new PeriodKey(from), periodTo: new PeriodKey(to)));
            db.RoleAssignments.Add(assignment);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return await LoginAsync(app, name).ConfigureAwait(false);
    }
}
