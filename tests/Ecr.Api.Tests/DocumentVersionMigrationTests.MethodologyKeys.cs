// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.MethodologyKeys.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D1 (клон версії і ключі методологій): правила (<c>MatchJson</c>) й обов'язкові входи методології
/// посилаються на <c>ColumnDefId</c> КОНКРЕТНОЇ версії шаблону. Якщо методологія, прив'язана до цільової
/// версії, має опубліковану версію з ключами на колонки ІНШОЇ версії того ж шаблону, після переносу прогін
/// мовчки нічого не рахує, а <c>Block</c>-вимога блокує збереження назавжди — тож перенос відмовляє.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    public enum KeyKind { RuleKey, RequiredInput }

    /// <summary>Методологія, прив'язана до цільової колонки, з опублікованою (або ні) версією, що посилається на <paramref name="columnDefId"/>.</summary>
    private async Task ArrangeMethodologyKeyAsync(Scenario s, int columnDefId, KeyKind kind, bool publish = true)
    {
        var methodologyId = await AddBindingAsync(s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(false);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var version = new MethodologyVersion(
            methodologyId, "1.0", CalculationLevel.Configuration, createdByUserId: s.UserId,
            new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (kind == KeyKind.RuleKey)
        {
            db.MethodologyRules.Add(new MethodologyRule(
                version.Id, EcrCode.Create("R_KEY"), $$"""{"{{columnDefId}}":"X"}""", 10));
        }
        else
        {
            db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(
                version.Id, columnDefId, RequiredInputSeverity.Block, hint: null));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        if (publish)
        {
            version.Publish(s.UserId + 1, "тест D1", new DateOnly(2026, 1, 1), testsPassed: true,
                new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    [Theory]
    [InlineData(KeyKind.RuleKey)]
    [InlineData(KeyKind.RequiredInput)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D1")]
    public async Task Методологія_з_ключами_на_колонку_якої_немає_в_цільовій_версії_блокує_перенос_і_видна_в_сухому_прогоні(KeyKind kind)
    {
        // C1: ключ на колонку, що має відповідник за шляхом у цілі, перекладається при читанні і НЕ блокує
        // (див. наступний тест). Відмова лишилась лише для колонки, яку ціль втратила: C3 вилучено.
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        await ArrangeMethodologyKeyAsync(s, s.Doc.ColumnDefIds[2], kind).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.Contains("methodologyKeysNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422",
            "err.ECR-SCHM-0422.migrateMethodologyKeysNotMapped").ConfigureAwait(true);

        // Відмова називає, ЩО саме посилається не на ту версію: код колонки в переліку.
        Assert.Contains("methodologyKeys", body, StringComparison.Ordinal);
        Assert.Contains("C3_" + s.Tag, body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Theory]
    [InlineData(KeyKind.RuleKey)]
    [InlineData(KeyKind.RequiredInput)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C1")]
    public async Task Ключі_на_колонку_вихідної_версії_з_відповідником_за_шляхом_перенос_не_блокують(KeyKind kind)
    {
        // C1: C2 є в обох версіях під тим самим кодом аркуша/таблиці/колонки — правило/вимога перекладається при
        // читанні (MethodologyKeyLocalizer), тож перенос безпечний.
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await ArrangeMethodologyKeyAsync(s, s.Doc.ColumnDefIds[1], kind).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.DoesNotContain("methodologyKeysNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D1")]
    public async Task Методологія_з_ключами_на_колонки_цільової_версії_перенос_не_блокує()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await ArrangeMethodologyKeyAsync(s, s.TargetColumns["C2"], KeyKind.RuleKey).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.DoesNotContain("methodologyKeysNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D1")]
    public async Task Неопублікована_версія_методології_з_чужими_ключами_перенос_не_блокує()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await ArrangeMethodologyKeyAsync(s, s.Doc.ColumnDefIds[1], KeyKind.RuleKey, publish: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C1")]
    public async Task Методологія_з_двома_опублікованими_версіями_ключі_v1_і_v2_перенос_не_блокує()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var methodologyId = await AddBindingAsync(s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var old = new MethodologyVersion(
            methodologyId, "1.0", CalculationLevel.Configuration, createdByUserId: s.UserId,
            new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        var next = new MethodologyVersion(
            methodologyId, "2.0", CalculationLevel.Configuration, createdByUserId: s.UserId,
            new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        db.MethodologyVersions.AddRange(old, next);
        await db.SaveChangesAsync().ConfigureAwait(true);
        db.MethodologyRules.Add(new MethodologyRule(old.Id, EcrCode.Create("R_OLD"), $$"""{"{{s.Doc.ColumnDefIds[1]}}":"X"}""", 10));
        db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(old.Id, s.Doc.ColumnDefIds[1], RequiredInputSeverity.Block, hint: null));
        db.MethodologyRules.Add(new MethodologyRule(next.Id, EcrCode.Create("R_NEW"), $$"""{"{{s.TargetColumns["C2"]}}":"X"}""", 10));
        db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(next.Id, s.TargetColumns["C2"], RequiredInputSeverity.Block, hint: null));
        await db.SaveChangesAsync().ConfigureAwait(true);
        old.Publish(s.UserId + 1, "тест", new DateOnly(2026, 1, 1), testsPassed: true, new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync().ConfigureAwait(true);
        next.Publish(s.UserId + 1, "тест", new DateOnly(2026, 6, 1), testsPassed: true, new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.DoesNotContain("methodologyKeysNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
    }
}
