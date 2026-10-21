// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.LookupRemap.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Мапа записів Lookup шапки при переносі версії (<c>lookupRemaps</c>): значення шапки переїздить на запис нового
/// довідника в тій самій транзакції, що й версія; неповна мапа, непридатна ціль чи чужий <c>from</c> нічого не пишуть.
/// </summary>
/// <remarks>
/// Мутаційні докази (червоніють названі тести): прибрати <c>ValueRegistryEntryId</c> з <c>HeaderSqlWithLookupRemap</c> —
/// «Повна мапа…»; прибрати перевірку <c>UnusableTargets</c> у планувальнику або <c>NotUsableReason</c> у обробнику —
/// «Ціль…»; прибрати перевірку приналежності <c>from</c> старому довіднику — «Чужий from…»; прибрати відмову
/// <c>LookupUnmappedValues</c> — «Неповна мапа…».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private sealed record LookupSetup(
        Scenario S, string FieldCode, int OldField, int NewField, long OldEntry, long OtherOldEntry,
        long GoodNew, long InactiveNew, long DeletedNew, long ExpiredNew, long ForeignNew);

    private async Task<LookupSetup> ArrangeLookupAsync(bool submitted = false, bool approved = false)
    {
        var s = await ArrangeAsync(Target.OnlyLabels, submitted: submitted, approved: approved).ConfigureAwait(false);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        RegistryDef Registry(string name)
        {
            var def = new RegistryDef(
                EcrCode.Create($"{name}_{Guid.NewGuid():N}"[..20]), Name(name), isTemporal: false);
            db.RegistryDefs.Add(def);
            return def;
        }

        var oldRegistry = Registry("OLDR");
        var newRegistry = Registry("NEWR");
        var foreignRegistry = Registry("FORR");
        await db.SaveChangesAsync().ConfigureAwait(false);

        RegistryEntry Entry(RegistryDef registry, string code)
        {
            var entry = new RegistryEntry(registry.Id, EcrCode.Create(code), Name(code));
            db.RegistryEntries.Add(entry);
            return entry;
        }

        var oldEntry = Entry(oldRegistry, "OLD1");
        var otherOld = Entry(oldRegistry, "OLD2");
        var good = Entry(newRegistry, "NEW1");
        var inactive = Entry(newRegistry, "NEWOFF");
        inactive.Deactivate();
        var deleted = Entry(newRegistry, "NEWDEL");
        deleted.SoftDelete();
        var expired = Entry(newRegistry, "NEWEXP");
        expired.SetValidity(new DateOnly(2000, 1, 1), new DateOnly(2001, 1, 1));
        var foreign = Entry(foreignRegistry, "FOR1");
        await db.SaveChangesAsync().ConfigureAwait(false);

        var code = $"PRM_{s.Tag}";
        var oldField = new HeaderFieldDef(s.Doc.TemplateVersionId, EcrCode.Create(code), Name("Permit"), 0, CellDataType.Lookup);
        oldField.SetLookup(oldRegistry.Id);
        var newField = new HeaderFieldDef(s.TargetVersionId, EcrCode.Create(code), Name("Permit"), 0, CellDataType.Lookup);
        newField.SetLookup(newRegistry.Id);
        db.HeaderFieldDefs.AddRange(oldField, newField);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.DocumentHeaderValues.Add(new DocumentHeaderValue(
            s.Doc.DocumentId, oldField.Id, new DocumentHeaderValueData { ValueRegistryEntryId = oldEntry.Id }));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new LookupSetup(
            s, code, oldField.Id, newField.Id, oldEntry.Id, otherOld.Id, good.Id, inactive.Id, deleted.Id, expired.Id, foreign.Id);
    }

    private static Task<HttpResponseMessage> PostRemapAsync(
        HttpClient client, LookupSetup l, bool dryRun, bool async = false, params (long From, long To)[] pairs)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{l.S.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new Dictionary<string, object?>
            {
                ["targetVersionId"] = l.S.TargetVersionId,
                ["mode"] = "Safe",
                ["dryRun"] = dryRun,
                ["async"] = async,
                ["lookupRemaps"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target"] = new Dictionary<string, object?> { ["kind"] = "header", ["code"] = l.FieldCode },
                        ["mappings"] = pairs
                            .Select(p => new Dictionary<string, object?> { ["from"] = p.From, ["to"] = p.To })
                            .ToArray(),
                    },
                },
            });

    private async Task<(int FieldId, long? EntryId)> StoredLookupAsync(LookupSetup l)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var row = await db.DocumentHeaderValues.AsNoTracking()
            .Where(v => v.DocumentId == l.S.Doc.DocumentId)
            .Select(v => new { v.HeaderFieldDefId, v.ValueRegistryEntryId })
            .SingleAsync().ConfigureAwait(false);
        return (row.HeaderFieldDefId, row.ValueRegistryEntryId);
    }

    private async Task<string> CellsAsync(LookupSetup l)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == l.S.Doc.PeriodKey.Value && l.S.Doc.RowIds.Contains(c.TableRowId))
            .OrderBy(c => c.TableRowId).ThenBy(c => c.ColumnDefId)
            .Select(c => $"{c.TableRowId}/{c.ValueString}/{c.ValueNumeric}/{c.IsEmpty}")
            .ToListAsync().ConfigureAwait(false);
        return string.Join(";", cells);
    }

    private async Task<List<string>> RemapEventsAsync(LookupSetup l)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentHeaderLookupRemapped' AND ChangedByUserId = {l.S.UserId}")
            .ToListAsync().ConfigureAwait(false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Без_мапи_зміна_довідника_шапки_лишається_guardedWithData_а_текст_HeaderSql_незмінний()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostAsync(client, l.S, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.False(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.Contains("guardedWithData", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.DoesNotContain("lookupValuesNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.True(!dry.TryGetProperty("lookupRemapReport", out var report) || report.ValueKind == JsonValueKind.Null);

        // Land-safety: шлях без мапи виконує ТОЙ САМИЙ текст `HeaderSql` (без OPENJSON і запису запису довідника).
        var headerSql = (string)typeof(DocumentVersionMigrationStore)
            .GetField("HeaderSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.DoesNotContain("lookupRemap", headerSql, StringComparison.Ordinal);
        Assert.DoesNotContain("ValueRegistryEntryId", headerSql, StringComparison.Ordinal);
        var remapSql = (string)typeof(DocumentVersionMigrationStore)
            .GetField("HeaderSqlWithLookupRemap", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.Contains("OPENJSON(@lookupRemap)", remapSql, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Повна_мапа_переписує_значення_шапки_пише_аудит_а_комірки_й_перерахунок_не_чіпає()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);
        var cellsBefore = await CellsAsync(l).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        // Зайва пара (OldEntry2 ніде не вжито) ігнорується.
        var dry = await ReadJsonAsync(await PostRemapAsync(
            client, l, dryRun: true, pairs: [(l.OldEntry, l.GoodNew), (l.OtherOldEntry, l.GoodNew)]).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.Equal(1, dry.GetProperty("lookupRemapped").GetInt64());
        var item = Assert.Single(dry.GetProperty("lookupRemapReport").EnumerateArray());
        Assert.True(item.GetProperty("applied").GetBoolean());
        Assert.Equal(1, item.GetProperty("mappedValues").GetInt64());
        Assert.Equal(0, item.GetProperty("unmappedValues").GetInt64());
        Assert.Equal((l.OldField, (long?)l.OldEntry), await StoredLookupAsync(l).ConfigureAwait(true));

        var response = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OldEntry, l.GoodNew), (l.OtherOldEntry, l.GoodNew)])
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());

        // ⛔ Предмет тесту: поле — нової версії І запис — нового довідника.
        Assert.Equal((l.NewField, (long?)l.GoodNew), await StoredLookupAsync(l).ConfigureAwait(true));
        Assert.Equal(cellsBefore, await CellsAsync(l).ConfigureAwait(true));

        // Аудит: подія на документ зі старим/новим id і ознакою locked; підсумок має лічильники.
        var events = await RemapEventsAsync(l).ConfigureAwait(true);
        var evt = JsonDocument.Parse(Assert.Single(events)).RootElement;
        Assert.Equal(l.S.Doc.DocumentId, evt.GetProperty("documentId").GetInt64());
        var field = Assert.Single(evt.GetProperty("fields").EnumerateArray());
        Assert.Equal(l.FieldCode, field.GetProperty("code").GetString());
        Assert.Equal(l.OldEntry, field.GetProperty("oldEntryId").GetInt64());
        Assert.Equal(l.GoodNew, field.GetProperty("newEntryId").GetInt64());
        Assert.False(field.GetProperty("locked").GetBoolean());

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var summary = await db.Database
                .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {l.S.UserId}")
                .SingleAsync().ConfigureAwait(true);
            Assert.Equal(1, JsonDocument.Parse(summary).RootElement.GetProperty("lookupRemapped").GetInt64());
        }

        // Перерахунок не ставиться.
        await Task.Delay(2000).ConfigureAwait(true);
        Assert.Equal(0, await ProjectRecalcJobsAsync(l.S, systemOnly: false).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Неповна_мапа_відмовляє_422_lookupNotMapped_і_нічого_не_записує()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);
        var before = await SnapshotAsync(l.S).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        // Мапа не покриває вжитий запис OldEntry.
        var dry = await ReadJsonAsync(await PostRemapAsync(client, l, dryRun: true, pairs: [(l.OtherOldEntry, l.GoodNew)]).ConfigureAwait(true))
            .ConfigureAwait(true);
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("lookupValuesNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        var item = Assert.Single(dry.GetProperty("lookupRemapReport").EnumerateArray());
        Assert.Equal(1, item.GetProperty("unmappedValues").GetInt64());
        Assert.Equal(l.OldEntry, Assert.Single(item.GetProperty("unmappedEntryIds").EnumerateArray()).GetInt64());

        var response = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OtherOldEntry, l.GoodNew)]).ConfigureAwait(true);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateLookupNotMapped")
            .ConfigureAwait(true);

        Assert.Equal(before, await SnapshotAsync(l.S).ConfigureAwait(true));
        Assert.Equal((l.OldField, (long?)l.OldEntry), await StoredLookupAsync(l).ConfigureAwait(true));
        Assert.Empty(await RemapEventsAsync(l).ConfigureAwait(true));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("foreign")]
    [InlineData("notValidInWindow")]
    [InlineData("missing")]
    public async Task Ціль_неактивна_видалена_чужого_довідника_нечинна_або_неіснуюча_це_відмова_без_запису(string reason)
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);
        var before = await SnapshotAsync(l.S).ConfigureAwait(true);
        var target = reason switch
        {
            "inactive" => l.InactiveNew,
            "deleted" => l.DeletedNew,
            "foreign" => l.ForeignNew,
            "notValidInWindow" => l.ExpiredNew,
            _ => 999_999_999_999L,
        };

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostRemapAsync(client, l, dryRun: true, pairs: [(l.OldEntry, target)]).ConfigureAwait(true))
            .ConfigureAwait(true);
        Assert.False(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        var bad = Assert.Single(Assert.Single(dry.GetProperty("lookupRemapReport").EnumerateArray()).GetProperty("targetNotUsable").EnumerateArray());
        Assert.Equal(reason, bad.GetProperty("reason").GetString());
        Assert.Equal(target, bad.GetProperty("entryId").GetInt64());

        var response = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OldEntry, target)]).ConfigureAwait(true);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateLookupNotMapped")
            .ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(l.S).ConfigureAwait(true));
        Assert.Equal((l.OldField, (long?)l.OldEntry), await StoredLookupAsync(l).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Чужий_from_не_зі_старого_довідника_це_422_migrateLookupRemapInvalid()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);
        var before = await SnapshotAsync(l.S).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        // from — запис ЦІЛЬОВОГО довідника (а не вихідного); навіть як зайва пара мапа не приймається.
        var response = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OldEntry, l.GoodNew), (l.InactiveNew, l.GoodNew)])
            .ConfigureAwait(true);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateLookupRemapInvalid")
            .ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(l.S).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Колонка_Lookup_у_мапі_це_422_migrateLookupRemapInvalid_ще_до_постановки_у_чергу()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        foreach (var async in new[] { false, true })
        {
            var response = await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{l.S.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
                new Dictionary<string, object?>
                {
                    ["targetVersionId"] = l.S.TargetVersionId,
                    ["mode"] = "Safe",
                    ["dryRun"] = false,
                    ["async"] = async,
                    ["lookupRemaps"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["target"] = new Dictionary<string, object?> { ["kind"] = "column", ["code"] = "PermitNumber" },
                            ["mappings"] = new[] { new Dictionary<string, object?> { ["from"] = 1, ["to"] = 2 } },
                        },
                    },
                }).ConfigureAwait(true);
            await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateLookupRemapInvalid")
                .ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Ціль_без_зміни_довідника_або_відсутня_в_парі_версій_applied_false_і_нічого_не_ламає()
    {
        // Версії без полів шапки: мапа на неіснуюче поле мовчки пропускається.
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var l = new LookupSetup(s, "NOPE", 0, 0, 1, 2, 3, 4, 5, 6, 7);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostRemapAsync(client, l, dryRun: true, pairs: [(1, 3)]).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        var item = Assert.Single(dry.GetProperty("lookupRemapReport").EnumerateArray());
        Assert.False(item.GetProperty("applied").GetBoolean());

        var response = await PostRemapAsync(client, l, dryRun: false, pairs: [(1, 3)]).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Повторне_виконання_тієї_самої_мапи_це_migrateSameVersion_і_значення_не_змінюється()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        var first = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OldEntry, l.GoodNew)]).ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync().ConfigureAwait(true));
        var after = await StoredLookupAsync(l).ConfigureAwait(true);

        var second = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OldEntry, l.GoodNew)]).ConfigureAwait(true);
        await AssertProblemAsync(second, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.migrateSameVersion")
            .ConfigureAwait(true);

        Assert.Equal(after, await StoredLookupAsync(l).ConfigureAwait(true));
        Assert.Single(await RemapEventsAsync(l).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Затверджений_документ_шапка_ремапиться_без_прапора_а_в_аудиті_locked_true()
    {
        var l = await ArrangeLookupAsync(submitted: true, approved: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        var response = await PostRemapAsync(client, l, dryRun: false, pairs: [(l.OldEntry, l.GoodNew)]).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        Assert.Equal((l.NewField, (long?)l.GoodNew), await StoredLookupAsync(l).ConfigureAwait(true));
        var evt = JsonDocument.Parse(Assert.Single(await RemapEventsAsync(l).ConfigureAwait(true))).RootElement;
        Assert.True(Assert.Single(evt.GetProperty("fields").EnumerateArray()).GetProperty("locked").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Фоновий_перенос_з_мапою_теж_переписує_значення_шапки()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, l.S.UserName).ConfigureAwait(true);

        var response = await PostRemapAsync(client, l, dryRun: false, async: true, pairs: [(l.OldEntry, l.GoodNew)]).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var jobId = JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;

        var job = await WaitJobAsync(client, jobId, app).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");
        Assert.Equal((l.NewField, (long?)l.GoodNew), await StoredLookupAsync(l).ConfigureAwait(true));
    }
}
