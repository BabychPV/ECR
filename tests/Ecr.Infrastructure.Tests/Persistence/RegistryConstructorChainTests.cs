// tests/Ecr.Infrastructure.Tests/Persistence/RegistryConstructorChainTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Ланка конструктора довідника від БАЗИ до відповіді (<c>ФВ-8.12</c>).
/// </summary>
/// <remarks>
/// ⛔ Тест існує рівно тому, що обробник із замокненим сховищем доводить лише
/// одне: обробник уміє скласти відповідь із того, що йому дали. Він нічого не
/// каже про те, чи ДОХОДЯТЬ до нього правила, мапінг і види зв'язків із
/// реальних таблиць — а це три різні запити до трьох різних схем
/// (<c>cfg</c>, <c>ext</c>, <c>dic</c>), кожен із яких можна написати так, що
/// він мовчки поверне порожньо.
///
/// ⚠ Саме така помилка вже траплялася: <c>ListInboundLinksAsync</c> дивиться
/// лише праворуч, і конструктор довідника дозволів показав би нуль зв'язків
/// саме там, де їх найбільше.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryConstructorChainTests(SqlServerFixture sql)
{
    /// <summary>
    /// Суфікс кодів — свій на КОЖЕН тест.
    /// </summary>
    /// <remarks>
    /// ⚠ База спільна на всю збірку, а коди довідників унікальні
    /// (<c>UQ_RegistryDef</c>). Спільний суфікс на клас робив би другий тест
    /// червоним через дубль, залишений першим, — тобто падінням не зі своєї
    /// причини.
    /// </remarks>
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правила_мапінг_і_звʼязки_доходять_до_обробника_з_бази()
    {
        var (permitCode, permitId) = await SeedAsync();

        await using var db = Context();
        var store = new RegistryStore(db);
        var handler = new GetRegistryDefinitionHandler(store, Access(), User());

        var definition = await handler.HandleAsync(permitCode, CancellationToken.None);

        Assert.Equal(permitId, definition.Id);

        // ⛔ Усі ЧОТИРИ види правил доїхали з `cfg.RegistryRuleDef`. Це і є
        // перевірка `H-10` на реальній таблиці: обмеження `CK_RegRule_Kind`
        // пропустило рівно чотири, і жоден із них не загубився в дорозі.
        Assert.Equal(
            ["CrossRegistry", "Expression", "RequiredWhen", "UniqueWithin"],
            definition.Rules.Select(r => r.RuleKind).Order(StringComparer.Ordinal).ToList());

        // ⛔ Мапінг зібраний із трьох таблиць `ext` і назвав ОДИНИЦІ обох
        // боків: розбіжність між ними — «найчастіше джерело мовчазних
        // розбіжностей у числах» (`ФВ-16.9`).
        var mapping = Assert.Single(definition.Mappings);
        Assert.Equal("Limit", mapping.FieldCode);
        Assert.Equal($"PI_{_tag}", mapping.SourceCode);
        Assert.Equal($"m3_{_tag}", mapping.SourceUnitCode);
        Assert.Equal($"km3_{_tag}", mapping.TargetUnitCode);

        // ⛔ Зв'язок M:N зарахований довіднику, записи якого стоять ЛІВОРУЧ.
        // Дивитися лише праворуч (як `ListInboundLinksAsync`) означало б
        // показати нуль зв'язків саме в тому конструкторі, де їх найбільше.
        var association = Assert.Single(definition.Relations, r => r.Kind == "Association");
        Assert.Equal($"permit-substance-{_tag}", association.LinkKind);
        Assert.Equal(1, association.LinkCount);

        // Каскад — поле, що вказує на ЧУЖИЙ довідник.
        Assert.Contains(definition.Relations, r => r.Kind == "Cascade" && r.FieldCode == "Substance");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Історія_довідника_читається_з_журналу_структурних_змін()
    {
        var (permitCode, permitId) = await SeedAsync();

        await using var db = Context();
        var writer = new AuditWriter(db);

        await writer.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                TemplateVersionId: 0,
                EntityType: "cfg.RegistryDef",
                EntityId: permitId,
                ChangeClass: ChangeClass.Guarded,
                Operation: "SaveDefinition",
                OldJson: """{"fields":[]}""",
                NewJson: """{"fields":[{"Code":"Limit"}]}""",
                ChangeReason: $"додано ліміт {_tag}",
                ChangedByUserId: 9,
                CorrelationId: "test"),
            CancellationToken.None);

        var handler = new GetRegistryHistoryHandler(
            new RegistryStore(db), new AuditReader(db), Access(), User());

        var history = await handler.HandleAsync(permitCode, CancellationToken.None);

        // ⚠ Перевіряється ПРИЧИНА, а не факт запису: питання, на яке цей
        // екран відповідає, — «чому тут з'явилося це поле», і відповідь на
        // нього пише автор, а не система.
        var entry = Assert.Single(history, h => h.ChangeReason == $"додано ліміт {_tag}");
        Assert.Equal("SaveDefinition", entry.Operation);
        Assert.Equal(9, entry.ChangedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нове_правило_доїжджає_до_бази_і_повертається_наступним_читанням()
    {
        // ⛔ Замкнена ланка: збереження → база → читання. Обробник із замокненим
        // сховищем доводить, що він КЛИЧЕ `AddRule`; він нічого не каже про те,
        // чи є під цим таблиця, чи проходить `CK_RegRule_Kind` і чи побачить
        // правило наступний, хто відкриє конструктор.
        var (permitCode, _) = await SeedAsync();

        await using var db = Context();
        var store = new RegistryStore(db);

        var existing = await new GetRegistryDefinitionHandler(store, Access(), User())
            .HandleAsync(permitCode, CancellationToken.None);

        var save = new SaveRegistryDefinitionHandler(
            store, new UnitOfWork(db), new AuditWriter(db), Editor(), User(), Clock());

        var rules = existing.Rules
            .Select(r => new RegistryRuleSaveDto(
                r.Id, r.Code, r.RuleKind, r.Expression, r.Severity, r.MessageL10n,
                r.ParametersJson, r.IsActive))
            .ToList();

        rules.Add(new RegistryRuleSaveDto(
            null, "LimitBelowCap", "Expression", "[Limit] < 1000", "Warning",
            Text("ліміт завеликий"), null, true));

        var version = await save.HandleAsync(
            permitCode,
            new SaveRegistryDefinitionDto(
                // ⚠ Порядок береться з ПОЗИЦІЇ у відповіді: `Ordinal` у DTO
                // читання немає навмисно — поля приходять уже впорядкованими,
                // і друге поле з тим самим змістом розійшлося б із першим.
                // Клієнт складає запит так само (`buildSaveRequest`).
                existing.Fields
                    .Select((f, i) => new RegistryFieldSaveDto(
                        f.Id, f.Code, f.NameL10n, f.DataType, i + 1,
                        f.IsRequired, f.IsScopeField, f.LookupRegistryDefId, f.UnitId))
                    .ToList(),
                rules,
                $"стеля ліміту {_tag}"),
            CancellationToken.None);

        Assert.Equal(2, version);

        // ⚠ Читання йде НОВИМ контекстом: той самий трекер віддав би правило з
        // пам'яті навіть тоді, коли `SaveChanges` до таблиці не дійшов.
        await using var fresh = Context();
        var reread = await new GetRegistryDefinitionHandler(
                new RegistryStore(fresh), Access(), User())
            .HandleAsync(permitCode, CancellationToken.None);

        var added = Assert.Single(reread.Rules, r => r.Code == "LimitBelowCap");
        Assert.Equal("Expression", added.RuleKind);
        Assert.Equal("Warning", added.Severity);
        Assert.Equal(2, reread.DefinitionVersion);

        // Причина зміни лягла в журнал і читається історією того ж довідника.
        var history = await new GetRegistryHistoryHandler(
                new RegistryStore(fresh), new AuditReader(fresh), Access(), User())
            .HandleAsync(permitCode, CancellationToken.None);

        Assert.Contains(history, h => h.ChangeReason == $"стеля ліміту {_tag}");
    }

    /// <summary>Заводить довідник із полями, правилами, мапінгом і зв'язком.</summary>
    /// <returns>Код довідника дозволів і його ідентифікатор.</returns>
    private async Task<(string Code, int Id)> SeedAsync()
    {
        await using var db = Context();

        var permits = new RegistryDef(
            EcrCode.Create($"PERMIT_{_tag}"), Text("Дозволи"), isTemporal: true);
        var substances = new RegistryDef(
            EcrCode.Create($"SUBSTANCE_{_tag}"), Text("Речовини"), isTemporal: false);

        db.RegistryDefs.AddRange(permits, substances);
        await db.SaveChangesAsync();

        var number = new RegistryFieldDef(
            permits.Id, EcrCode.Create("Number"), Text("Номер"), CellDataType.String, 1);
        number.MarkKey(true);

        var substanceRef = new RegistryFieldDef(
            permits.Id, EcrCode.Create("Substance"), Text("Речовина"), CellDataType.Lookup, 2);
        substanceRef.PointTo(substances.Id);

        var limit = new RegistryFieldDef(
            permits.Id, EcrCode.Create("Limit"), Text("Ліміт"), CellDataType.Decimal, 3);

        db.RegistryFieldDefs.AddRange(number, substanceRef, limit);

        // ⛔ Рівно чотири види — стільки їх і є (`H-10`).
        db.RegistryRuleDefs.AddRange(
            Rule(permits.Id, "IndicatorRequired", RegistryRuleKind.RequiredWhen, "[Type] = 'Emission'"),
            Rule(permits.Id, "NumberUnique", RegistryRuleKind.UniqueWithin, "[Number]"),
            Rule(permits.Id, "LimitPositive", RegistryRuleKind.Expression, "[Limit] > 0"),
            Rule(permits.Id, "SubstanceExists", RegistryRuleKind.CrossRegistry, "[Substance]"));

        // ⚠ Розмірність береться з SEED, а не заводиться своя: `uom.Dimension.Id`
        // не IDENTITY, і власний запис зіштовхнувся б із каталогом, який
        // фікстура наливає перед кожним прогоном.
        var volume = await db.Dimensions.OrderBy(d => d.Id).FirstAsync();

        var cubicMetre = new Unit(
            EcrCode.Create($"m3_{_tag}"), Text("м³"), Text("кубометр"), volume.Id,
            isBase: false, factorToBase: 1m, offsetToBase: 0m);
        var cubicKilometre = new Unit(
            EcrCode.Create($"km3_{_tag}"), Text("км³"), Text("кубокілометр"), volume.Id,
            isBase: false, factorToBase: 1e9m, offsetToBase: 0m);
        db.Units.AddRange(cubicMetre, cubicKilometre);

        var source = new DataSource(
            EcrCode.Create($"PI_SRC_{_tag}"), Text("PI"), ExternalTransport.PiWebApi,
            "https://pi.local", $"secret_{_tag}");
        db.DataSources.Add(source);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(source.Id, $"PI_{_tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);

        var permitEntry = new RegistryEntry(permits.Id, EcrCode.Create("P1"), Text("Дозвіл 1"));
        var substanceEntry = new RegistryEntry(substances.Id, EcrCode.Create("CO"), Text("CO"));
        db.RegistryEntries.AddRange(permitEntry, substanceEntry);
        await db.SaveChangesAsync();

        var map = EntityFieldMap.ToRegistryField(entity.Id, $"Permit_Limit_{_tag}", limit.Id);
        map.SetUnits(cubicMetre.Id, cubicKilometre.Id);
        map.SetTransform("Last");
        db.EntityFieldMaps.Add(map);

        // ⚠ Дозвіл стоїть ЛІВОРУЧ: саме він звужує перелік речовин.
        db.RegistryEntryLinks.Add(new RegistryEntryLink(
            permitEntry.Id, substanceEntry.Id, $"permit-substance-{_tag}"));

        await db.SaveChangesAsync();

        return (permits.Code, permits.Id);
    }

    private static RegistryRuleDef Rule(
        int registryDefId, string code, RegistryRuleKind kind, string expression)
        => new(
            registryDefId, EcrCode.Create(code), kind, expression,
            ValidationSeverity.Error, Text(code));

    private static IAccessDecisionService Access()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        return access;
    }

    /// <summary>Профіль із правом правити ОПИС довідника.</summary>
    private static IAccessDecisionService Editor()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }
                  .Permission("Registry.View")
                  .Permission("Registry.EditDefinition")
                  .Build());
        return access;
    }

    private static IClock Clock()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc));
        return clock;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.CorrelationId.Returns("test");
        return user;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
}

