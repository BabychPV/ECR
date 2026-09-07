// tests/Ecr.Application.Tests/Registries/RegistryDefinitionTests.cs
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
/// Конструктор довідника (<c>ФВ-8.12</c>): поля, зв'язки, правила, мапінг,
/// історія.
/// </summary>
/// <remarks>
/// ⚠ Трейт <c>ФВ-8.12</c> стоїть на <b>клієнтських</b> тестах
/// (<c>constructor.test.tsx</c>), бо вимога дослівно каже «UI: конструктор
/// довідника». Тут перевіряється те, без чого той екран не має що показувати:
/// що чотири області доходять до нього однією відповіддю і що збереження не
/// приймає опису, який перетлумачує вже збережені записи.
/// </remarks>
public sealed class RegistryDefinitionTests
{
    private const int PermitsId = 4;
    private const int SubstancesId = 5;

    private static readonly DateTime Now = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IAuditReader _auditReader = Substitute.For<IAuditReader>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    private readonly RegistryDef _permits;
    private readonly RegistryDef _substances;
    private readonly List<RegistryRuleDef> _rules;

    public RegistryDefinitionTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("test");

        Allow("Registry.View", "Registry.EditDefinition");

        _permits = Definition("PERMIT", PermitsId);
        _substances = Definition("SUBSTANCE", SubstancesId);

        // Поля: ключове, посилання на інший довідник (каскад), посилання на
        // себе (ієрархія) і поле, яке наповнює зовнішнє джерело.
        AddField(_permits, 41, "Number", CellDataType.String, ordinal: 1, isKey: true);
        AddField(_permits, 42, "Substance", CellDataType.Lookup, ordinal: 2, refRegistry: SubstancesId);
        AddField(_permits, 43, "Parent", CellDataType.Lookup, ordinal: 3, refRegistry: PermitsId);
        AddField(_permits, 44, "Limit", CellDataType.Decimal, ordinal: 4);

        // ⛔ Усі ЧОТИРИ види правил (директива №06 `H-10`), і саме чотири:
        // п'ятого — `ValidityWindow` — немає, бо вікно чинності це поля
        // запису, а не правило (`ФВ-8.5`).
        _rules =
        [
            Rule(101, "IndicatorRequired", RegistryRuleKind.RequiredWhen, "[Type] = 'Emission'"),
            Rule(102, "CodeUnique", RegistryRuleKind.UniqueWithin, "[Number]"),
            Rule(103, "LimitPositive", RegistryRuleKind.Expression, "[Limit] > 0"),
            Rule(104, "SubstanceExists", RegistryRuleKind.CrossRegistry, "[Substance] IN SUBSTANCE"),
        ];

