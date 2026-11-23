// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Bindings.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-13: перенос проєкту на іншу версію шаблону не мовчить, якщо активна прив'язка методології до колонки
/// вихідної версії не має відповідника в цільовій (колонка є, а прив'язки нема, або колонки немає зовсім).
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    private async Task<int> AddBindingAsync(int tableDefId, int columnDefId, int? methodologyId = null, bool active = true)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var id = methodologyId;
        if (id is null)
        {
            var methodology = new Methodology(
                EcrCode.Create($"MBM_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Migrate bound" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync().ConfigureAwait(false);
            id = methodology.Id;
        }

        var binding = new CalculationBinding(tableDefId, columnDefId, id.Value, "TONS", "{}");
        if (!active)
        {
            binding.Update("{}", isActive: false);
        }

        db.CalculationBindings.Add(binding);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return id.Value;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Активна_прив_язка_без_відповідника_в_цільовій_версії_блокує_перенос_і_видна_в_сухому_прогоні()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await AddBindingAsync(s.Doc.TableDefId, s.Doc.ColumnDefIds[1]).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("bindingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateBindingsNotMapped").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Прив_язка_до_колонки_якої_нема_в_цільовій_версії_теж_блокує_перенос()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        await AddBindingAsync(s.Doc.TableDefId, s.Doc.ColumnDefIds[2]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("bindingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Прив_язка_що_є_і_в_цільовій_версії_перенос_не_блокує()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var methodologyId = await AddBindingAsync(s.Doc.TableDefId, s.Doc.ColumnDefIds[1]).ConfigureAwait(true);
        await AddBindingAsync(s.TargetTableDefId, s.TargetColumns["C2"], methodologyId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.DoesNotContain("bindingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Вимкнена_прив_язка_без_відповідника_перенос_не_блокує()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await AddBindingAsync(s.Doc.TableDefId, s.Doc.ColumnDefIds[1], active: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
    }

    /// <summary>
    /// ✎ P-1123-4 (вода T7b_00): вихід методології (Calculated) у цільовій версії рахує ФОРМУЛА ТАБЛИЦІ, а прив'язку свідомо знято -
    /// це не «прив'язка без відповідника» (колонка не лишається порожньою). Мутаційний доказ: прибрати
    /// <c>formulaTargets</c> у <c>DocumentVersionMigrationStore.CountUnmappedBindingsAsync</c> - червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Прив_язка_замінена_формулою_таблиці_в_цільовій_версії_не_є_bindingsNotMapped()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, publishTarget: false).ConfigureAwait(true);
        await AddBindingAsync(s.Doc.TableDefId, s.Doc.ColumnDefIds[1]).ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var c2 = s.TargetColumns["C2"];
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.ColumnDef SET DataType = 8 WHERE Id = {2}; " +   // джерело - вихід методології (Calculated)
                "UPDATE cfg.ColumnDef SET DataType = 6, IsReadOnly = 1 WHERE Id = {0}; " +
                "INSERT INTO cfg.FormulaDef (TableDefId, ColumnDefId, Scope, Dialect, Expression) VALUES ({1}, {0}, 0, 0, N'1+1'); " +
                "UPDATE v SET Status = 1, PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 FROM cfg.TemplateVersion v " +
                "JOIN cfg.SheetDef sd ON sd.TemplateVersionId = v.Id JOIN cfg.TableDef td ON td.SheetDefId = sd.Id WHERE td.Id = {1};",
                [c2, s.TargetTableDefId, s.Doc.ColumnDefIds[1]]).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.DoesNotContain("bindingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
    }
}
