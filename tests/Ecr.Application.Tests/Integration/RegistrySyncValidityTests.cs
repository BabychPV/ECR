// tests/Ecr.Application.Tests/Integration/RegistrySyncValidityTests.cs
using Ecr.Application.Integration.RegistrySync;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Вікно дії запису з атрибутів AF (<c>D-212</c> (8), PR-7): <see cref="RegistrySyncValidity"/> і
/// планувальник — формат дати, включна/виключна межа, пояс AF на межі доби, політика за
/// <c>SourceKind</c>, автостворення.
/// </summary>
/// <remarks>
/// Пояс «+2» — власний (<see cref="TimeZoneInfo.CreateCustomTimeZone(string, TimeSpan, string, string)"/>),
/// а не «FLE Standard Time»/«Europe/Kyiv»: ідентифікатори поясів різняться між Windows і Linux
/// агентом CI, а перевіряється тут арифметика межі, а не база поясів ОС.
/// </remarks>
public sealed class RegistrySyncValidityTests
{
    private const long EntryId = 1001;
    private const string Guid1 = "F1A2B3C4-0000-0000-0000-000000000001";
    private const string From = "Actual_Start_Date";
    private const string To = "Actual_End_Date";

    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("ECR+2", TimeSpan.FromHours(2), "ECR+2", "ECR+2");

