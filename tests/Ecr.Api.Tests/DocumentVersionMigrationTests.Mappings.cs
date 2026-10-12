// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Mappings.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-1 (приймальна RC20): перенос проєкту на іншу версію шаблону переносить мапінги збору
/// (<c>ext.EntityFieldMap</c>) на колонки-відповідники цільової версії в тій самій транзакції, а мапінг без
/// відповідника не лишає мовчки: активний — відмова, призупинений — лишається як є.
/// </summary>
/// <remarks>
/// ⛔ Що було. Мапінг адресує колонку числовим <c>ColumnDefId</c> версії; перенос переміщав комірки, але не
/// мапінги, тож збір «успішно» читав точки й нічого не писав, а перепризначити мапінг було неможливо
/// (повторний POST — 409, PUT немає, DELETE — 409 «призупиніть»).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private async Task<int> AddMappingAsync(int columnDefId, bool active = true, string? rowKey = null)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var dataSource = new DataSource(
            EcrCode.Create($"MapSrc{tag}"),
            new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Source" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⛔ Неактивна сутність навмисно: база тестів спільна, а активне джерело, яке жодного разу не збиралося,
        // робить `/health/ready` Degraded у чужих тестах (див. CollectionScheduleStateSaveTests).
        var entity = new SourceEntity(dataSource.Id, $"MapEnt{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var map = EntityFieldMap.ToColumn(entity.Id, "Value", columnDefId);
        if (rowKey is not null)
        {
            map.SetMaterialization(rowKey, AggregationKind.Sum);
        }

        if (!active)
        {
            map.Pause();
        }

        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return map.Id;
    }

    private async Task<int> MappingColumnAsync(int mappingId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.EntityFieldMaps.AsNoTracking()
            .Where(m => m.Id == mappingId)
            .Select(m => m.TargetColumnDefId!.Value)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.Clone();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task Мапінги_збору_переїжджають_на_колонки_цільової_версії_разом_із_переносом_активний_і_призупинений()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var active = await AddMappingAsync(s.Doc.ColumnDefIds[1]).ConfigureAwait(true);
        var paused = await AddMappingAsync(s.Doc.ColumnDefIds[0], active: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // Сухий прогін показує, що буде перенесено, і нічого не міняє.
        var dry = await ReadJsonAsync(await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.Equal(2, dry.GetProperty("mappingsRemapped").GetInt32());
        Assert.Equal(0, dry.GetProperty("mappingsUnmapped").GetInt32());
        Assert.Equal(s.Doc.ColumnDefIds[1], await MappingColumnAsync(active).ConfigureAwait(true));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("mappingsRemapped").GetInt32());

        // ⛔ Предмет тесту: мапінг тепер на колонці ЦІЛЬОВОЇ версії (за кодом), а не лишився на старій.
        Assert.Equal(s.TargetColumns["C2"], await MappingColumnAsync(active).ConfigureAwait(true));
        Assert.Equal(s.TargetColumns["C1"], await MappingColumnAsync(paused).ConfigureAwait(true));

        // Перенос залишає мапінг у тому ж стані (пауза не знімається).
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.False((await db.EntityFieldMaps.AsNoTracking().SingleAsync(m => m.Id == paused).ConfigureAwait(true)).IsActive);
        Assert.True((await db.EntityFieldMaps.AsNoTracking().SingleAsync(m => m.Id == active).ConfigureAwait(true)).IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task Активний_мапінг_на_колонку_без_відповідника_блокує_перенос_422_і_видний_у_сухому_прогоні()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var orphan = await AddMappingAsync(s.Doc.ColumnDefIds[2]).ConfigureAwait(true);
        var carried = await AddMappingAsync(s.Doc.ColumnDefIds[0]).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("mappingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, dry.GetProperty("mappingsUnmapped").GetInt32());
        Assert.Equal(1, dry.GetProperty("mappingsRemapped").GetInt32());

        // Називає колонку шаблону (аркуш.таблиця.колонка), а не джерело й поле.
        var keys = dry.GetProperty("mappingsUnmappedKeys").EnumerateArray().Select(k => k.GetString()!).ToList();
        var key = Assert.Single(keys);
        Assert.StartsWith(s.Doc.SheetCode + ".", key, StringComparison.Ordinal);
        Assert.DoesNotContain("Value", key, StringComparison.Ordinal);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateMappingsNotMapped")
            .ConfigureAwait(true);

        // Відмова нічого не змінила: ні документи, ні мапінги (атомарність).
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
        Assert.Equal(s.Doc.ColumnDefIds[2], await MappingColumnAsync(orphan).ConfigureAwait(true));
        Assert.Equal(s.Doc.ColumnDefIds[0], await MappingColumnAsync(carried).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task Призупинений_мапінг_без_відповідника_перенос_не_блокує_і_лишається_на_місці()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var paused = await AddMappingAsync(s.Doc.ColumnDefIds[2], active: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.Equal(0, dry.GetProperty("mappingsUnmapped").GetInt32());

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal(s.Doc.ColumnDefIds[2], await MappingColumnAsync(paused).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task Фоновий_перенос_теж_переносить_мапінги_і_каже_скільки_у_повідомленні_задачі()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var mapping = await AddMappingAsync(s.Doc.ColumnDefIds[1]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var jobId = JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;

        var job = await WaitJobAsync(client, jobId, app).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        Assert.Equal(s.TargetColumns["C2"], await MappingColumnAsync(mapping).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task Активний_мапінг_без_відповідника_блокує_і_фоновий_перенос_задача_Failed_мапінг_і_документи_на_місці()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var orphan = await AddMappingAsync(s.Doc.ColumnDefIds[2]).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var jobId = JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;

        var job = await WaitJobAsync(client, jobId, app).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Failed", $"задача: {job.GetRawText()}");

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
        Assert.Equal(s.Doc.ColumnDefIds[2], await MappingColumnAsync(orphan).ConfigureAwait(true));
    }
}
