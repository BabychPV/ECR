using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Зв'язки читаються В МЕЖАХ ВЕРСІЇ (<c>ФВ-2.12</c>).
/// </summary>
/// <remarks>
/// ⛔ Це найтонше місце механізму. <c>cfg.TableRelationDef</c> не має власного
/// <c>TemplateVersionId</c>, а <c>Code</c> унікальний у межах усієї бази
/// (<c>UQ_TableRelationDef</c>). Версія дістається лише через
/// <c>TableDef → SheetDef</c>, і запит без цього з'єднання ПРАЦЮЄ: він просто
/// віддає зв'язок чужої версії. Симптому в такої вади немає — редактор
/// показував би чужий зв'язок як свій, а <c>PUT</c> правив би структуру іншого
/// шаблону.
///
/// ⚠ Тест інтеграційний навмисно: перевіряється саме SQL-з'єднання, а
/// підставний <c>ITemplateVersionStore</c> у тестах застосунку про нього не
/// знає нічого.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableRelationStoreTests(SqlServerFixture sql)
{
    /// <summary>Друга таблиця у версії: зв'язку потрібні дві.</summary>
    /// <param name="sheetDefId">Аркуш версії.</param>
    /// <param name="tag">Унікальний суфікс коду.</param>
    /// <returns>Ідентифікатор створеної таблиці.</returns>
    private async Task<int> AddTableAsync(int sheetDefId, string tag)
    {
        await using var db = CreateContext();

        var table = new TableDef(
            sheetDefId, EcrCode.Create($"TBL2{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Table 2 {tag}" }),
            2, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);

        db.TableDefs.Add(table);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return table.Id;
    }

    private async Task AddRelationAsync(string code, int sourceTableDefId, int targetTableDefId)
    {
        await using var db = CreateContext();

        db.TableRelations.Add(new TableRelationDef(
            EcrCode.Create(code), sourceTableDefId, targetTableDefId,
            TableRelationKind.Rollup, """{"by":"RowKey"}"""));

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Зв_язок_чужої_версії_не_потрапляє_ні_в_перелік_ні_в_пошук_за_кодом()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var mine = await builder.BuildAsync(ct: ct);
        var alien = await builder.BuildAsync(ct: ct);

        var mineSecond = await AddTableAsync(mine.SheetDefId, $"M{mine.TemplateVersionId}");
        var alienSecond = await AddTableAsync(alien.SheetDefId, $"A{alien.TemplateVersionId}");

        var mineCode = $"RelMine{mine.TemplateVersionId}";
        var alienCode = $"RelAlien{alien.TemplateVersionId}";

        await AddRelationAsync(mineCode, mine.TableDefId, mineSecond);
        await AddRelationAsync(alienCode, alien.TableDefId, alienSecond);

        await using var db = CreateContext();
        var store = new TemplateVersionStore(db);

        var listed = await store.ListTableRelationsAsync(mine.TemplateVersionId, ct);
        Assert.Equal([mineCode], listed.Select(r => r.Code));

        // ⛔ Головне твердження. Код унікальний у межах бази, тому запит без
        // з'єднання з версією знайшов би ЧУЖИЙ зв'язок і віддав би його на
        // правку — мовчки і правдоподібно.
        Assert.Null(await store.FindTableRelationAsync(mine.TemplateVersionId, alienCode, ct));
        Assert.NotNull(await store.FindTableRelationAsync(mine.TemplateVersionId, mineCode, ct));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Коди_таблиць_беруться_лише_зі_своєї_версії()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var mine = await builder.BuildAsync(ct: ct);
        var alien = await builder.BuildAsync(ct: ct);

        await using var db = CreateContext();
        var codes = await new TemplateVersionStore(db).ListTableCodesAsync(mine.TemplateVersionId, ct);

        // ⚠ Саме цей словник і є перевіркою належності таблиці версії:
        // зовнішній ключ про версії не знає, і без неї зв'язок можна було б
        // завести між таблицями різних шаблонів.
        Assert.True(codes.ContainsKey(mine.TableDefId));
        Assert.False(codes.ContainsKey(alien.TableDefId));
    }
}
