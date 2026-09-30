// tests/Ecr.Application.Tests/Registries/RegistryCompositionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Композиція довідників (RT-12, <c>ФВ-8.16</c>, <c>D-155</c>, FEATURE-REGISTRY-TABLES §4.8):
/// обмеження опису, видимість частин за батьком — на рівні застосунку, без бази.
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item><c>RegistryCompositionRules.Validate</c> без пошуку кола (<c>FindCycle</c> → <c>null</c>) →
/// <see cref="Цикл_композиції_відхиляється"/> і <see cref="Коло_через_третій_довідник_відхиляється"/>
/// червоні (збереження проходить);</item>
/// <item><c>RegistryCompositionRules.Compose</c> без перевірки типу →
/// <see cref="Композиція_на_не_Lookup_422_до_домену"/> червоний (домен кидає
/// <see cref="InvalidOperationException"/>, тобто 500);</item>
/// <item><c>RegistryResolver.VisibleWithCompositionParents</c> без переходу до батька (частина видима
/// сама по собі) → <see cref="Частина_невидима_коли_невидимий_батько"/> червоний.</item>
/// </list>
/// </remarks>
public sealed class RegistryCompositionTests
{
    private const int StreamId = 31;
    private const int CaseId = 32;
    private const int CompositionId = 33;

    private static readonly DateTime Now = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();

