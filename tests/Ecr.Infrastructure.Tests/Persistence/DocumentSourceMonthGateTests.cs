// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSourceMonthGateTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B80 (N2b, R3.1 A3/A5): <see cref="DocumentSourceMonthGate"/> на справжньому SQL Server — подання й затвердження
/// споживача зв'язки <c>ApprovedOnly</c> мають перешкоду лише тоді, коли документ-джерело року є, а місяць джерела не
/// затверджено (оригінал аркуша). За <c>Any</c>, без споживача, без документа року — перешкоди немає.
/// </summary>
/// <remarks>
/// ⚠ Правило 394: шаблон-джерело, довідник і зв'язка в кожного тесту власні; період 2099xx.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class DocumentSourceMonthGateTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private const int March = (Year * 100) + 3;
    private static readonly DateTime Stamp = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// Мутації: у воротах прибрати перевірку «місяць затверджено» (<c>approved.Contains(month)</c> → <c>true</c>) — березень
    /// не названо, червоний; прибрати вибір споживача (шаблон документа) — рядок «не споживач» нижче червоний.
    /// </remarks>
    [Fact]
    public async Task ApprovedOnly_незатверджений_місяць_джерела_названо_з_документом_і_проєктом()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        // Лише подано — не затверджено.
        await AddStateAsync(world.Source, March, approve: false);

        var missing = await FindAsync(world.Land.DocumentId, March);

        Assert.NotNull(missing);
        Assert.Equal(world.Source.DocumentId, missing.SourceDocumentId);
        Assert.Equal(March, missing.SourcePeriodKey);
        Assert.Equal(world.SourceProjectCode, missing.SourceProjectCode);
        Assert.Equal(world.SourceDocumentKey, missing.SourceDocumentKey);
    }

    [Fact]
    public async Task ApprovedOnly_затверджений_місяць_джерела_перешкоди_не_дає()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await AddStateAsync(world.Source, March, approve: true);

        Assert.Null(await FindAsync(world.Land.DocumentId, March));
    }

    /// <summary>Пара в одному світі: за <c>Any</c> — нічого (як до B80), за <c>ApprovedOnly</c> — місяць названо.</summary>
    /// <remarks>Мутація: прибрати фільтр <c>SourceState == ApprovedOnly</c> у запиті зв'язок — рядок Any червоний.</remarks>
    [Theory]
    [InlineData(DocumentSourceState.Any, false)]
    [InlineData(DocumentSourceState.ApprovedOnly, true)]
    public async Task Any_нічого_ApprovedOnly_відмова(DocumentSourceState state, bool refused)
    {
        var world = await ArrangeAsync(state);

        Assert.Equal(refused, await FindAsync(world.Land.DocumentId, March) is not null);
    }

    /// <summary>A5: за рік документа-джерела немає — рядки довідника, як доти; перешкоди немає.</summary>
    /// <remarks>Мутація: на <c>chosen is null</c> називати місяць замість <c>continue</c> — червоний.</remarks>
    [Fact]
    public async Task ApprovedOnly_рік_без_документа_джерела_перешкоди_не_дає()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        Assert.Null(await FindAsync(world.Land.DocumentId, ((Year - 1) * 100) + 3));
    }

    /// <summary>Документ шаблону, що не є споживачем зв'язки, — перешкоди немає.</summary>
    [Fact]
    public async Task ApprovedOnly_документ_не_споживача_перешкоди_не_дає()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: March);

        Assert.NotNull(await FindAsync(world.Land.DocumentId, March));
        Assert.Null(await FindAsync(other.DocumentId, March));
    }

    /// <summary>
    /// Затверджена КОПІЯ аркуша джерела не робить місяць затвердженим — лише оригінал (те саме правило, що в
    /// <see cref="DocumentSourceGate.ApprovedMonthsAsync"/>).
    /// </summary>
    /// <remarks>Мутація: прибрати <c>s.SheetCopyId == null</c> у <c>ApprovedMonthsAsync</c> — червоний.</remarks>
    [Fact]
    public async Task ApprovedOnly_затверджена_копія_аркуша_джерела_не_рахується()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await AddStateAsync(world.Source, March, approve: false);

        await using (var db = sql.CreateContext())
        {
            var original = await db.DocumentSheets.FirstOrDefaultAsync(
                s => s.DocumentId == world.Source.DocumentId && s.SheetDefId == world.Source.SheetDefId && s.CopyOfId == null);
            if (original is null)
            {
                original = new DocumentSheet(world.Source.DocumentId, world.Source.SheetDefId);
                db.DocumentSheets.Add(original);
                await db.SaveChangesAsync();
            }

            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync();

            var copyState = new ApprovalState(world.Source.DocumentId, world.Source.SheetDefId, March, copy.Id);
            copyState.Submit(userId: 5, Stamp);
            copyState.Approve(userId: 6, Stamp);
            db.ApprovalStates.Add(copyState);
            await db.SaveChangesAsync();
        }

        var missing = await FindAsync(world.Land.DocumentId, March);
        Assert.NotNull(missing);
        Assert.Equal(March, missing.SourcePeriodKey);
    }

    /// <summary>Читання з документа вимкнено (біт 0 без ключа, або явний ключ false) — перешкоди немає; ключ true перекриває біт 0.</summary>
    /// <remarks>Мутація: <c>readOverride ?? binding.ReadFromDocument</c> → <c>true</c> — червоний.</remarks>
    [Fact]
    public async Task Читання_з_документа_вимкнено_перешкоди_немає_а_ключ_true_перекриває_біт()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await using (var db = sql.CreateContext())
        {
            await db.DocumentSourceBindings.Where(b => b.Id == world.BindingId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReadFromDocument, false));
        }

        Assert.Null(await FindAsync(world.Land.DocumentId, March));
        Assert.Null(await FindAsync(world.Land.DocumentId, March, readOverride: false));
        Assert.NotNull(await FindAsync(world.Land.DocumentId, March, readOverride: true));
    }

    /// <summary>
    /// Річний споживач: період-якір володіє 12 місяцями — затвердження року вимагає кожного місяця джерела; названо
    /// перший незатверджений.
    /// </summary>
    [Fact]
    public async Task Річний_якір_споживача_вимагає_кожного_місяця_джерела()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await using (var db = sql.CreateContext())
        {
            await db.Projects.Where(p => p.Id == world.Land.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Yearly));
        }

        for (var month = 1; month <= 12; month++)
        {
            await AddStateAsync(world.Source, (Year * 100) + month, approve: month != 5);
        }

        var anchor = (Year * 100) + 1;
        Assert.Equal((Year * 100) + 5, (await FindAsync(world.Land.DocumentId, anchor))?.SourcePeriodKey);

        // Місячний проєкт споживача: січень затверджено — перешкоди немає.
        await using (var db = sql.CreateContext())
        {
            await db.Projects.Where(p => p.Id == world.Land.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Monthly));
        }

        Assert.Null(await FindAsync(world.Land.DocumentId, anchor));
    }

    private async Task<UnapprovedSourceMonth?> FindAsync(long documentId, int periodKey, bool? readOverride = null)
    {
        await using var db = sql.CreateContext();
        return await new DocumentSourceMonthGate(db, readOverride: readOverride, DependencyGraphTestKit.Provider(db))
            .FindUnapprovedSourceMonthAsync(documentId, periodKey, CancellationToken.None);
    }

    private async Task AddStateAsync(TestDocument source, int periodKey, bool approve)
    {
        await using var db = sql.CreateContext();
        var state = new ApprovalState(source.DocumentId, source.SheetDefId, periodKey);
        state.Submit(userId: 5, Stamp);
        if (approve)
        {
            state.Approve(userId: 6, Stamp);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Документ-джерело (власний шаблон з таблицею потоку <c>FG_Internal</c>, екземпляр за березень 2099), власний
    /// довідник, зв'язка в заданому стані і документ Land, чий шаблон — споживач.
    /// </summary>
    private async Task<World> ArrangeAsync(DocumentSourceState state)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var source = await builder.BuildAsync(periodKey: March);
        var land = await builder.BuildAsync(periodKey: March);

        await using var db = sql.CreateContext();
        var stream = new TableDef(
            source.SheetDefId, EcrCode.Create("FG_Internal"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FG_Internal" }), 50,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(stream);
        await db.SaveChangesAsync();

        var instanceId = await new BulkCellLoader(sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(new PeriodKey(March), instanceId, source.DocumentId, stream.Id, Stamp));

        var registry = new RegistryDef(
            EcrCode.Create($"HM{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Composition" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var binding = new DocumentSourceBinding($"B80N2B_{registry.Code}", source.TemplateId, registry.Id);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        await DependencyGraphTestKit.MakeConsumerAsync(db, land, registry.Code);
        await db.DocumentSourceBindings.Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state));

        var facts = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == source.DocumentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { ProjectCode = p.Code, d.BusinessKey })
            .SingleAsync();

        return new World(source, land, binding.Id, facts.ProjectCode, facts.BusinessKey);
    }

    private sealed record World(
        TestDocument Source, TestDocument Land, int BindingId, string SourceProjectCode, string SourceDocumentKey);
}
