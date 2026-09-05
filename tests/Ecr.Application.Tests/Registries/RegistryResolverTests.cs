// tests/Ecr.Application.Tests/Registries/RegistryResolverTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Темпоральний резолвінг «станом на дату **періоду**», а не «на сьогодні» —
/// різниця стає видимою, коли звіт за минулий рік перераховують цього року.
/// </summary>
public sealed class RegistryResolverTests
{
    /// <summary>Довідник дозволів.</summary>
    private const int Permits = 4;

    /// <summary>Довідник водних об'єктів — той, що звужується каскадом.</summary>
    private const int WaterBodies = 5;

    /// <summary>Дата періоду, за який заповнюють документ: березень 2026.</summary>
    private static readonly DateOnly MarchPeriod = new(2026, 3, 31);

    /// <summary>«Сьогодні» — на пів року пізніше за період.</summary>
    private static readonly DateTime Now = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly RegistryResolver _resolver = new();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public RegistryResolverTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _user.CorrelationId.Returns("test");

        // Права перевіряються в обробнику, а не атрибутом контролера
        // (архітектурне правило 7). Тут вони видані: предмет цих тестів —
        // правила довідників, а не доступ.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
    }

    /// <summary>Профіль із правами на довідники.</summary>
    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Registry.View", "Registry.EditData", "Registry.EditDefinition",
        },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
    };

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_чинний_на_дату_періоду_потрапляє_у_список()
    {
        var permit = Entry(101, Permits, "PERMIT_A", from: new DateOnly(2025, 1, 1), to: new DateOnly(2027, 1, 1));

        var resolved = _resolver.Resolve([permit], [], MarchPeriod, parentEntryId: null);

        Assert.Equal([101L], resolved);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_що_втратив_чинність_до_періоду_не_потрапляє()
    {
        var expired = Entry(102, Permits, "PERMIT_OLD", from: new DateOnly(2020, 1, 1), to: new DateOnly(2026, 3, 30));
        var current = Entry(101, Permits, "PERMIT_A", from: new DateOnly(2025, 1, 1), to: null);

        var resolved = _resolver.Resolve([expired, current], [], MarchPeriod, parentEntryId: null);

        // ⚠ Межа включна: дозвіл, чинний «до 30 березня», у звіті за 31 березня
        // вже нечинний, але у звіті за 30-те — ще чинний. Помилка на день тут
        // не видима нікому, крім того, хто звіряє з паперовим дозволом.
        Assert.Equal([101L], resolved);
        Assert.Contains(102L, _resolver.Resolve([expired], [], new DateOnly(2026, 3, 30), null));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_що_набуде_чинності_пізніше_не_потрапляє()
    {
        var future = Entry(103, Permits, "PERMIT_NEW", from: new DateOnly(2026, 4, 1), to: null);

        Assert.Empty(_resolver.Resolve([future], [], MarchPeriod, parentEntryId: null));

        // І з першого ж дня чинності — з'являється. Виключний початок зробив би
        // дозвіл недоступним у день, яким його видали.
        Assert.Equal([103L], _resolver.Resolve([future], [], new DateOnly(2026, 4, 1), null));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Резолвінг_робиться_на_дату_періоду_а_не_на_поточну_дату()
    {
        // Дозвіл, чинний у березні і закритий у червні. Сьогодні — жовтень.
        var permit = Entry(101, Permits, "PERMIT_A", from: new DateOnly(2025, 1, 1), to: new DateOnly(2026, 6, 30));

        var forPeriod = _resolver.Resolve([permit], [], MarchPeriod, parentEntryId: null);
        var forToday = _resolver.Resolve([permit], [], DateOnly.FromDateTime(Now), parentEntryId: null);

        // ⛔ Ось уся суть темпоральності: перерахунок березневого звіту в жовтні
        // мусить бачити березневий перелік. Резолвінг «на сьогодні» тихо
        // прибрав би дозвіл, і документ, який рік був валідним, став би
        // осиротілим — не тому, що щось змінилося, а тому, що його відкрили.
        Assert.Equal([101L], forPeriod);
        Assert.Empty(forToday);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.4")]
    public void Каскад_звужує_список_водних_обєктів_за_обраним_дозволом()
    {
        var river = Entry(201, WaterBodies, "RIVER_1", ordinal: 1);
        var lake = Entry(202, WaterBodies, "LAKE_1", ordinal: 2);
        var sea = Entry(203, WaterBodies, "SEA_1", ordinal: 3);

        // Дозвіл 101 покриває річку і озеро; море належить іншому дозволу.
        RegistryEntryLink[] links =
        [
            new(101, 201, "permit-water-body"),
            new(101, 202, "permit-water-body"),
            new(999, 203, "permit-water-body"),
        ];

        var all = _resolver.Resolve([river, lake, sea], links, MarchPeriod, parentEntryId: null);
        var narrowed = _resolver.Resolve([river, lake, sea], links, MarchPeriod, parentEntryId: 101);

        // Порядок — за Ordinal, а не за кодом: алфавіт кодів для людини нічого
        // не означає, і список «стрибав» би щоразу, коли запис перейменують.
        Assert.Equal([201L, 202L, 203L], all);
        Assert.Equal([201L, 202L], narrowed);

        // ⚠ Дозвіл без жодного водного об'єкта дає ПОРОЖНІЙ список, а не
        // повний. Показати всі — означає мовчки скасувати каскад саме там, де
        // він потрібен: користувач обрав би об'єкт, не покритий дозволом.
        Assert.Empty(_resolver.Resolve([river, lake, sea], links, MarchPeriod, parentEntryId: 555));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.6")]
    public async Task Запис_на_який_посилаються_дані_не_видаляється_ECR_REG_0409()
    {
        var entry = Entry(101, Permits, "PERMIT_A");
        _registries.FindEntryAsync(101, Arg.Any<CancellationToken>()).Returns(entry);
        _registries.CountReferencesAsync(101, Arg.Any<CancellationToken>()).Returns(17);

        var handler = new DeleteRegistryEntryHandler(_registries, _uow, _access, _user, _clock);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(101, CancellationToken.None));

        Assert.Equal("ECR-REG-0409", error.ErrorCode);

        // ⚠ Запис лишається недоторканим: не «видалений логічно», а саме не
        // змінений. У комірках лежить його Id (ФВ-8.8), і вимкнений запис
        // зробив би поданий два роки тому звіт нечитабельним.
        Assert.False(entry.IsDeleted);
        Assert.True(entry.IsActive);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Без посилань — видалення логічне і проходить.
        _registries.CountReferencesAsync(101, Arg.Any<CancellationToken>()).Returns(0);
        await handler.HandleAsync(101, CancellationToken.None);
        Assert.True(entry.IsDeleted);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зміна_записів_інкрементує_ревізію_даних_реєстру()
    {
        var definition = Definition();
        var entry = Entry(101, Permits, "PERMIT_A");

        _registries.FindDefinitionByIdAsync(Permits, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindEntryAsync(101, Arg.Any<CancellationToken>()).Returns(entry);

        var before = definition.DataRevision;

        await new UpsertRegistryEntryHandler(_registries, _uow, _access, _user, _clock).HandleAsync(
            new RegistryEntryUpsertDto(
                Id: 101, RegistryDefId: Permits, Code: "PERMIT_A",
                Display: Text("Дозвіл A (перейменований)"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?>()),
            CancellationToken.None);

        // ⚠ Ревізія рухається навіть від зміни самої лише назви: саме вона
        // лежить у ключі кешу списків. Без інкременту grid показував би старий
        // підпис до перезапуску процесу — і ніхто б не зрозумів, чому.
        Assert.Equal(before + 1, definition.DataRevision);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.9")]
    public async Task Перемикання_master_у_відкритому_періоді_відхиляється_ECR_REG_0422()
    {
        var definition = Definition();
        _registries.FindDefinitionAsync("PERMITS", Arg.Any<CancellationToken>()).Returns(definition);
        _registries.HasOpenPeriodAsync(Arg.Any<CancellationToken>()).Returns(true);

        var handler = new SwitchRegistrySourceHandler(_registries, _uow, _audit, _access, _user, _clock);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync("PERMITS", RegistrySourceKind.External, CancellationToken.None));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);

        // ⛔ Master не змінився. Інакше частина документів періоду заповнилася б
        // за одним переліком записів, а частина — за іншим, і в даних не
        // лишилося б жодної позначки, де проходить межа (ФВ-8.9).
        Assert.Equal(RegistrySourceKind.Local, definition.SourceKind);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Коли відкритих періодів немає — перемикання проходить.
        _registries.HasOpenPeriodAsync(Arg.Any<CancellationToken>()).Returns(false);
        await handler.HandleAsync("PERMITS", RegistrySourceKind.External, CancellationToken.None);
        Assert.Equal(RegistrySourceKind.External, definition.SourceKind);
    }

    /// <summary>Опис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryDef Definition()
    {
        var definition = new RegistryDef(EcrCode.Create("PERMITS"), Text("Permits"), isTemporal: true);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, Permits);
        return definition;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Запис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryEntry Entry(
        long id, int registryDefId, string code,
        DateOnly? from = null, DateOnly? to = null, int ordinal = 0)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        entry.SetOrdinal(ordinal);

        if (from is not null || to is not null)
        {
            entry.SetValidity(from, to);
        }

        return entry;
    }
}
