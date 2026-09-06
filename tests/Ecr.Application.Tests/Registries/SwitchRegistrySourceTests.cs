using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
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
/// Перемикання master-джерела <b>набором</b> довідників (ФВ-8.9, ФВ-13.10).
/// </summary>
/// <remarks>
/// ⛔ Сутності «група довідників» немає навмисно: група — це факт ОДНОГО
/// перемикання, а не властивість довідника. Усе, заради чого група була б
/// потрібна, лишається в аудиті: один запис із повним набором і причиною.
/// Тому тести перевіряють не «перемкнулося», а **атомарність набору** і
/// **повноту запису**.
/// </remarks>
public sealed class SwitchRegistrySourceTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    private readonly RegistryDef _permits = Definition("PERMIT", 4);
    private readonly RegistryDef _waterBodies = Definition("WATER_BODY", 5);
    private readonly RegistryDef _outfalls = Definition("OUTFALL", 6);

    public SwitchRegistrySourceTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("test");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Build());

        foreach (var definition in new[] { _permits, _waterBodies, _outfalls })
        {
            _registries.FindDefinitionAsync(definition.Code, Arg.Any<CancellationToken>())
                .Returns(definition);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.10")]
    public async Task Невідомий_код_у_наборі_не_перемикає_жодного()
    {
        // ⛔ Головний тест кроку. Перемкнути «те, що знайшлося», було б гірше
        // за відмову: половина блоку опинилася б в одному режимі, половина в
        // іншому, і ніхто б не знав, де проходить межа.
        _registries.FindDefinitionAsync("NO_SUCH", Arg.Any<CancellationToken>())
            .Returns((RegistryDef?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(
            ["PERMIT", "NO_SUCH", "WATER_BODY"], RegistrySourceKind.External, "перехід", default));

        Assert.Equal(RegistrySourceKind.Local, _permits.SourceKind);
        Assert.Equal(RegistrySourceKind.Local, _waterBodies.SourceKind);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.9")]
    public async Task Відкритий_період_відхиляє_весь_набір()
    {
        // ⚠ Питання ставиться ОДИН раз і глобально: довідник один на всі
        // проєкти, і перемикання «у закритому проєкті» змінило б перелік
        // записів у сусідньому, відкритому.
        _registries.HasOpenPeriodAsync(Arg.Any<CancellationToken>()).Returns(true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            ["PERMIT", "WATER_BODY", "OUTFALL"], RegistrySourceKind.External, "перехід", default));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.All(All(), d => Assert.Equal(RegistrySourceKind.Local, d.SourceKind));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // ⚠ І рівно ОДИН виклик перевірки на весь набір, а не по одному на
        // довідник: у наборі з тридцяти це тридцять однакових запитів, кожен
        // із яких сканує всі проєкти.
        await _registries.Received(1).HasOpenPeriodAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.10")]
    public async Task Набір_записується_в_аудит_одним_записом_із_причиною()
    {
        // ⛔ Три записи поруч у журналі не відрізняються від трьох випадкових
        // перемикань, зроблених того ж дня. Зв'язок між ними і є тим єдиним,
        // заради чого була б потрібна сутність «група».
        var changed = await Handler().HandleAsync(
            ["PERMIT", "WATER_BODY", "OUTFALL"],
            RegistrySourceKind.External,
            "перехід водного блоку на master в ECR",
            default);

        Assert.Equal(3, changed);

        var records = _audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteStructureChangeAsync))
            .Select(c => (StructureChangeRecord)c.GetArguments()[0]!)
            .ToList();

        var record = Assert.Single(records);

        Assert.Equal("SwitchSourceSet", record.Operation);
        Assert.Equal("перехід водного блоку на master в ECR", record.ChangeReason);

        foreach (var code in new[] { "PERMIT", "WATER_BODY", "OUTFALL" })
        {
            Assert.Contains(code, record.NewJson, StringComparison.Ordinal);
            Assert.Contains(code, record.OldJson, StringComparison.Ordinal);
        }

        // ⛔ Одне збереження на весь набір: або перемкнулися всі, або жоден.
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.9")]
    public async Task Ревізія_даних_не_рухається()
    {
        // ⚠ Змінився ВЛАСНИК довідника, а не його вміст. Рухати ревізію
        // означало б інвалідувати кеш там, де нічого не змінилося, і привчити
        // клієнта ігнорувати ревізію взагалі.
        var before = All().Select(d => d.DataRevision).ToList();

        await Handler().HandleAsync(
            ["PERMIT", "WATER_BODY", "OUTFALL"], RegistrySourceKind.External, "перехід", default);

        Assert.Equal(before, All().Select(d => d.DataRevision));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.10")]
    public async Task Довідник_уже_в_цільовому_режимі_не_потрапляє_в_подію()
    {
        // У наборі такі трапляються постійно: перемикають блок, частина вже
        // там. Порахувати їх як змінені означало б написати в журнал неправду.
        _permits.SwitchSource(RegistrySourceKind.External);

        var changed = await Handler().HandleAsync(
            ["PERMIT", "WATER_BODY"], RegistrySourceKind.External, "перехід", default);

        Assert.Equal(1, changed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.10")]
    public async Task Порожній_набір_дублі_і_порожня_причина_відхиляються()
    {
        var empty = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            [], RegistrySourceKind.External, "перехід", default));
        Assert.Equal("ECR-REG-0422", empty.ErrorCode);

        // ⚠ Дубль означає, що набір зліпили з двох переліків, і другий міг
        // містити зайве. Мовчазне злиття сховало б саме це.
        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            ["PERMIT", "PERMIT"], RegistrySourceKind.External, "перехід", default));
        Assert.Equal("ECR-REG-0422", duplicate.ErrorCode);

        var noReason = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            ["PERMIT"], RegistrySourceKind.External, "   ", default));
        Assert.Equal("ECR-REG-0422", noReason.ErrorCode);

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Без_права_Integration_Manage_набір_не_перемикається()
    {
        // ⚠ Право небезпечне (`ФВ-6.12`) і в seed його не має ніхто: воно
        // видається поіменно. Перемикання master — крок поетапного переходу
        // (`ФВ-11.4`), а не правка визначення довідника.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.EditDefinition").Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            ["PERMIT"], RegistrySourceKind.External, "перехід", default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal(RegistrySourceKind.Local, _permits.SourceKind);
    }

    private SwitchRegistrySourceHandler Handler()
        => new(_registries, _uow, _audit, _access, _user, _clock);

    private RegistryDef[] All() => [_permits, _waterBodies, _outfalls];

    /// <summary>Опис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryDef Definition(string code, int id)
    {
        var definition = new RegistryDef(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            isTemporal: true);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, id);

        return definition;
    }
}
