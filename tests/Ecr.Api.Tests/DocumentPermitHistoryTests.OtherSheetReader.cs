// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.OtherSheetReader.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ F13 AU (B79): колонку дозволу читає лише формула ІНШОГО аркуша (у ньому нічого не затверджено), а весь рік аркуша
/// правки (календар — січень 2026) затверджено. Затверджені значення не змінюються: додати дозвіл рядка й записати
/// частини поділу не можна — <c>DocumentApproved</c>, і нічого не записано. І коли рік затверджено ДО рішення (рішення
/// поза транзакцією), і коли МІЖ рішенням і записом (повторна перевірка під замками, <c>AnnualDecisionRecheck</c>).
/// Контроль: незатверджений рік — дозвіл додається.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (AU): в <c>AccessDecisionService.Decide</c> зводити <c>Approved</c> до <c>Draft</c> без умови
/// <c>OwnSheetReads</c> — червоніє «…(false)»; в <c>AnnualDecisionRecheck.EnsureAsync</c> прибрати перевірку агрегату в
/// гілці <c>DependentOnlyOtherSheets</c> — червоніє «…(true)». «…частини_пишуться…» — контроль (частини пишуть стовпець
/// без залежних, тож їх замикає звичайний агрегат; зелений і без фіксу).
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Читач_на_іншому_аркуші_рік_аркуша_правки_затверджено_додавання_дозволу_DocumentApproved(bool raced)
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var ids = await MakeAnnualAsync(s, dependent: false).ConfigureAwait(true);
        await AddOtherSheetReaderAsync(s, ids).ConfigureAwait(true);
        var race = new DecisionRace();
        if (raced)
        {
            race.AfterDecision = () => ApproveJanuaryAsync(s, ids);
        }
        else
        {
            await ApproveJanuaryAsync(s, ids).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var racing = RacingDecision(app, race);
        using var client = await SignedInRoutedAsync(racing, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(raced, race.Fired);
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("DocumentApproved", JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>
    /// Контроль: той самий читач на іншому аркуші, рік не затверджено — дозвіл додається; затвердження між рішенням і
    /// записом частин (стовпець без залежних) відхиляє їх звичайним агрегатом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Читач_на_іншому_аркуші_рік_не_затверджено_частини_пишуться_а_затвердження_між_рішенням_і_записом_відхиляє()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var ids = await MakeAnnualAsync(s, dependent: false).ConfigureAwait(true);
        await AddOtherSheetReaderAsync(s, ids).ConfigureAwait(true);
        var race = new DecisionRace();
        using var app = new EcrApiFactory(sql);
        using var racing = RacingDecision(app, race);
        using var client = await SignedInRoutedAsync(racing, s.UserName).ConfigureAwait(true);

        var added = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        Assert.True(added.StatusCode == HttpStatusCode.OK, $"{added.StatusCode}: {await added.Content.ReadAsStringAsync().ConfigureAwait(true)}");
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));

        race.AfterDecision = () => ApproveJanuaryAsync(s, ids);
        var split = await SplitAsync(client, s, 5m, null).ConfigureAwait(true);
        var body = await split.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "обгортка служби доступу не вклинилась");
        Assert.True(split.StatusCode == HttpStatusCode.Forbidden, $"{split.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("DocumentApproved", JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, await PartCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Аркуш B тієї ж версії з формулою стовпця, ребро якої читає колонку дозволу таблиці аркуша правки.</summary>
    private async Task AddOtherSheetReaderAsync(Scenario s, AnnualIds ids)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var versionId = await db.SheetDefs.AsNoTracking()
            .Where(x => x.Id == ids.SheetDefId).Select(x => x.TemplateVersionId).SingleAsync().ConfigureAwait(false);
        var permitColumnId = (await db.ColumnDefs.AsNoTracking()
                .Where(c => c.TableDefId == ids.TableDefId).ToListAsync().ConfigureAwait(false))
            .Single(c => string.Equals(c.Code, s.PermitColumnCode, StringComparison.Ordinal)).Id;

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var sheetB = new SheetDef(versionId, EcrCode.Create($"SHR{tag}"), Text("Reader sheet"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"TBR{tag}"), Text("Reader table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var readerColumn = new ColumnDef(tableB.Id, EcrCode.Create($"RR{tag}"), Text("Reader"), 1, CellDataType.Formula);
        db.ColumnDefs.Add(readerColumn);
        db.DocumentSheets.Add(new DocumentSheet(s.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var formula = new FormulaDef(tableB.Id, FormulaScope.Column, "0", ExpressionDialect.Template);
        formula.AssignColumn(readerColumn.Id);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.FormulaDependencies.Add(FormulaDependency.ForFormula(
            formula.Id, dependsOnKind: 0, ids.TableDefId, rowKey: null, permitColumnId,
            filterJson: null, periodOffset: null, sortOrder: 0));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
