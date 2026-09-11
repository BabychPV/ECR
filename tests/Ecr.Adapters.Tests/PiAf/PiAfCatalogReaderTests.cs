using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// <see cref="PiAfCatalogReader"/> не мав жодного тесту (аудит, `Q-257`) —
/// саме той клас маленької стрічкової/шляхової логіки (регістр, межа
/// префікса, порожні рядки), що ламається тихо на краях. Тести нижче
/// покривають фільтр <c>BrowseAsync</c> (у т.ч. межовий випадок префікса),
/// фільтр <c>AttributesAsync</c> за <c>DataType</c>, стелю
/// <see cref="PiAfCatalogReader.MaxNodesPerLevel"/> і відмову для
/// неіснуючого/незареєстрованого джерела.
/// </summary>
/// <remarks>
/// ⛔ <c>IsChildOf</c> — приватний статичний метод; єдиний спосіб перевірити
/// його поведінку ззовні — через публічний <c>BrowseAsync</c>, тому кожен
/// тест підставляє фейковий <see cref="IExternalDataSource"/>
/// (<c>NSubstitute</c>, той самий взірець, що й
/// <c>CollectionRunnerTests.World</c>) і читає результат фільтрації.
/// </remarks>
public sealed class PiAfCatalogReaderTests
{
    private const int DataSourceId = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    public async Task BrowseAsync_повертає_дітей_шляху_і_ігнорує_регістр_батька()
    {
        var world = new World();
        world.Source.DiscoverAsync(DataSourceId, Arg.Any<CancellationToken>())
            .Returns(new List<SourceEntityDescriptor>
            {
                Descriptor("Child-SameCase", @"Root\Line1\StackA"),
                // Батьківський шлях підставлений в іншому регістрі — AF регістру
                // в іменах не розрізняє (коментар `IsChildOf` у джерелі).
                Descriptor("Child-DiffCase", @"ROOT\LINE1\StackB"),
                // Інша гілка дерева — не префікс "Root\Line1", тож не дитина.
                Descriptor("Sibling-Branch", @"Root\Line2\StackC"),
            });

        var result = await world.Reader.BrowseAsync(DataSourceId, @"Root\Line1", CancellationToken.None);

        Assert.Equal(["Child-DiffCase", "Child-SameCase"], result.Select(r => r.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    public async Task BrowseAsync_не_вважає_вузол_власною_дитиною_попри_збіг_префікса()
    {
        // ⛔ Регресія, яку ловить цей тест: наївний `StartsWith` без
        // `path.Length > parentPath.Length` вважав би вузол ВЛАСНОЮ дитиною —
        // "Root\Line1".StartsWith("Root\Line1") теж true. Саме перевірка
        // довжини (не сам `StartsWith`) відрізняє "є довшим шляхом під
        // батьком" від "той самий шлях, що й батько".
        var world = new World();
        world.Source.DiscoverAsync(DataSourceId, Arg.Any<CancellationToken>())
            .Returns(new List<SourceEntityDescriptor>
            {
                Descriptor("Self", @"Root\Line1"),
                Descriptor("RealChild", @"Root\Line1\StackA"),
            });

        var result = await world.Reader.BrowseAsync(DataSourceId, @"Root\Line1", CancellationToken.None);

        Assert.Equal(["RealChild"], result.Select(r => r.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    public async Task BrowseAsync_на_корені_не_фільтрує_за_шляхом()
    {
        // `parentPath is null` — корінь; порожній/пробільний рядок теж
        // означає корінь (`IsNullOrWhiteSpace` у джерелі), не "нічого не
        // знайдено".
        var world = new World();
        world.Source.DiscoverAsync(DataSourceId, Arg.Any<CancellationToken>())
            .Returns(new List<SourceEntityDescriptor>
            {
                Descriptor("A", @"Root\Line1"),
                Descriptor("B", @"Root\Line2\StackC"),
            });

        var result = await world.Reader.BrowseAsync(DataSourceId, parentPath: "   ", CancellationToken.None);

        Assert.Equal(["A", "B"], result.Select(r => r.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    public async Task AttributesAsync_виключає_вузли_типу_Element()
    {
        const string path = @"Root\Line1\StackA";
        var world = new World();
        world.Source.DiscoverAsync(DataSourceId, Arg.Any<CancellationToken>())
            .Returns(new List<SourceEntityDescriptor>
            {
                Descriptor("Temperature", path, dataType: "Double"),
                // Дочірній елемент дерева на тому самому шляху — атрибутом не є.
                Descriptor("SubElement", path, dataType: "Element"),
            });

        var result = await world.Reader.AttributesAsync(DataSourceId, path, CancellationToken.None);

        Assert.Equal(["Temperature"], result.Select(r => r.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    public async Task BrowseAsync_обрізає_рівень_на_MaxNodesPerLevel()
    {
        var world = new World();
        var descriptors = Enumerable.Range(0, PiAfCatalogReader.MaxNodesPerLevel + 50)
            .Select(i => Descriptor($"N{i:D5}", @"Root\Line1\Child"))
            .ToList();
        world.Source.DiscoverAsync(DataSourceId, Arg.Any<CancellationToken>()).Returns(descriptors);

        var result = await world.Reader.BrowseAsync(DataSourceId, @"Root\Line1", CancellationToken.None);

        Assert.Equal(PiAfCatalogReader.MaxNodesPerLevel, result.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    public async Task BrowseAsync_кидає_ECR_INT_0503_якщо_джерело_не_існує_або_вимкнене()
    {
        var world = new World();
        world.Store.FindDataSourceAsync(DataSourceId, Arg.Any<CancellationToken>())
            .Returns((DataSource?)null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Reader.BrowseAsync(DataSourceId, null, CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-257")]
    [Trait("Requirement", "ФВ-11.2")]
    public async Task BrowseAsync_кидає_ECR_INT_0503_якщо_транспорт_не_зареєстровано()
    {
        var world = new World();
        // Джерело налаштоване на транспорт, для якого немає зареєстрованого
        // адаптера (вибір адаптера — за транспортом джерела, ФВ-11.2).
        world.Source.Transport.Returns(ExternalTransport.PiSqlClient);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Reader.BrowseAsync(DataSourceId, null, CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
    }

    private static SourceEntityDescriptor Descriptor(string code, string? path, string dataType = "Double") =>
        new(code, DisplayName: code, EntityPath: path, SourceUnitSymbol: null, DataType: dataType);

    /// <summary>Мінімальне оточення читача каталогу: одне джерело, один адаптер.</summary>
    private sealed class World
    {
        public World(ExternalTransport transport = ExternalTransport.PiWebApi)
        {
            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
                transport,
                "https://pi.example",
                "PiAf.Primary");

            Store.FindDataSourceAsync(DataSourceId, Arg.Any<CancellationToken>()).Returns(dataSource);
            Source.Transport.Returns(transport);

            Reader = new PiAfCatalogReader([Source], Store);
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IExternalDataSource Source { get; } = Substitute.For<IExternalDataSource>();

        public PiAfCatalogReader Reader { get; }
    }
}