        _registries.FindDefinitionAsync("PERMIT", Arg.Any<CancellationToken>()).Returns(_permits);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryDef>>([_permits, _substances]);
        _registries.ListRulesAsync(PermitsId, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<RegistryRuleDef>)_rules.ToList());
        _registries.ListFieldMappingsAsync(PermitsId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryFieldMapping>>(
            [
                new RegistryFieldMapping(
                    FieldMapId: 7, RegistryFieldDefId: 44, SourceCode: "PI_PERMITS",
                    SourceField: "Permit_Limit", TransformCode: "Last",
                    SourceUnitCode: "m3", TargetUnitCode: "km3", IsActive: true),

                // ⚠ Мапінг на поле, якого в довіднику немає: він мусить
                // відсіятися, а не приїхати рядком «поле ?».
                new RegistryFieldMapping(
                    FieldMapId: 8, RegistryFieldDefId: 999, SourceCode: "PI_PERMITS",
                    SourceField: "Ghost", TransformCode: null,
                    SourceUnitCode: null, TargetUnitCode: null, IsActive: false),
            ]);
        _registries.ListLinkKindsAsync(PermitsId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryLinkKindStat>>(
                [new RegistryLinkKindStat("permit-water-body", 12)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Опис_приходить_однією_відповіддю_з_усіма_чотирма_областями()
    {
        // ⛔ Саме це і є «конструктор довідника (поля, зв'язки, правила,
        // мапінг)»: чотири області разом. Чотири запити давали б чотири різні
        // моменти часу на одному екрані.
        var definition = await Definitions().HandleAsync("PERMIT", default);

        Assert.Equal(4, definition.Fields.Count);

        // Зв'язки ОБЧИСЛЮЮТЬСЯ: `cfg.RegistryRelationDef` у схемі немає.
        Assert.Contains(definition.Relations, r => r.Kind == "Cascade" && r.FieldCode == "Substance");
        Assert.Contains(definition.Relations, r => r.Kind == "Hierarchy" && r.FieldCode == "Parent");
        Assert.Contains(definition.Relations, r => r.Kind == "Association" && r.LinkCount == 12);

        // ⛔ Чотири види правил, і всі чотири доходять до екрана.
        Assert.Equal(
            ["CrossRegistry", "Expression", "RequiredWhen", "UniqueWithin"],
            definition.Rules.Select(r => r.RuleKind).Order(StringComparer.Ordinal).ToList());

        // Мапінг названий КОДОМ поля, а не його ідентифікатором: на екрані
        // конструктора число 44 не означає нічого.
        var mapping = Assert.Single(definition.Mappings);
        Assert.Equal("Limit", mapping.FieldCode);
        Assert.Equal("m3", mapping.SourceUnitCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Каскад_і_ієрархія_розрізняються_за_ціллю_а_не_за_назвою_поля()
    {
        // ⚠ Поле, що вказує на ВЛАСНИЙ довідник, — ієрархія; на чужий —
        // каскад. Помилка тут дала б у конструкторі дозволів «каскад на самого
        // себе», тобто фільтр, який виглядає працюючим і нічого не звужує.
        var definition = await Definitions().HandleAsync("PERMIT", default);

        var hierarchy = Assert.Single(definition.Relations, r => r.Kind == "Hierarchy");
        Assert.Equal(PermitsId, hierarchy.TargetRegistryDefId);
        Assert.Equal("PERMIT", hierarchy.TargetRegistryCode);

        var cascade = Assert.Single(definition.Relations, r => r.Kind == "Cascade");
        Assert.Equal("SUBSTANCE", cascade.TargetRegistryCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Registry_View_опис_не_читається()
    {
        Allow("Registry.EditData");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Definitions().HandleAsync("PERMIT", default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Правка_опису_вимагає_EditDefinition_а_не_EditData()
    {
        // ⛔ Це різні люди. Той, хто заводить речовину, і той, хто вирішує, що
        // в довіднику речовин узагалі є поле «клас небезпеки», — не одна роль.
        Allow("Registry.EditData", "Registry.View");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Saves().HandleAsync("PERMIT", Request(), default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Пʼятий_вид_правила_відхиляється()
    {
        // ⛔ Директива №06 `H-10`: видів рівно ЧОТИРИ. `ValidityWindow`
        // виглядає правдоподібно — він стоїть п'ятим у `reference/design/07`
        // — і саме тому мусить бути відхилений явно: вікно чинності це поля
        // запису (`ФВ-8.5`), і правило-дублер дало б два джерела істини.
        var request = Request(rules:
        [
            new RegistryRuleSaveDto(
                null, "PermitWindow", "ValidityWindow", "[ValidTo]", "Error", Text("вікно"), null, true),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", request, default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Contains("чотири", error.Message, StringComparison.Ordinal);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Вид_наявного_правила_не_змінюється()
    {
        // ⛔ Параметри і предикат означають для кожного виду різне. Зміна виду
        // при збережених параметрах дала б правило, синтаксично ціле і таке,
        // що перевіряє не те.
        var request = Request(rules:
        [
            new RegistryRuleSaveDto(
                101, "IndicatorRequired", "Expression", "[Type] = 'Emission'", "Error",
                Text("обов'язково"), null, true),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", request, default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Правило_якого_немає_в_запиті_вимикається_а_не_зникає()
    {
        // ⚠ Правило, що колись діяло, — єдине пояснення того, чому наявні
        // записи виглядають саме так. Видалене правило робить це пояснення
        // недоступним назавжди.
        var kept = _rules[0];
        var request = Request(rules:
        [
            new RegistryRuleSaveDto(
                kept.Id, kept.Code, kept.RuleKind.ToString(), kept.Expression,
                kept.Severity.ToString(), kept.MessageL10n, null, true),
        ]);

        await Saves().HandleAsync("PERMIT", request, default);

        Assert.True(_rules[0].IsActive);
        Assert.All(_rules.Skip(1), r => Assert.False(r.IsActive));
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Поле_довідника_не_видаляється()
    {
        // ⛔ `dic.RegistryValue` посилається на поле зовнішнім ключем, і
        // видалення поля означало б видалення значень — те саме тихе зникнення
        // історії, від якого захищає `ФВ-8.6`.
        var request = Request(fields: Fields().Take(3).ToList());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", request, default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal(4, _permits.Fields.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Код_і_тип_наявного_поля_не_змінюються()
    {
        // ⛔ Код — те, чим на поле посилаються вирази і мапінг; тип — те, як
        // читається колонка `dic.RegistryValue`. Обидва перетлумачують уже
        // збережені значення.
        var renamed = Fields();
        renamed[0] = renamed[0] with { Code = "PermitNumber" };

        var code = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", Request(fields: renamed), default));
        Assert.Equal("ECR-REG-0422", code.ErrorCode);

        var retyped = Fields();
        retyped[3] = retyped[3] with { DataType = "String" };

        var type = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", Request(fields: retyped), default));
        Assert.Equal("ECR-REG-0422", type.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Нове_поле_не_може_бути_обовʼязковим_одразу()
    {
        // ⚠ Наявні записи його не мають, і вимога значення зробила б увесь
        // довідник недійсним у мить збереження.
        var fields = Fields();
        fields.Add(new RegistryFieldSaveDto(
            null, "HazardClass", Text("Клас небезпеки"), "Int", 5,
            IsRequired: true, IsKey: false, LookupRegistryDefId: null, UnitId: null));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", Request(fields: fields), default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal(4, _permits.Fields.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Опис_без_ключового_поля_відхиляється()
    {
        // ⛔ Бізнес-ключ запису не було б із чого скласти: ані зіставити з
        // зовнішнім джерелом, ані впізнати між версіями.
        //
        // Ключовість наявного поля не змінюється взагалі (див.
        // `RegistryFieldDef.Update`), тому перевірка чіпляється за довідник,
        // у якому ключового поля не було з самого початку.
        var bare = Definition("BARE", 6);
        AddField(bare, 61, "Name", CellDataType.String, ordinal: 1, isKey: false);
        _registries.FindDefinitionAsync("BARE", Arg.Any<CancellationToken>()).Returns(bare);
        _registries.ListRulesAsync(6, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryRuleDef>>([]);

        var request = new SaveRegistryDefinitionDto(
            [
                new RegistryFieldSaveDto(
                    61, "Name", Text("Назва"), "String", 1, false, false, null, null),
            ],
            [],
            "перевірка ключа");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("BARE", request, default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Причина_зміни_обовʼязкова()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("PERMIT", Request(reason: "   "), default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Збереження_піднімає_версію_опису_і_пише_нове_правило_в_журнал()
    {
        // ⛔ Знімок «після» береться з ПАМ'ЯТІ: щойно додане правило ще не
        // збережене, і повторне читання бази віддало б стан «до» під виглядом
        // стану «після» — журнал, який мовчки бреше саме там, де змін
        // найбільше.
        var rules = _rules
            .Select(r => new RegistryRuleSaveDto(
                r.Id, r.Code, r.RuleKind.ToString(), r.Expression, r.Severity.ToString(),
                r.MessageL10n, r.ParametersJson, r.IsActive))
            .ToList();

        rules.Add(new RegistryRuleSaveDto(
            null, "LimitBelowCap", "Expression", "[Limit] < 1000", "Warning",
            Text("ліміт завеликий"), null, true));

        var version = await Saves().HandleAsync(
            "PERMIT", Request(rules: rules, reason: "додано стелю ліміту"), default);

        Assert.Equal(2, version);
        Assert.Equal(2, _permits.DefinitionVersion);

        var record = Assert.Single(_audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteStructureChangeAsync))
            .Select(c => (StructureChangeRecord)c.GetArguments()[0]!));

        Assert.Equal("SaveDefinition", record.Operation);
        Assert.Equal("додано стелю ліміту", record.ChangeReason);
        Assert.DoesNotContain("LimitBelowCap", record.OldJson, StringComparison.Ordinal);
        Assert.Contains("LimitBelowCap", record.NewJson, StringComparison.Ordinal);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Історія_показує_власні_зміни_і_перемикання_свого_набору()
    {
        // ⛔ Перемикання master пишеться ОДНИМ записом на весь набір із
        // `EntityId = 0` (`ФВ-13.10`). Історія довідника без нього неповна, а
        // з чужим набором — брехлива.
        _auditReader.ReadStructureChangesAsync(
                Arg.Is<IReadOnlyList<string>>(t => t.Count == 2), Arg.Is(PermitsId),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<StructureChangeView>>(
            [
                new StructureChangeView(
                    Now, "cfg.RegistryDef", PermitsId, "SaveDefinition", "{}", "{}", "правка", 9),
            ]);

        _auditReader.ReadStructureChangesAsync(
                Arg.Is<IReadOnlyList<string>>(t => t.Count == 1), Arg.Is(0),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<StructureChangeView>>(
            [
                new StructureChangeView(
                    Now.AddDays(-1), "cfg.RegistryDef", 0, "SwitchSourceSet", null,
                    """{"sourceKind":"Local","registryCodes":["PERMIT","WATER_BODY"]}""",
                    "перехід", 9),

                // ⚠ Чужий набір: код `PERMIT_TYPE` містить підрядок `PERMIT`, і
                // саме тому фільтр іде по РОЗІБРАНОМУ JSON, а не по підрядку.
                new StructureChangeView(
                    Now.AddDays(-2), "cfg.RegistryDef", 0, "SwitchSourceSet", null,
                    """{"sourceKind":"External","registryCodes":["PERMIT_TYPE"]}""",
                    "чуже", 9),
            ]);

        var history = await History().HandleAsync("PERMIT", default);

        Assert.Equal(2, history.Count);
        Assert.Equal("SaveDefinition", history[0].Operation);
        Assert.Equal("SwitchSourceSet", history[1].Operation);
        Assert.DoesNotContain(history, h => h.ChangeReason == "чуже");
    }

    private GetRegistryDefinitionHandler Definitions() => new(_registries, _access, _user);

    private GetRegistryHistoryHandler History() => new(_registries, _auditReader, _access, _user);

    private SaveRegistryDefinitionHandler Saves()
        => new(_registries, _uow, _audit, _access, _user, _clock);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    /// <summary>Поля довідника у формі, яку надсилає конструктор.</summary>
    private List<RegistryFieldSaveDto> Fields()
        => [.. _permits.Fields
            .OrderBy(f => f.Ordinal)
            .Select(f => new RegistryFieldSaveDto(
                f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal,
                f.IsRequired, f.IsKey, f.RefRegistryDefId, f.UnitId))];

    private SaveRegistryDefinitionDto Request(
        List<RegistryFieldSaveDto>? fields = null,
        List<RegistryRuleSaveDto>? rules = null,
        string reason = "правка опису")
        => new(
            fields ?? Fields(),
            rules ?? [.. _rules.Select(r => new RegistryRuleSaveDto(
                r.Id, r.Code, r.RuleKind.ToString(), r.Expression, r.Severity.ToString(),
                r.MessageL10n, r.ParametersJson, r.IsActive))],
            reason);

    private static RegistryDef Definition(string code, int id)
    {
        var definition = new RegistryDef(
            EcrCode.Create(code), Text(code), isTemporal: true);

        SetId(definition, id);
        return definition;
    }

    private static void AddField(
        RegistryDef definition,
        int id,
        string code,
        CellDataType dataType,
        int ordinal,
        bool isKey = false,
        int? refRegistry = null)
    {
        var field = new RegistryFieldDef(
            definition.Id, EcrCode.Create(code), Text(code), dataType, ordinal);

        SetId(field, id);
        field.MarkKey(isKey);
        field.PointTo(refRegistry);
        definition.AddField(field);
    }

    private static RegistryRuleDef Rule(int id, string code, RegistryRuleKind kind, string expression)
    {
        var rule = new RegistryRuleDef(
            PermitsId, EcrCode.Create(code), kind, expression, ValidationSeverity.Error, Text(code));

        SetId(rule, id);
        return rule;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Ідентифікатор дає база; у тестах його призначаємо руками.</summary>
    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
