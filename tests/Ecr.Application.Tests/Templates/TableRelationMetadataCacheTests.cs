using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// T6-01: правка зв'язку Rollup/Check має доходити до кешу метаданих (<c>HasActiveRollupOrCheck</c>), інакше
/// <c>SubmitSheetHandler</c> не запускає Check і Block не блокує подання, хоча <c>Validate</c> його показує.
/// На <b>реальному</b> SQL Server і справжньому <see cref="MetadataCache"/>.
/// </summary>
/// <remarks>
/// ⛔ Кеш ключується <c>PresentationRevision</c>, а правка зв'язку його не піднімає. Тест прогріває кеш ДО правки
/// (саме так на стенді: шаблон відкривали й валидували до додавання зв'язку), далі правиться зв'язок і знімок
/// перечитується. Мутація: прибрати <c>metadataCache.InvalidateAsync</c> у <c>Save</c>/<c>DeleteTableRelationHandler</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableRelationMetadataCacheTests(SqlServerFixture sql) : IDisposable
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private const string Match = """{"by":"RowKey"}""";
    private const string CheckMap = """{"left":"A","right":"B","tolerance":"0","severity":"Block"}""";

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Check_доданий_після_прогріву_кешу_одразу_видимий_а_після_видалення_зникає()
    {
        var version = await DraftAsync();
        Assert.False(await HasRelationsAsync(version.VersionId), "Контроль: до правки зв'язків немає.");

        await SaveAsync(version, isActive: true);
        Assert.True(
            await HasRelationsAsync(version.VersionId),
            "T6-01: Check додано, а кеш метаданих віддає стару відповідь — Submit не перевірить Block.");

        await using (var db = Context())
        {
            await Delete(db).HandleAsync(version.VersionId, version.RelationCode, CancellationToken.None);
        }

        Assert.False(
            await HasRelationsAsync(version.VersionId),
            "T6-01: Check видалено, а кеш метаданих досі вважає зв'язок активним.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Вимкнення_і_ввімкнення_Check_доходить_до_кешу()
    {
        var version = await DraftAsync();
        await SaveAsync(version, isActive: true);
        Assert.True(await HasRelationsAsync(version.VersionId));

        await SaveAsync(version, isActive: false);
        Assert.False(
            await HasRelationsAsync(version.VersionId),
            "T6-01: Check вимкнено (isActive=false), а кеш досі його бачить.");

        await SaveAsync(version, isActive: true);
        Assert.True(
            await HasRelationsAsync(version.VersionId),
            "T6-01: Check увімкнено знову, а кеш досі його не бачить.");
    }

    private async Task<bool> HasRelationsAsync(int versionId)
    {
        await using var db = Context();
        var snapshot = await new MetadataCache(_memory, db).GetAsync(versionId, CancellationToken.None);
        return snapshot.HasActiveRollupOrCheck;
    }

    private async Task SaveAsync(DraftVersion version, bool isActive)
    {
        await using var db = Context();
        await Save(db).HandleAsync(
            version.VersionId, version.RelationCode,
            new SaveTableRelationCommand(
                version.SourceTableDefId, version.TargetTableDefId, TableRelationKind.Check, Match, CheckMap, 0, isActive),
            CancellationToken.None);
    }

    private SaveTableRelationHandler Save(EcrDbContext db)
    {
        Profile();

        return new SaveTableRelationHandler(
            new Repository<TemplateVersion, int>(db), new Repository<TableRelationDef, int>(db),
            new TemplateVersionStore(db), new ChangeClassifier(), new MetadataCache(_memory, db),
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user);
    }

    private DeleteTableRelationHandler Delete(EcrDbContext db)
    {
        Profile();

        return new DeleteTableRelationHandler(
            new Repository<TemplateVersion, int>(db), new Repository<TableRelationDef, int>(db),
            new TemplateVersionStore(db), new ChangeClassifier(), new MetadataCache(_memory, db),
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user);
    }

    private void Profile()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Permission("Template.View").Build());
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Чернетка з двома таблицями, у кожній по одній десятковій колонці (<c>A</c> і <c>B</c>).</summary>
    private async Task<DraftVersion> DraftAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"TC{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var source = new TableDef(
            sheet.Id, EcrCode.Create($"Main{tag}"), Name("Main"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        var target = new TableDef(
            sheet.Id, EcrCode.Create($"Cons{tag}"), Name("Cons"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(source);
        db.TableDefs.Add(target);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(source.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal));
        db.ColumnDefs.Add(new ColumnDef(target.Id, EcrCode.Create("B"), Name("B"), 1, CellDataType.Decimal));
        await db.SaveChangesAsync();

        return new DraftVersion(version.Id, source.Id, target.Id, $"CHK{tag}");
    }

    private sealed record DraftVersion(int VersionId, int SourceTableDefId, int TargetTableDefId, string RelationCode);
}