    public RegistryCompositionTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);

        var profile = new AccessBuilder { UserId = 9 }
            .Permission("Registry.View")
            .Permission("Registry.EditDefinition")
            .Permission("Registry.Publish")
            .Build();
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    [Trait("Requirement", "ФВ-8.4")]
    public async Task Цикл_композиції_відхиляється()
    {
        // STREAM — частина STREAM_CASE, STREAM_CASE — частина STREAM: коло з двох ребер. Дані
        // могли прийти повз опис; збереження опису такий граф не приймає.
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId);
        Compose(stream, 311, "CASE", CaseId);
        Compose(@case, 321, "STREAM", StreamId);
        Known(stream, @case);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM", Request(stream), default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.compositionCycle", error.Details!["messageKey"]);
        Assert.Equal("STREAM → STREAM_CASE → STREAM", error.Details["chain"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Коло_через_третій_довідник_відхиляється()
    {
        // Опис кожного з трьох окремо «правильний»: коло видно лише на графі.
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId);
        var composition = Registry("GAS_COMPOSITION", CompositionId);
        Compose(@case, 321, "STREAM", StreamId);
        Compose(composition, 331, "CASE", CaseId);
        Compose(stream, 311, "PART_OF", CompositionId);
        Known(stream, @case, composition);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM", Request(stream), default));

        Assert.Equal("err.ECR-REG-0422.compositionCycle", error.Details!["messageKey"]);
        Assert.Equal("STREAM → GAS_COMPOSITION → STREAM_CASE → STREAM", error.Details["chain"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Ланцюжок_без_кола_зберігається()
    {
        // Контроль до двох тестів вище: три рівні (потік → кейс → склад) — законний опис.
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId);
        var composition = Registry("GAS_COMPOSITION", CompositionId);
        Compose(@case, 321, "STREAM", StreamId);
        Compose(composition, 331, "CASE", CaseId);
        Known(stream, @case, composition);

        var version = await Saves().HandleAsync("GAS_COMPOSITION", Request(composition), default);

        Assert.Equal(2, version);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public void Композиція_на_не_Lookup_422_до_домену()
    {
        var field = new RegistryFieldDef(CaseId, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);

        var error = Assert.Throws<BusinessRuleException>(
            () => RegistryCompositionRules.Compose(field, ParentDeletePolicy.Cascade));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.compositionNotLookup", error.Details!["messageKey"]);
        Assert.Equal("NAME", error.Details["fieldCode"]);
        Assert.Equal("String", error.Details["dataType"]);
        Assert.Equal(RegistryRelationKind.Reference, field.RelationKind);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    [InlineData("moreThanOne", "err.ECR-REG-0422.compositionMoreThanOne")]
    [InlineData("self", "err.ECR-REG-0422.compositionTargetSelf")]
    [InlineData("optional", "err.ECR-REG-0422.compositionNotRequired")]
    [InlineData("temporal", "err.ECR-REG-0422.compositionChildTemporal")]
    public async Task Опис_композиції_поза_правилами_відхиляється(string breach, string messageKey)
    {
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId, isTemporal: breach == "temporal");
        Compose(@case, 321, "STREAM", breach == "self" ? CaseId : StreamId, required: breach != "optional");
        if (breach == "moreThanOne")
        {
            Compose(@case, 322, "STREAM_TOO", StreamId);
        }

        Known(stream, @case);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM_CASE", Request(@case), default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public void Частина_невидима_коли_невидимий_батько()
    {
        // Потік 1 закритий датою (сам невидимий) → його кейс 10 і рядок складу 100 невидимі,
        // хоч самі обираються. Потік 2 видимий → кейс 20 і рядок 200 видимі. Рядок 300 без
        // батька (поле композиції порожнє) невидимий. 400 ↔ 401 — коло в даних: невидимі, а
        // не нескінченна рекурсія.
        var selectable = new HashSet<long> { 2, 10, 20, 100, 200, 300, 400, 401 };
        var parents = new Dictionary<long, long?>
        {
            [10] = 1, [20] = 2, [100] = 10, [200] = 20, [300] = null, [400] = 401, [401] = 400,
        };

        var visible = new RegistryResolver().VisibleWithCompositionParents(
            selectable.Contains,
            id => parents.TryGetValue(id, out var parent) ? (true, parent) : (false, null));

        Assert.Equal(
            new long[] { 2, 20, 200 },
            new long[] { 1, 2, 10, 20, 100, 200, 300, 400, 401 }.Where(visible).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Нове_поле_композиції_не_Lookup_через_опис_422()
    {
        // RT-11: `relationKind` приходить запитом опису, і `ApplyFields` кличе
        // `RegistryCompositionRules.Compose`, а не `ComposeInto` домену: інакше поле String з
        // композицією давало б InvalidOperationException, тобто 500 замість 422.
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId);
        Known(stream, @case);

        var request = Request(@case) with
        {
            Fields =
            [
                .. Request(@case).Fields,
                new RegistryFieldSaveDto(
                    null, "STREAM", Text("STREAM"), nameof(CellDataType.String), 2, IsRequired: true,
                    IsKey: false, LookupRegistryDefId: StreamId, UnitId: null,
                    RelationKind: RegistryRelationKind.Composition, OnParentDelete: ParentDeletePolicy.Cascade),
            ],
        };

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM_CASE", request, default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.compositionNotLookup", error.Details!["messageKey"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Нове_поле_композиції_Lookup_через_опис_стає_частиною_батька()
    {
        // RT-11: порожній довідник — нове поле композиції обов'язкове (§4.8) і приймається.
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId);
        Known(stream, @case);

        var request = Request(@case) with
        {
            Fields =
            [
                .. Request(@case).Fields,
                new RegistryFieldSaveDto(
                    null, "STREAM", Text("STREAM"), nameof(CellDataType.Lookup), 2, IsRequired: true,
                    IsKey: false, LookupRegistryDefId: StreamId, UnitId: null,
                    RelationKind: RegistryRelationKind.Composition, OnParentDelete: ParentDeletePolicy.Cascade),
            ],
            CodeMode = RegistryCodeMode.Auto,
        };

        await Saves().HandleAsync("STREAM_CASE", request, default);

        var field = Assert.Single(@case.Fields, f => f.Code == "STREAM");
        Assert.Equal(RegistryRelationKind.Composition, field.RelationKind);
        Assert.Equal(ParentDeletePolicy.Cascade, field.OnParentDelete);
        Assert.Equal(RegistryCodeMode.Auto, @case.CodeMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Відношення_наявного_поля_не_змінюється()
    {
        // Наявне посилання, яке раптом стало композицією, сховало б наявні записи без батька.
        var stream = Registry("STREAM", StreamId);
        var @case = Registry("STREAM_CASE", CaseId);
        var link = new RegistryFieldDef(CaseId, EcrCode.Create("STREAM"), Text("STREAM"), CellDataType.Lookup, 2);
        SetId(link, 322);
        link.PointTo(StreamId);
        @case.AddField(link);
        Known(stream, @case);

        var request = Request(@case);
        request = request with
        {
            Fields = [.. request.Fields.Select(f => f.Id == 322 ? f with { RelationKind = RegistryRelationKind.Composition } : f)],
        };

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM_CASE", request, default));

        Assert.Equal("err.ECR-REG-0422.relationKindImmutable", error.Details!["messageKey"]);
        Assert.Equal(RegistryRelationKind.Reference, link.RelationKind);
    }

    private SaveRegistryDefinitionHandler Saves()
        => new(_registries, _uow, _audit, _access, _user, _clock, _units, _keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(_keys, _uow));

    private void Known(params RegistryDef[] registries)
    {
        foreach (var registry in registries)
        {
            _registries.FindDefinitionAsync(registry.Code, Arg.Any<CancellationToken>()).Returns(registry);
        }

        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns(registries);
    }

    /// <summary>Довідник із ключовим полем <c>NAME</c> (без ключового поля опис не зберігається).</summary>
    private static RegistryDef Registry(string code, int id, bool isTemporal = false)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal);
        SetId(registry, id);

        var name = new RegistryFieldDef(id, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);
        SetId(name, (id * 10) + 9);
        name.MarkKey(true);
        registry.AddField(name);
        return registry;
    }

    /// <summary>Поле композиції: <c>Lookup</c> на батька, обов'язкове, <c>Cascade</c>.</summary>
    private static void Compose(RegistryDef child, int fieldId, string code, int parentId, bool required = true)
    {
        var field = new RegistryFieldDef(child.Id, EcrCode.Create(code), Text(code), CellDataType.Lookup, fieldId);
        SetId(field, fieldId);
        field.Update(Text(code), fieldId, required);
        field.PointTo(parentId);
        field.ComposeInto(ParentDeletePolicy.Cascade);
        child.AddField(field);
    }

    /// <summary>Запит збереження, що лишає опис як є (зміна — лише причина).</summary>
    private static SaveRegistryDefinitionDto Request(RegistryDef registry)
        => new(
            [.. registry.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldSaveDto(
                f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal,
                f.IsRequired, f.IsKey, f.RefRegistryDefId, f.UnitId))],
            [],
            "RT-12");

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