    private static readonly DateOnly D2024 = new(2024, 1, 1);
    private static readonly DateOnly D2025 = new(2025, 1, 1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Без_політики_дат_вікно_не_синхронізується_навіть_за_наявних_атрибутів()
    {
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, validity: null, (From, "2024-01-01"), (To, "2025-01-01")));

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void External_дати_ISO_дають_зміну_вікна_а_той_самий_знімок_повторно_нічого()
    {
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(), (From, "2024-01-01T00:00:00.0000000Z"), (To, "2025-01-01T00:00:00Z")));

        var change = Assert.Single(plan.ValidityChanges);
        Assert.Equal(EntryId, change.RegistryEntryId);
        Assert.Equal(Guid1, change.ExternalId);
        Assert.Equal(ValidityWindow.Always, change.Old);
        Assert.Equal(new ValidityWindow(D2024, D2025), change.New);
        Assert.Empty(plan.Events);

        // Ідемпотентність: вікно вже таке — план порожній.
        var again = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(), current: new ValidityWindow(D2024, D2025),
            attributes: [(From, "2024-01-01T00:00:00.0000000Z"), (To, "2025-01-01T00:00:00Z")]));
        Assert.True(again.IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Атрибута_немає_у_знімку_межа_лишається_порожній_атрибут_знімає_межу()
    {
        // Кінця немає у знімку — джерело нічого не сказало: лишається поточний 2025-01-01.
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(), current: new ValidityWindow(null, D2025),
            attributes: [(From, "2024-01-01")]));
        Assert.Equal(new ValidityWindow(D2024, D2025), Assert.Single(plan.ValidityChanges).New);

        // Кінець порожній — «без обмеження».
        var cleared = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(), current: new ValidityWindow(D2024, D2025),
            attributes: [(From, "2024-01-01"), (To, "  ")]));
        Assert.Equal(new ValidityWindow(D2024, null), Assert.Single(cleared.ValidityChanges).New);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("01.01.2024")]
    [InlineData("1/1/2024")]
    [InlineData("2024-13-01")]
    [InlineData("не дата")]
    public void Невалідна_дата_дає_ValueRejected_і_межу_не_чіпає(string raw)
    {
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(), current: new ValidityWindow(D2024, null),
            attributes: [(From, raw), (To, "2025-01-01")]));

        // Кінець — валідний і змінюється; початок лишається поточним.
        Assert.Equal(new ValidityWindow(D2024, D2025), Assert.Single(plan.ValidityChanges).New);
        var rejected = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ValueRejected, rejected.Kind);
        Assert.Equal(RegistrySyncValidity.FromFieldCode, rejected.FieldCode);
        Assert.Equal(raw, rejected.SourceValue);
        Assert.Equal("ECR-REG-0422", rejected.ErrorCode);
        Assert.Equal(RegistrySyncValidity.DateInvalidKey, rejected.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Число_замість_дати_відхиляється()
    {
        var plan = RegistrySyncPlanner.Plan(Input(RegistrySourceKind.External, Source(), (To, 45292m)));

        Assert.Empty(plan.ValidityChanges);
        Assert.Equal(RegistrySyncValidity.DateInvalidKey, Assert.Single(plan.Events).MessageKey);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("2024-12-31", true, "2025-01-01")]
    [InlineData("2024-12-31", false, "2024-12-31")]
    [InlineData("2024-12-31T23:59:59", true, "2025-01-01")]
    [InlineData("2024-12-31T10:00:00", false, "2025-01-01")]
    [InlineData("2025-01-01T00:00:00", false, "2025-01-01")]
    public void Включна_межа_додає_день_виключна_округлює_вгору_до_півночі(string raw, bool inclusive, string expected)
    {
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(inclusive: inclusive), (To, raw)));

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Assert.Single(plan.ValidityChanges).New.ToExclusive);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("2024-12-31T22:00:00Z", true, "2025-01-01")]
    [InlineData("2024-12-31T22:00:00Z", false, "2024-12-31")]
    [InlineData("2025-01-01T00:00:00+02:00", false, "2024-12-31")]
    [InlineData("2025-01-01T00:00:00+02:00", true, "2025-01-01")]
    [InlineData("2024-12-31T22:00:00", true, "2024-12-31")]
    public void Пояс_AF_на_межі_доби(string raw, bool plus2, string expected)
    {
        // Північ за «+2» записана в UTC — це 1 січня, а не 31 грудня. Без поясу — уже місцевий час AF.
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(zone: plus2 ? Plus2 : TimeZoneInfo.Utc), (From, raw)));

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Assert.Single(plan.ValidityChanges).New.FromInclusive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Порожнє_вікно_з_джерела_дає_ValueRejected_і_вікно_не_змінюється()
    {
        var plan = RegistrySyncPlanner.Plan(Input(
            RegistrySourceKind.External, Source(), (From, "2025-01-01"), (To, "2024-06-30")));

        Assert.Empty(plan.ValidityChanges);
        var rejected = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ValueRejected, rejected.Kind);
        Assert.Equal(RegistrySyncValidity.WindowFieldCode, rejected.FieldCode);
        Assert.Equal(RegistrySyncValidity.WindowEmptyKey, rejected.MessageKey);
        Assert.Equal("[2025-01-01, 2024-06-30)", rejected.SourceValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Local_лише_звіряє_вікно_подією_Diverged()
    {
        var plan = RegistrySyncPlanner.Plan(Input(RegistrySourceKind.Local, Source(), (From, "2024-01-01")));

        Assert.Empty(plan.ValidityChanges);
        var diverged = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.Diverged, diverged.Kind);
        Assert.Equal(RegistrySyncValidity.WindowFieldCode, diverged.FieldCode);
        Assert.Equal("[-∞, ∞)", diverged.CurrentValue);
        Assert.Equal("[2024-01-01, ∞)", diverged.SourceValue);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData(RegistrySourceKind.Hybrid, true, false)]
    [InlineData(RegistrySourceKind.Hybrid, false, true)]
    [InlineData(RegistrySourceKind.External, true, true)]
    public void Людина_виграє_вікно_лише_в_Hybrid(RegistrySourceKind kind, bool byHuman, bool written)
    {
        var plan = RegistrySyncPlanner.Plan(Input(
            kind, Source(), current: ValidityWindow.Always, byHuman: byHuman, attributes: [(From, "2024-01-01")]));

        Assert.Equal(written, plan.ValidityChanges.Count == 1);
        if (!written)
        {
            Assert.Equal(RegistrySyncEventKind.ConflictKeptManual, Assert.Single(plan.Events).Kind);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Автостворення_бере_вікно_з_атрибутів()
    {
        var element = new RegistrySyncSourceElement(
            Guid1, @"\\AF\ECR\Flares\FL-01",
            new Dictionary<string, object?> { [From] = "2024-01-01", [To] = "2024-12-31" }, "FL-01");
        var input = new RegistrySyncInput(40, RegistrySourceKind.External, true, [element], [], [], [], Validity: Source(inclusive: true));

        var create = Assert.Single(RegistrySyncPlanner.Plan(input).Creates);

        Assert.Equal(new ValidityWindow(D2024, D2025), create.Validity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Пояс_з_конфігурації_порожній_UTC_невідомий_помилка_з_ключем()
    {
        Assert.Equal(TimeZoneInfo.Utc, RegistrySyncValidity.ResolveTimeZone(null));
        Assert.Equal(TimeZoneInfo.Utc, RegistrySyncValidity.ResolveTimeZone("  "));
        Assert.Equal(TimeZoneInfo.Utc.BaseUtcOffset, RegistrySyncValidity.ResolveTimeZone("UTC").BaseUtcOffset);

        var ex = Assert.Throws<InvalidOperationException>(() => RegistrySyncValidity.ResolveTimeZone("Mars/Olympus_Mons"));
        Assert.Contains(RegistrySyncValidity.TimeZoneKey, ex.Message, StringComparison.Ordinal);
    }

    private static RegistrySyncValiditySource Source(bool inclusive = false, TimeZoneInfo? zone = null)
        => new(From, To, inclusive, zone ?? TimeZoneInfo.Utc);

    private static RegistrySyncInput Input(
        RegistrySourceKind kind, RegistrySyncValiditySource? validity, params (string Name, object? Value)[] attributes)
        => Input(kind, validity, ValidityWindow.Always, false, attributes);

    private static RegistrySyncInput Input(
        RegistrySourceKind kind,
        RegistrySyncValiditySource? validity,
        ValidityWindow current,
        bool byHuman = false,
        (string Name, object? Value)[]? attributes = null)
    {
        var element = new RegistrySyncSourceElement(
            Guid1,
            @"\\AF\ECR\Flares\FL-01",
            (attributes ?? []).ToDictionary(a => a.Name, a => a.Value));

        return new RegistrySyncInput(
            40,
            kind,
            IsCompleteSnapshot: true,
            [element],
            [new RegistrySyncLink(Guid1, EntryId, @"\\AF\ECR\Flares\FL-01")],
            [new RegistrySyncEntryState(
                EntryId, new Dictionary<int, RegistrySyncCurrentValue>(), true, current.FromInclusive, current.ToExclusive, byHuman)],
            [],
            Validity: validity);
    }
}
