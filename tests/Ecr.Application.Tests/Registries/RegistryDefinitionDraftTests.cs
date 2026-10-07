// tests/Ecr.Application.Tests/Registries/RegistryDefinitionDraftTests.cs
using System.Text.Json;
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

/// <summary>Чернетка опису довідника і її публікація (<c>BE-24</c> крок 2).</summary>
public sealed class RegistryDefinitionDraftTests
{
    private const int RegistryId = 4;
    private static readonly DateTime Now = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Version1 = [0, 0, 0, 0, 0, 0, 0, 1];

    /// <summary>`If-Match` із версією опису 1 — тією, що в довіднику тесту.</summary>
    private const string IfMatch1 = "\"1\"";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryDraftStore _drafts = Substitute.For<IRegistryDraftStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    // HSE301 U1: поля тут без одиниць, довідник одиниць не читається.
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly RegistryDef _registry;

    public RegistryDefinitionDraftTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);

        _registry = new RegistryDef(EcrCode.Create("PERMIT"), Text("PERMIT"), isTemporal: false);
        SetId(_registry, RegistryId);
        var key = new RegistryFieldDef(RegistryId, EcrCode.Create("Number"), Text("Number"), CellDataType.String, 1);
        SetId(key, 41);
        key.MarkKey(true);
        _registry.AddField(key);

        _registries.FindDefinitionAsync("PERMIT", Arg.Any<CancellationToken>()).Returns(_registry);
        _registries.ListRulesAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryRuleDef>());

        Allow("Registry.View", "Registry.EditDefinition", "Registry.Publish");
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Збереження_чернетки_не_змінює_опублікований_опис()
    {
        RegistryDefinitionDraft? added = null;
        _drafts.Add(Arg.Do<RegistryDefinitionDraft>(d => added = d));

        await SaveDraft().HandleAsync("PERMIT", DraftRequest("Renamed", rowVersion: null), default, IfMatch1);

        Assert.Equal("Number", _registry.Fields[0].NameL10n.Get("en"));
        Assert.Equal(1, _registry.DefinitionVersion);
        Assert.NotNull(added);
        Assert.Equal(1, added.BaseDefinitionVersion);
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "cfg.RegistryDefinitionDraft"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    [Trait("Requirement", "L5-02")]
    public async Task Збереження_чернетки_з_чужою_версією_опису_409_і_нічого_не_пише()
    {
        // L5-02: клієнт будував форму з версії 1, а опис уже опублікували (версія 2). Без звірки збереження
        // перебазувало б чернетку на 2, і публікація мовчки вимкнула б правила/ключі сусіда.
        _registry.BumpDefinitionVersion();

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => SaveDraft().HandleAsync("PERMIT", DraftRequest("Renamed", rowVersion: null), default, IfMatch1));

        Assert.Equal("err.ECR-REG-0409.definitionChanged", ex.Details!["messageKey"]);
        _drafts.DidNotReceive().Add(Arg.Any<RegistryDefinitionDraft>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    [Trait("Requirement", "L5-02")]
    public async Task Збереження_чернетки_без_If_Match_422()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => SaveDraft().HandleAsync("PERMIT", DraftRequest("Renamed", rowVersion: null), default));

        Assert.Equal("err.ECR-REQ-0422.definitionVersionRequired", ex.Details!["messageKey"]);
        _drafts.DidNotReceive().Add(Arg.Any<RegistryDefinitionDraft>());
    }

    [Theory]
    [InlineData(4000, false)]
    [InlineData(4001, true)]
    [Trait("Requirement", "A4-03")]
    public async Task Вираз_правила_довший_за_колонку_422_а_рівно_на_межі_зберігається(int length, bool rejected)
    {
        // A4-03: cfg.RegistryRuleDef.Expression — 4000; довший вираз давав 500 (SQL truncated) замість 422.
        var expression = new string('1', length);
        var request = DraftRequest("Number", rowVersion: null) with
        {
            Rules = [new RegistryRuleSaveDto(null, "R1", "Expression", expression, "Error", Text("m"), null, true)],
        };

        if (!rejected)
        {
            await SaveDraft().HandleAsync("PERMIT", request, default, IfMatch1);
            return;
        }

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => SaveDraft().HandleAsync("PERMIT", request, default, IfMatch1));

        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", ex.Details!["messageKey"]);
        Assert.Equal("4000", ex.Details["max"]);
        Assert.Equal("4001", ex.Details["length"]);
        _drafts.DidNotReceive().Add(Arg.Any<RegistryDefinitionDraft>());
    }
    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Публікація_застосовує_чернетку_і_прибирає_її()
    {
        var draft = Draft("Renamed", baseVersion: 1);

        var version = await Publish().HandleAsync("PERMIT", new(Convert.ToBase64String(Version1)), default);

        Assert.Equal(2, version);
        Assert.Equal("Renamed", _registry.Fields[0].NameL10n.Get("en"));
        _drafts.Received(1).Remove(draft);
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.Operation == "PublishDefinition" && r.ChangeReason == "нова назва"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Без_права_Registry_Publish_публікація_відхиляється()
    {
        Allow("Registry.View", "Registry.EditDefinition");
        Draft("Renamed", baseVersion: 1);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Publish().HandleAsync("PERMIT", new(Convert.ToBase64String(Version1)), default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal("Number", _registry.Fields[0].NameL10n.Get("en"));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Пряме_збереження_опису_теж_вимагає_Registry_Publish()
    {
        Allow("Registry.View", "Registry.EditDefinition");

        var request = DraftRequest("Renamed", null);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Apply()
            .HandleAsync("PERMIT", new(request.Fields, request.Rules, request.Reason), default));
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Чернетка_несе_ключі_й_режим_коду_до_публікації()
    {
        // RT-11: ключі й режим коду — частина опису, тож і чернетки. Чернетка, що їх губить,
        // опублікувала б опис без ключа, який людина щойно завела.
        RegistryDefinitionDraft? added = null;
        _drafts.Add(Arg.Do<RegistryDefinitionDraft>(d => added = d));

        var request = DraftRequest("Number", rowVersion: null) with
        {
            Keys = [new RegistryKeySaveDto(null, "BY_NUMBER", Text("By number"), ["Number"], false, true, true)],
            CodeMode = RegistryCodeMode.Auto,
        };
        await SaveDraft().HandleAsync("PERMIT", request, default, IfMatch1);

        Assert.NotNull(added);
        typeof(RegistryDefinitionDraft).GetProperty(nameof(RegistryDefinitionDraft.RowVersion))!.SetValue(added, Version1);
        _drafts.FindAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(added);

        var state = await new GetRegistryDefinitionDraftHandler(_registries, _drafts, _access, _user)
            .HandleAsync("PERMIT", default);
        var key = Assert.Single(state.Draft!.Keys!);
        Assert.Equal(["Number"], key.FieldCodes);
        Assert.Equal(RegistryCodeMode.Auto, state.Draft.CodeMode);

        await Publish().HandleAsync("PERMIT", new(Convert.ToBase64String(Version1)), default);

        // Режим коду застосовано (записів у довіднику немає), новий ключ дійшов до опису —
        // створюється він у транзакції, коли поле збережене.
        Assert.Equal(RegistryCodeMode.Auto, _registry.CodeMode);
        _keys.Received(1).AddKey(Arg.Is<RegistryKeyDef>(k => k.Code == "BY_NUMBER"));
    }

    [Theory]
    [Trait("Directive", "BE-24")]
    [InlineData("AAAAAAAAAAI=", 1, "err.ECR-REG-0409.definitionDraftChanged")]
    [InlineData("AAAAAAAAAAE=", 0, "err.ECR-REG-0409.definitionDraftStale")]
    public async Task Чужа_версія_чернетки_або_опису_дає_409(string rowVersion, int baseVersion, string messageKey)
    {
        Draft("Renamed", baseVersion);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Publish().HandleAsync("PERMIT", new(rowVersion), default));

        Assert.Equal("ECR-REG-0409", conflict.ErrorCode);
        Assert.Equal(messageKey, conflict.Details!["messageKey"]);
        Assert.Equal("Number", _registry.Fields[0].NameL10n.Get("en"));
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Скасування_видаляє_чернетку_пише_аудит_і_не_змінює_опис()
    {
        var draft = Draft("Renamed", baseVersion: 1);

        await Discard().HandleAsync("PERMIT", Convert.ToBase64String(Version1), default);

        _drafts.Received(1).Remove(draft);
        Assert.Equal("Number", _registry.Fields[0].NameL10n.Get("en"));
        Assert.Equal(1, _registry.DefinitionVersion);
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.Operation == "DiscardDefinitionDraft"
                && r.EntityType == "cfg.RegistryDefinitionDraft" && r.OldJson == draft.ContentJson),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Скасування_без_чернетки_дає_404()
    {
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Discard().HandleAsync("PERMIT", null, default));

        Assert.Equal("err.ECR-REG-0404.definitionDraft", missing.Details!["messageKey"]);
    }

    [Theory]
    [Trait("Directive", "BE-24")]
    [InlineData("AAAAAAAAAAI=")]
    [InlineData(null)]
    public async Task Скасування_з_чужою_версією_дає_409_і_чернетка_лишається(string? rowVersion)
    {
        Draft("Renamed", baseVersion: 1);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Discard().HandleAsync("PERMIT", rowVersion, default));

        Assert.Equal("err.ECR-REG-0409.definitionDraftChanged", conflict.Details!["messageKey"]);
        _drafts.DidNotReceive().Remove(Arg.Any<RegistryDefinitionDraft>());
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public async Task Скасування_без_Registry_EditDefinition_відхиляється()
    {
        Allow("Registry.View", "Registry.Publish");
        Draft("Renamed", baseVersion: 1);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Discard().HandleAsync("PERMIT", Convert.ToBase64String(Version1), default));

        _drafts.DidNotReceive().Remove(Arg.Any<RegistryDefinitionDraft>());
    }

    private DiscardRegistryDefinitionDraftHandler Discard()
        => new(_registries, _drafts, _uow, _audit, _access, _user, _clock);

    private RegistryDefinitionDraft Draft(string name, int baseVersion)
    {
        var request = DraftRequest(name, null);
        var draft = new RegistryDefinitionDraft(
            RegistryId, baseVersion,
            JsonSerializer.Serialize(new { request.Fields, request.Rules }, WebJson),
            request.Reason, 9, Now);
        typeof(RegistryDefinitionDraft).GetProperty(nameof(RegistryDefinitionDraft.RowVersion))!.SetValue(draft, Version1);
        _drafts.FindAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(draft);
        return draft;
    }

    private static SaveRegistryDefinitionDraftRequest DraftRequest(string name, string? rowVersion)
        => new(
            [new RegistryFieldSaveDto(41, "Number", Text(name), "String", 1, false, true, null, null)],
            [],
            "нова назва",
            rowVersion);

    private SaveRegistryDefinitionDraftHandler SaveDraft()
        => new(_registries, _drafts, _uow, _audit, _access, _user, _clock);

    private PublishRegistryDefinitionHandler Publish()
        => new(_registries, _drafts, Apply(), _access, _user);

    private SaveRegistryDefinitionHandler Apply()
        => new(_registries, _uow, _audit, _access, _user, _clock, _units, _keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(_keys, _uow));

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
