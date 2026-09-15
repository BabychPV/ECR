// tests/Ecr.Application.Tests/Documents/RequiredByMethodologyColumnTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// <c>ColumnDto.IsRequiredByMethodology</c> — сигнал НА СІТЦІ, ДО спроби
/// зберегти, що колонка є обов'язковим входом чинної методології (директива
/// «правила методологій на сторінці»). До цього поля не існувало взагалі:
/// оператор дізнавався про вимогу лише ПІСЛЯ відхиленого `PATCH /cells`
/// (`PatchCellsHandler.EnforceRequiredInputsAsync`, `ECR-CALC-0437`).
/// </summary>
public sealed class RequiredByMethodologyColumnTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const int CategoryId = 12;
    private const int MethodologyId = 100;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();

    public RequiredByMethodologyColumnTests()
    {
        var volume = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);

        var category = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Category"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Category" }), 2, CellDataType.String);
        SetId(category, CategoryId);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        table.AddColumn(volume);
        table.AddColumn(category);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = volume, [CategoryId] = category },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long>());
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());

        _periods.FindPeriodBoundsAsync(Document, Period, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static IUnitCatalog Units()
    {
        var catalogue = Substitute.For<IUnitCatalog>();
        catalogue.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        return catalogue;
    }

    /// <summary>Порожній каталог стилів (директива registry-lookup / cell-style, PR B2) — не предмет цих тестів.</summary>
    private static IStyleCatalog Styles()
    {
        var catalogue = Substitute.For<IStyleCatalog>();
        catalogue.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, StyleDef>());
        return catalogue;
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private GetTableSliceHandler Handler()
        => new(_rows, _cells, _metadata, Units(), _access, _methodologies, _periods, Styles());

    /// <summary>Методологія, чинна для таблиці, з обов'язковим входом <c>Category</c>.</summary>
    /// <param name="withRule">
    /// Версія без жодного правила прив'язки ніколи не входить у gate
    /// `PatchCellsHandler.ResolveApplicableAsync` — тест
    /// <see cref="Методологія_без_правила_прив_язки_не_позначає_колонку"/>
    /// перевіряє, що позначка на сітці не бреше про це.
    /// </param>
    private void WithMethodology(bool withRule = true)
    {
        var version = new MethodologyVersion(MethodologyId, "1.0", CalculationLevel.Configuration, 1, Now);
        var rules = withRule
            ? (IReadOnlyList<MethodologyRule>)[version.AddRule(EcrCode.Create("all"), "{}", 100)]
            : [];
        var requiredInput = version.AddRequiredInput(CategoryId, RequiredInputSeverity.Block, hint: null);
        version.Publish(publishedByUserId: 2, "тестова публікація", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([MethodologyId]));
        _methodologies.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyVersion>>([version]));
        _methodologies.GetRulesAsync(version.Id, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(rules));
        _methodologies.GetRequiredInputsAsync(version.Id, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyRequiredInput>>([requiredInput]));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Обовязковий_вхід_чинної_методології_позначає_колонку()
    {
        WithMethodology();

        var slice = await Handler().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        var category = slice.Columns.Single(c => c.Code == "Category");
        var volume = slice.Columns.Single(c => c.Code == "Volume");

        Assert.True(category.IsRequiredByMethodology);
        Assert.False(volume.IsRequiredByMethodology);

        // ⚠ Вісь `IsRequired` (загальна обов'язковість) не зачеплена: колонки
        // з методологічною вимогою можуть не бути `ColumnDef.IsRequired`.
        Assert.False(category.IsRequired);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Без_привязаної_методології_жодна_колонка_не_позначена()
    {
        // ⚠ Фікстура вже підставляє порожній `GetMethodologyIdsBoundToTableAsync`
        // за замовчуванням (NSubstitute) — жоден метод крім нього не має
        // бути викликаний, інакше fast-path зламано.
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var slice = await Handler().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.All(slice.Columns, c => Assert.False(c.IsRequiredByMethodology));
        await _methodologies.DidNotReceive().GetPublishedVersionsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Методологія_без_правила_прив_язки_не_позначає_колонку()
    {
        // ⛔ Версія з обов'язковим входом, але БЕЗ жодного правила прив'язки,
        // ніколи не проходить `PatchCellsHandler.ResolveApplicableAsync`
        // (`rules.Count == 0` → методологія пропускається цілком) — тобто
        // вимога, яку вона нібито несе, НІКОЛИ не enforced на записі. Зірочка
        // тут була б брехнею.
        WithMethodology(withRule: false);

        var slice = await Handler().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.All(slice.Columns, c => Assert.False(c.IsRequiredByMethodology));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Без_відомих_меж_періоду_сітка_читається_без_позначки()
    {
        WithMethodology();
        _periods.FindPeriodBoundsAsync(Document, Period, Arg.Any<CancellationToken>())
                .Returns((PeriodBounds?)null);

        var slice = await Handler().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        // ⚠ Немає меж періоду — не привід відмовити в читанні сітки: лише
        // позначка методології лишається відсутньою.
        Assert.All(slice.Columns, c => Assert.False(c.IsRequiredByMethodology));
    }
}
