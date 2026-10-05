// tests/Ecr.Application.Tests/Sources/SourceEntityHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Заведення сутності збору з вебу (<c>ФВ-13.11</c>) і прив'язка її до
/// довідника (<c>ФВ-8.11</c>).
/// </summary>
/// <remarks>
/// ⛔ До <see cref="CreateSourceEntityHandler"/> <c>new SourceEntity(</c> був лише
/// в тестах і сіді: сутність збору з вебу не заводилась узагалі.
/// </remarks>
public sealed class SourceEntityHandlersTests
{
    private const int DataSourceId = 3;
    private const int EntityId = 11;
    private const int RegistryId = 70;

    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();
    private readonly IDataSourceStore _dataSources = Substitute.For<IDataSourceStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly SourceEntity _entity = new(DataSourceId, "Flare_01", RegistrySourceKind.External);
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISourceCatalogReader _catalog = Substitute.For<ISourceCatalogReader>();

    public SourceEntityHandlersTests()
    {
        _user.UserId.Returns(9);

        // Підробка UoW виконує замикання транзакції, інакше запис і журнал (ФВ-12.10) не запустилися б.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        // ⚠ Registry.EditData — бо прив'язка вимагає права на дані довідника
        // (D-202, доповнення 2026-09-29); без нього перевіряють окремі тести нижче.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Permission("Registry.EditData").Build());

        _dataSources.FindAsync(DataSourceId, Arg.Any<CancellationToken>())
            .Returns(new DataSource(
                EcrCode.Create("PI_MAIN"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "PI" }),
                ExternalTransport.PiWebApi, "https://example.test", "secret"));

