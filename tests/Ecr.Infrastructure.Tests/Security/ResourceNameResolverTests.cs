// tests/Ecr.Infrastructure.Tests/Security/ResourceNameResolverTests.cs
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// <see cref="ResourceNameResolver"/> на <b>реальному</b> SQL Server (<c>Q-299</c>).
/// </summary>
/// <remarks>
/// ⛔ Ключове припущення, на якому стоїть увесь дизайн картки: `SheetDef.Id`/
/// `TableDef.Id`/`ColumnDef.Id` — суцільні IDENTITY-ключі власних таблиць, а
/// НЕ складові з `TemplateVersionId` (перевірено в
/// `TemplateStructureConfiguration.cs`: `HasKey(x => x.Id)` — окремо від
/// унікального індексу `(TemplateVersionId, Code)`). Якби це припущення було
/// хибним, розв'язання за `(kind, id)` без версії було б недовизначеним —
/// тест нижче доводить це на ДВОХ окремих версіях шаблону одночасно, а не
/// просто вірить коментарю в конфігурації.
/// </remarks>
[Collection("SqlServer")]
public sealed class ResourceNameResolverTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розвязує_код_ресурсу_для_кожного_виду()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        var resolver = new ResourceNameResolver(db);

        var result = await resolver.ResolveAsync(
            [
                (ResourceKind.Project, doc.ProjectId),
                (ResourceKind.Sheet, doc.SheetDefId),
                (ResourceKind.Table, doc.TableDefId),
                (ResourceKind.Column, doc.ColumnDefIds[0]),
            ],
            CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Equal(doc.SheetCode, result[(ResourceKind.Sheet, doc.SheetDefId)]);
        Assert.StartsWith("PRJ", result[(ResourceKind.Project, doc.ProjectId)], StringComparison.Ordinal);
        Assert.StartsWith("TBL", result[(ResourceKind.Table, doc.TableDefId)], StringComparison.Ordinal);
        Assert.StartsWith("C1_", result[(ResourceKind.Column, doc.ColumnDefIds[0])], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ідентифікатор_не_плутається_між_версіями_шаблону()
    {
        // ⚠ Головне твердження картки: `SheetDef`/`TableDef`/`ColumnDef` мають
        // ГЛОБАЛЬНО унікальні id — тому розв'язання без версії однозначне.
        // Дві незалежні версії (два виклики `BuildAsync`) дають РІЗНІ id за
        // побудовою (IDENTITY); якби резолвер помилково фільтрував за чимось,
        // крім (kind, id), або id колись повторився між версіями, цей тест
        // упіймав би підміну — сторонній аркуш другої версії мав би ІНШИЙ код.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var first = await builder.BuildAsync();
        var second = await builder.BuildAsync();

        Assert.NotEqual(first.SheetDefId, second.SheetDefId);

        await using var db = builder.CreateContext();
        var resolver = new ResourceNameResolver(db);

        var result = await resolver.ResolveAsync(
            [(ResourceKind.Sheet, first.SheetDefId), (ResourceKind.Sheet, second.SheetDefId)],
            CancellationToken.None);

        Assert.Equal(first.SheetCode, result[(ResourceKind.Sheet, first.SheetDefId)]);
        Assert.Equal(second.SheetCode, result[(ResourceKind.Sheet, second.SheetDefId)]);
        Assert.NotEqual(
            result[(ResourceKind.Sheet, first.SheetDefId)],
            result[(ResourceKind.Sheet, second.SheetDefId)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Неіснуючий_ідентифікатор_просто_відсутній_у_результаті()
    {
        // ⛔ Не помилка, не виняток: ресурс видалено фізично або посилання
        // «осиротіло» — виклик має відповісти «не знайшов», а не впасти.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        var resolver = new ResourceNameResolver(db);

        var result = await resolver.ResolveAsync(
            [(ResourceKind.Sheet, doc.SheetDefId), (ResourceKind.Sheet, 999_999_999)],
            CancellationToken.None);

        Assert.Single(result);
        Assert.True(result.ContainsKey((ResourceKind.Sheet, doc.SheetDefId)));
        Assert.False(result.ContainsKey((ResourceKind.Sheet, 999_999_999)));
    }
}
