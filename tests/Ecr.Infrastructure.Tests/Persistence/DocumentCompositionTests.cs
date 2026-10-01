using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Склад документа за <c>SheetGroupRule</c> (<c>ФВ-3.2</c>) на реальному SQL Server:
/// <c>RequiresAll</c> відхиляє половину групи, <c>RequiresOne</c> — жодного аркуша групи.
/// </summary>
/// <remarks>
/// ⛔ Звірка вимог 2026-09-30 (№14): серверного тесту складу не було — клієнт мав
/// дзеркало правила (<c>groupRuleViolations.ts</c>), а сервер, який один вирішує,
/// був покритий лише мокованим обробником (<c>CreateDocumentHandlerTests</c>: мок
/// <c>ValidateCompositionAsync</c> сам повертає порушення). Тут — справжній
/// <c>DocumentStore.ValidateCompositionAsync</c> над справжньою базою.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentCompositionTests(SqlServerFixture sql)
{
    private const byte RequiresAll = 0;
    private const byte RequiresOne = 1;
    private const byte Excludes = 2;

    /// <summary>Версія з групами <c>Water</c> (W1, W2; RequiresAll) і <c>Air</c> (A1, A2; RequiresOne).</summary>
    private async Task<(int VersionId, Dictionary<string, int> Sheets)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        var tag = Guid.NewGuid().ToString("N")[..6];
        var sheets = new Dictionary<string, int>();

        await using var db = builder.CreateContext();

        foreach (var (name, group, ordinal) in new[]
                 {
                     ("W1", "Water", 11), ("W2", "Water", 12), ("A1", "Air", 13), ("A2", "Air", 14),
                 })
        {
            var sheet = new SheetDef(
                doc.TemplateVersionId, EcrCode.Create($"{name}{tag}"), En(name), ordinal);
            sheet.SetGroup(group);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync(CancellationToken.None);
            sheets[name] = sheet.Id;
        }

        db.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "Water", RequiresAll, targetGroup: null));
        db.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "Air", RequiresOne, targetGroup: null));

        // `Excludes` сервер не перевіряє (див. `groupRuleViolations.ts`): правило є, порушень немає.
        db.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "Water", Excludes, targetGroup: "Air"));
        await db.SaveChangesAsync(CancellationToken.None);

        return (doc.TemplateVersionId, sheets);
    }

    private async Task<IReadOnlyList<Ecr.Application.Ports.CompositionViolation>> ValidateAsync(
        int versionId, params int[] chosen)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        return await new DocumentStore(db).ValidateCompositionAsync(versionId, chosen, CancellationToken.None);
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>DocumentStore.ValidateCompositionAsync</c> замінити
    /// <c>picked &lt; inGroup.Count</c> на <c>false</c> — тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task RequiresAll_відхиляє_половину_групи()
    {
        var (versionId, s) = await ArrangeAsync();

        // Water обрано лише наполовину (W1 без W2); Air покрито (A1).
        var violations = await ValidateAsync(versionId, s["W1"], s["A1"]);

        var violation = Assert.Single(violations);
        Assert.Equal("Water", violation.SheetGroup);
        Assert.Equal(RequiresAll, violation.RuleKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task RequiresAll_приймає_групу_цілком_і_групу_взагалі_без_вибору()
    {
        var (versionId, s) = await ArrangeAsync();

        // Цілком: обидва аркуші Water; Air покрито.
        Assert.Empty(await ValidateAsync(versionId, s["W1"], s["W2"], s["A1"]));

        // Взагалі не обрано Water — це не порушення RequiresAll (група не входить).
        Assert.Empty(await ValidateAsync(versionId, s["A2"]));
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>DocumentStore.ValidateCompositionAsync</c> замінити
    /// <c>picked == 0</c> на <c>false</c> — тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task RequiresOne_відхиляє_склад_без_жодного_аркуша_групи()
    {
        var (versionId, s) = await ArrangeAsync();

        // Water цілком, з Air — нічого.
        var violations = await ValidateAsync(versionId, s["W1"], s["W2"]);

        var violation = Assert.Single(violations);
        Assert.Equal("Air", violation.SheetGroup);
        Assert.Equal(RequiresOne, violation.RuleKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Порожній_склад_порушує_лише_RequiresOne()
    {
        var (versionId, _) = await ArrangeAsync();

        var violations = await ValidateAsync(versionId);

        var violation = Assert.Single(violations);
        Assert.Equal("Air", violation.SheetGroup);
        Assert.Equal(RequiresOne, violation.RuleKind);
    }

    private static LocalizedText En(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