        _sources.AddSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<SourceEntity>());

        _sources.FindSourceEntityAsync(EntityId, Arg.Any<CancellationToken>()).Returns(_entity);
        _sources.RegistryDefExistsAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(true);

        // Корінь каталогу з'єднання — база AF \\AF\ECR (L9-27); шляхи команд за замовчуванням — у ній.
        _catalog.BrowseAsync(Arg.Any<int>(), null, Arg.Any<CancellationToken>())
            .Returns([Root(@"\\AF\ECR\Area_1"), Root(@"\\af\ecr\Area_2")]);
    }

    private CreateSourceEntityHandler Create()
        => new(_sources, _dataSources, _access, _user, _uow, _audit, _clock, _catalog, new SourceCatalogPolicy(TimeSpan.FromSeconds(5)));

    private static SourceEntityDescriptor Root(string path)
        => new(path[(path.LastIndexOf('\\') + 1)..], null, path, SourceUnitSymbol: null, DataType: "Element");

    private BindSourceEntityRegistryHandler Bind() => new(_sources, _access, _user, _uow, _audit, _clock);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.10")]
    public async Task ФВ_12_10_заведення_і_прив_язка_пишуть_журнал_зі_старим_і_новим_станом_повтор_без_змін_ні()
    {
        await Create().HandleAsync(Command(), CancellationToken.None);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "ext.SourceEntity" && r.Operation == CreateSourceEntityHandler.AuditOperation
                && r.OldJson == null && r.NewJson!.Contains("Flare_01", StringComparison.Ordinal) && r.ChangedByUserId == 9),
            Arg.Any<CancellationToken>());

        _audit.ClearReceivedCalls();
        await Bind().HandleAsync(EntityId, RegistryId, CancellationToken.None);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.Operation == BindSourceEntityRegistryHandler.AuditOperation
                && r.OldJson!.Contains("\"registryDefId\":null", StringComparison.Ordinal)
                && r.NewJson!.Contains($"\"registryDefId\":{RegistryId}", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // Той самий довідник вдруге — стан не змінився, шуму в журналі немає.
        _audit.ClearReceivedCalls();
        await Bind().HandleAsync(EntityId, RegistryId, CancellationToken.None);
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    private static CreateSourceEntityCommand Command(
        string? code = "Flare_01", string? path = @"\\AF\ECR\Flare_01", RegistrySourceKind? kind = null)
        => new(DataSourceId, code, "Flare 01", path, kind);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Сутність_збору_заводиться_з_позиції_каталогу()
    {
        var dto = await Create().HandleAsync(Command(code: "  Flare_01 "), CancellationToken.None);

        Assert.Equal("Flare_01", dto.Code);
        Assert.Equal(@"\\AF\ECR\Flare_01", dto.EntityPath);
        Assert.Equal("Flare 01", dto.DisplayName);
        Assert.Equal(RegistrySourceKind.External, dto.SourceKind);
        Assert.True(dto.IsActive);
        Assert.Null(dto.RegistryDefId);

        await _sources.Received(1).AddSourceEntityAsync(
            Arg.Is<SourceEntity>(e => e.Code == "Flare_01" && e.EntityPath == @"\\AF\ECR\Flare_01"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Названий_SourceKind_зберігається()
    {
        var dto = await Create().HandleAsync(Command(kind: RegistrySourceKind.Hybrid), CancellationToken.None);

        Assert.Equal(RegistrySourceKind.Hybrid, dto.SourceKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Дубль_коду_в_з_єднанні_дає_конфлікт_і_не_пише()
    {
        // ⚠ Мутаційно НЕ доведено (класифікатор / рішення людини 2026-09-28).
        // Очікування, не перевірене прогоном: без перевірки
        // `SourceEntityCodeExistsAsync` у `CreateSourceEntityHandler` запис
        // дійшов би до сховища, і в базі відмову дав би UQ_SourceEntity як збій, а не 409.
        _sources.SourceEntityCodeExistsAsync(Arg.Any<int>(), "Flare_01", Arg.Any<CancellationToken>())
            .Returns(true);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(Command(), CancellationToken.None));

        Assert.Equal(ErrorCodes.EntityFieldMapStateConflict, ex.ErrorCode);
        Assert.Equal("err.ECR-INT-0409.sourceEntityDuplicate", ex.Details!["messageKey"]);
        Assert.Equal("PI_MAIN", ex.Details!["dataSource"]);
        await _sources.DidNotReceive().AddSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("x201")]
    public async Task Порожній_чи_задовгий_код_відхиляється(string? code)
    {
        var value = code == "x201" ? new string('x', CreateSourceEntityHandler.MaxCodeLength + 1) : code;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(Command(code: value), CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.sourceEntityInvalid", ex.Details!["messageKey"]);
        await _sources.DidNotReceive().AddSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Невідомий_SourceKind_відхиляється()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(Command(kind: (RegistrySourceKind)9), CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.sourceEntityInvalid", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Неіснуюче_з_єднання_дає_404()
    {
        _dataSources.FindAsync(DataSourceId, Arg.Any<CancellationToken>()).Returns((DataSource?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Create().HandleAsync(Command(), CancellationToken.None));

        Assert.Equal("err.ECR-INT-0404.dataSource", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Без_права_заведення_не_доходить_до_сховища()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Create().HandleAsync(Command(), CancellationToken.None));

        await _dataSources.DidNotReceive().FindAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _sources.DidNotReceive().AddSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Прив_язка_і_відв_язка_довідника_зберігаються()
    {
        var bound = await Bind().HandleAsync(EntityId, RegistryId, CancellationToken.None);

        Assert.Equal(RegistryId, bound.RegistryDefId);
        Assert.Equal(RegistryId, _entity.RegistryDefId);

        var unbound = await Bind().HandleAsync(EntityId, null, CancellationToken.None);

        Assert.Null(unbound.RegistryDefId);
        // ⚠ За посиланням: `Entity<int>` без Id не дорівнює навіть самому собі.
        await _sources.Received(2).SaveSourceEntityAsync(
            Arg.Is<SourceEntity>(e => ReferenceEquals(e, _entity)), Arg.Any<CancellationToken>());

        // Відв'язка не питає довідник: питати нема про що.
        await _sources.Received(1).RegistryDefExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Прив_язка_до_неіснуючого_довідника_дає_404_і_не_пише()
    {
        // ⚠ Мутаційно НЕ доведено (класифікатор / рішення людини 2026-09-28).
        // Очікування, не перевірене прогоном: без перевірки `RegistryDefExistsAsync`
        // у `BindSourceEntityRegistryHandler` прив'язка писалася б, і в базі
        // відмову дав би FK_SE_Registry як збій, а не 404.
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync(EntityId, RegistryId + 1, CancellationToken.None));

        Assert.Equal(ErrorCodes.RegistryEntryNotFound, ex.ErrorCode);
        Assert.Equal("err.ECR-REG-0404.registryId", ex.Details!["messageKey"]);
        Assert.Null(_entity.RegistryDefId);
        await _sources.DidNotReceive().SaveSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Прив_язка_неіснуючої_сутності_дає_404()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync(EntityId + 1, RegistryId, CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceEntityNotFound, ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Прив_язка_без_права_не_міняє_нічого()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Bind().HandleAsync(EntityId, RegistryId, CancellationToken.None));

        Assert.Null(_entity.RegistryDefId);
        await _sources.DidNotReceive().SaveSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    [InlineData(null)]
    [InlineData(GrantLevel.Read)]
    public async Task Прив_язка_без_права_на_дані_довідника_відмовляє_і_не_пише(GrantLevel? grant)
    {
        // D-202 (доповнення 2026-09-29): синк пише в довідник від svc-integration,
        // тож одного Integration.Manage мало. Грант Read — не Write.
        Profile(grant is { } level ? b => b.Grant(ResourceKind.Registry, RegistryId, level) : _ => { });

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Bind().HandleAsync(EntityId, RegistryId, CancellationToken.None));

        Assert.Equal("Registry.EditData", ex.Details!["permission"]);
        Assert.Null(_entity.RegistryDefId);
        await _sources.DidNotReceive().SaveSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Грант_Write_на_цільовий_довідник_достатній_для_прив_язки()
    {
        Profile(b => b.Grant(ResourceKind.Registry, RegistryId, GrantLevel.Write));

        var bound = await Bind().HandleAsync(EntityId, RegistryId, CancellationToken.None);

        Assert.Equal(RegistryId, bound.RegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Відв_язка_і_переприв_язка_вимагають_права_на_поточний_довідник()
    {
        const int Other = RegistryId + 5;
        _sources.RegistryDefExistsAsync(Other, Arg.Any<CancellationToken>()).Returns(true);
        _entity.BindRegistry(RegistryId);

        // Право є лише на НОВИЙ довідник — поточний (RegistryId) не дозволено зняти.
        Profile(b => b.Grant(ResourceKind.Registry, Other, GrantLevel.Write));

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Bind().HandleAsync(EntityId, null, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Bind().HandleAsync(EntityId, Other, CancellationToken.None));

        Assert.Equal(RegistryId, _entity.RegistryDefId);
        await _sources.DidNotReceive().SaveSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());

        // Контроль: з правом на обидва переприв'язка проходить.
        Profile(b => b.Grant(ResourceKind.Registry, Other, GrantLevel.Write)
            .Grant(ResourceKind.Registry, RegistryId, GrantLevel.Write));

        var moved = await Bind().HandleAsync(EntityId, Other, CancellationToken.None);

        Assert.Equal(Other, moved.RegistryDefId);
    }

    /// <summary>
    /// AN-40 / L9-27: шлях AF з бази, якої немає в корені каталогу з'єднання, — 422 і нічого не записано.
    /// </summary>
    /// <remarks>
    /// Мутація (лише локально): в <c>RequirePathOfSourceAsync</c> завжди <c>return</c> — сутність заводиться з шляхом
    /// чужої бази, червоніє цей тест.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Audit", "L9-27")]
    public async Task L9_27_шлях_чужої_бази_AF_дає_422_а_своєї_бази_заводиться()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(Command(path: @"\\AF\OTHER\Flare_01"), CancellationToken.None));

        Assert.Equal(
            ("ECR-REQ-0422", "err.ECR-REQ-0422.sourceEntityPathForeign"),
            (refused.ErrorCode, refused.Details!["messageKey"]));
        Assert.Equal(@"\\AF\OTHER\Flare_01", refused.Details["path"]);
        await _sources.DidNotReceiveWithAnyArgs().AddSourceEntityAsync(default!, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);

        // Регістр імені бази AF не важить; атрибут елемента своєї бази — теж свій.
        var own = await Create().HandleAsync(Command(path: @"\\af\Ecr\Area_1\Flare_01|Flow"), CancellationToken.None);

        Assert.Equal(@"\\af\Ecr\Area_1\Flare_01|Flow", own.EntityPath);
    }

    /// <summary>
    /// L9-27: перевірка не робить заведення залежним від PI — корінь не прочитано, шлях не AF чи корінь порожній —
    /// сутність заводиться, як і до виправлення.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Audit", "L9-27")]
    public async Task L9_27_без_кореня_каталогу_чи_для_шляху_не_AF_перевірка_не_блокує()
    {
        // Шлях не AF (SQL-джерело) — каталог навіть не читається.
        await Create().HandleAsync(Command(code: "Sql_1", path: "dbo.Flare"), CancellationToken.None);
        await _catalog.DidNotReceiveWithAnyArgs().BrowseAsync(default, default, default);

        // Джерело лежить — «не знаємо», а не «чуже».
        _catalog.BrowseAsync(Arg.Any<int>(), null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SourceEntityDescriptor>>(_ => throw new HttpRequestException("down"));
        await Create().HandleAsync(Command(code: "Down_1", path: @"\\AF\OTHER\X"), CancellationToken.None);

        // Корінь порожній або без шляхів AF — теж без відмови.
        _catalog.BrowseAsync(Arg.Any<int>(), null, Arg.Any<CancellationToken>())
            .Returns([new SourceEntityDescriptor("Unit-01", null, @"\Db\Unit-01", null, "Element")]);
        await Create().HandleAsync(Command(code: "Rel_1", path: @"\\AF\OTHER\Y"), CancellationToken.None);

        await _sources.Received(3).AddSourceEntityAsync(Arg.Any<SourceEntity>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Audit", "L9-27")]
    [InlineData(@"\\SRV\DB\El", @"\\SRV\DB")]
    [InlineData(@"\\SRV\DB\El\Child|Attr", @"\\SRV\DB")]
    [InlineData(@"\\SRV\DB", null)]
    [InlineData(@"\\SRV\\El", null)]
    [InlineData(@"\SRV\DB\El", null)]
    [InlineData("dbo.Flare", null)]
    [InlineData(null, null)]
    public void L9_27_база_AF_шляху(string? path, string? database)
        => Assert.Equal(database, CreateSourceEntityHandler.AfDatabaseOf(path));

    /// <summary>Профіль з Integration.Manage, але без глобального Registry.EditData.</summary>
    private void Profile(Action<AccessBuilder> configure)
    {
        var builder = new AccessBuilder { UserId = 9 }.Permission("Integration.Manage");
        configure(builder);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }
}
