// tests/Ecr.Application.Tests/Integration/RegistrySyncPlannerTests.cs
using Ecr.Application.Integration.RegistrySync;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Планувальник синхронізації довідника з PI AF (<see cref="RegistrySyncPlanner"/>,
/// <c>docs/build/FEATURE-REGISTRY-SYNC.md</c>, крок S4): політика за
/// <c>SourceKind</c>, ідемпотентність, ручна правка, зниклий і неприв'язаний
/// елемент, відмова типу.
/// </summary>
/// <remarks>
/// Мутаційні докази (2026-09-28, власний worktree; мутація — тимчасова правка
/// <c>RegistrySyncPlanner.cs</c>, повернута зворотною правкою; до мутації 12/12):
/// <list type="bullet">
/// <item>прибрано порівняння старе/нове (<c>if (Equals(currentValue, incoming)) return;</c>) —
/// 4 з 12 червоні, серед них <see cref="Незмінне_значення_дає_порожній_план"/>;</item>
/// <item>прибрано гілку <c>Local</c> (<c>sourceKind == Local ||</c>) — 1 з 12 червоний:
/// <see cref="Local_лише_звіряє_без_оновлень"/>;</item>
/// <item>прибрано гілку <c>Hybrid</c> (раннє повернення для поля без активного
/// мапінгу) — 1 з 12 червоний: <see cref="Hybrid_пише_лише_поля_з_активним_мапінгом"/>.</item>
/// </list>
/// <para>
/// D-212 PR-4 (2026-09-30, кожна мутація — правка планувальника, відкат і контрольний прогін):
/// </para>
/// <list type="bullet">
/// <item>людина виграє й у <c>External</c> (без <c>sourceKind == Hybrid &amp;&amp;</c>) —
/// червоний <see cref="External_перезаписує_значення_людини_без_ConflictKeptManual"/>;</item>
/// <item>коди шукаються в усіх довідниках разом, а не в <c>RefRegistryDefId</c> —
/// червоний <see cref="Lookup_з_невідомим_кодом_дає_ValueRejected_entryRefNotFound_і_поле_не_чіпається"/>;</item>
/// <item>код не обрізається від пробілів — червоні <see cref="Lookup_за_кодом_розв_язується_в_Id"/> і
/// <see cref="LookupCodes_перелічує_коди_для_розв_язання_без_порожніх_і_дублів"/>.</item>
/// <item>автостворення на неповному знімку — червоний випадок <c>(External, false)</c>
/// <see cref="Без_автостворення_новий_елемент_лише_подія_ElementUnlinked"/>;</item>
/// <item>автостворення й для <c>Hybrid</c> — червоний випадок <c>(Hybrid, true)</c> того ж тесту;</item>
/// <item>код = ім'я і в <c>CodeMode = Auto</c> — червоний <see cref="External_CodeMode_Auto_лишає_код_writer_у"/>;</item>
/// <item>вимкнений мапінг пише в новий запис — червоний
/// <see cref="External_новий_елемент_повного_знімка_створює_запис_з_кодом_за_іменем"/>.</item>
/// </list>
/// </remarks>
public sealed class RegistrySyncPlannerTests
{
    private const int RegistryDefId = 40;
    private const long EntryId = 1001;
    private const string Guid1 = "F1A2B3C4-0000-0000-0000-000000000001";
    private const string Path1 = @"\\AF\ECR\Flares\FL-01";

    private const int LimitField = 11;
    private const int NameField = 12;
    private const int NoteField = 13;

    private static readonly RegistrySyncFieldMapping Limit =
        new(LimitField, "LIMIT", CellDataType.Decimal, null, "Permit_Limit", IsActive: true);

    private static readonly RegistrySyncFieldMapping Name =
        new(NameField, "NAME", CellDataType.String, null, "Name", IsActive: true);

    private const int FuelField = 14;
    private const int FuelRegistry = 77;

    /// <summary>Перелік AF (вид палива) — за КОДОМ запису довідника палив (<c>D-212</c> (5)).</summary>
    private static readonly RegistrySyncFieldMapping Fuel =
        new(FuelField, "FUEL", CellDataType.Lookup, null, "Fuel", IsActive: true, RefRegistryDefId: FuelRegistry);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Local_лише_звіряє_без_оновлень()
    {
        var input = Input(
            RegistrySourceKind.Local,
            element: Element(("Permit_Limit", 15m)),
            current: Values((LimitField, 12.5m, false)),
            Limit);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        var diverged = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.Diverged, diverged.Kind);
        Assert.Equal(EntryId, diverged.RegistryEntryId);
        Assert.Equal("LIMIT", diverged.FieldCode);
        Assert.Equal(12.5m, diverged.CurrentValue);
        Assert.Equal(15m, diverged.SourceValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Незмінне_значення_дає_порожній_план()
    {
        // Джерело віддає число текстом і з іншою кількістю знаків: після
        // приведення до типу поля це те саме 12.5 — писати нічого.
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Permit_Limit", "12.50"), ("Name", "Факел 1")),
            current: Values((LimitField, 12.5m, false), (NameField, "Факел 1", false)),
            Limit, Name);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void External_оновлює_змінене_поле_зі_старим_і_новим_значенням()
    {
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Permit_Limit", 15m), ("Name", "Факел 1")),
            current: Values((LimitField, 12.5m, false), (NameField, "Факел 1", false)),
            Limit, Name);

        var plan = RegistrySyncPlanner.Plan(input);

        var update = Assert.Single(plan.Updates);
        Assert.Equal(new RegistrySyncUpdate(EntryId, LimitField, "LIMIT", 12.5m, 15m), update);
        Assert.Empty(plan.Events);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Зниклий_елемент_повного_знімка_лише_подія_SourceMissing()
    {
        var input = new RegistrySyncInput(
            RegistryDefId,
            RegistrySourceKind.External,
            IsCompleteSnapshot: true,
            Elements: [],
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1)],
            Entries: [new RegistrySyncEntryState(EntryId, Values((LimitField, 12.5m, false)))],
            Mappings: [Limit]);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        var missing = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.SourceMissing, missing.Kind);
        Assert.Equal(Guid1, missing.ExternalId);
        Assert.Equal(EntryId, missing.RegistryEntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Неповний_знімок_про_зникнення_нічого_не_каже()
    {
        var input = new RegistrySyncInput(
            RegistryDefId,
            RegistrySourceKind.External,
            IsCompleteSnapshot: false,
            Elements: [],
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1)],
            Entries: [new RegistrySyncEntryState(EntryId, Values())],
            Mappings: [Limit]);

        Assert.True(RegistrySyncPlanner.Plan(input).IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Hybrid_ручна_правка_лишається_і_дає_ConflictKeptManual()
    {
        var input = Input(
            RegistrySourceKind.Hybrid,
            element: Element(("Permit_Limit", 15m)),
            current: Values((LimitField, 13m, true)),
            Limit);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        var kept = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ConflictKeptManual, kept.Kind);
        Assert.Equal(13m, kept.CurrentValue);
        Assert.Equal(15m, kept.SourceValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void External_перезаписує_значення_людини_без_ConflictKeptManual()
    {
        // D-212 (1), D-211: у External людина не пише — «людське» значення є
        // залишком, і синк його перезаписує.
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Permit_Limit", 15m)),
            current: Values((LimitField, 13m, true)),
            Limit);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Equal(new RegistrySyncUpdate(EntryId, LimitField, "LIMIT", 13m, 15m), Assert.Single(plan.Updates));
        Assert.Empty(plan.Events);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Lookup_за_кодом_розв_язується_в_Id()
    {
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Fuel", "  GAS ")),
            current: Values((FuelField, 7L, false)),
            Fuel) with
        {
            LookupCodes = Codes((FuelRegistry, "GAS", 9L), (FuelRegistry, "OIL", 7L)),
        };

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Equal(new RegistrySyncUpdate(EntryId, FuelField, "FUEL", 7L, 9L), Assert.Single(plan.Updates));
        Assert.Empty(plan.Events);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Lookup_з_невідомим_кодом_дає_ValueRejected_entryRefNotFound_і_поле_не_чіпається()
    {
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Fuel", "COAL")),
            current: Values((FuelField, 7L, false)),
            Fuel) with
        {
            // Код є в іншому довіднику — це не той запис.
            LookupCodes = Codes((FuelRegistry, "OIL", 7L), (FuelRegistry + 1, "COAL", 5L)),
        };

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        var rejected = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ValueRejected, rejected.Kind);
        Assert.Equal("FUEL", rejected.FieldCode);
        Assert.Equal("ECR-REG-0422", rejected.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.entryRefNotFound", rejected.MessageKey);
        Assert.Equal("COAL", rejected.SourceValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void LookupCodes_перелічує_коди_для_розв_язання_без_порожніх_і_дублів()
    {
        var input = new RegistrySyncInput(
            RegistryDefId,
            RegistrySourceKind.External,
            IsCompleteSnapshot: true,
            Elements:
            [
                new RegistrySyncSourceElement(Guid1, Path1, Attrs(("Fuel", "GAS"), ("Permit_Limit", 1m))),
                new RegistrySyncSourceElement("G2", null, Attrs(("Fuel", " GAS "))),
                new RegistrySyncSourceElement("G3", null, Attrs(("Fuel", "  "))),
                new RegistrySyncSourceElement("G4", null, Attrs(("Fuel", null))),
                new RegistrySyncSourceElement("G5", null, Attrs(("Fuel", 42m))),
            ],
            Links: [],
            Entries: [],
            Mappings: [Limit, Fuel]);

        var codes = RegistrySyncPlanner.LookupCodes(input);

        Assert.Equal(
            new[] { new RegistrySyncLookupCode(FuelRegistry, "42"), new RegistrySyncLookupCode(FuelRegistry, "GAS") },
            codes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Hybrid_пише_лише_поля_з_активним_мапінгом()
    {
        var note = new RegistrySyncFieldMapping(NoteField, "NOTE", CellDataType.String, null, "Note", IsActive: false);
        var element = Element(("Permit_Limit", 15m), ("Note", "з AF"));
        var current = Values((LimitField, 12.5m, false), (NoteField, "локальна примітка", false));

        var hybrid = RegistrySyncPlanner.Plan(Input(RegistrySourceKind.Hybrid, element, current, Limit, note));

        var update = Assert.Single(hybrid.Updates);
        Assert.Equal(LimitField, update.RegistryFieldDefId);
        Assert.Empty(hybrid.Events);

        // Контраст: той самий вхід у довіднику External — поле з вимкненим
        // мапінгом не пишеться, але розбіжність видно.
        var external = RegistrySyncPlanner.Plan(Input(RegistrySourceKind.External, element, current, Limit, note));

        Assert.Equal(LimitField, Assert.Single(external.Updates).RegistryFieldDefId);
        var diverged = Assert.Single(external.Events);
        Assert.Equal(RegistrySyncEventKind.Diverged, diverged.Kind);
        Assert.Equal("NOTE", diverged.FieldCode);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    [InlineData("не число", "err.ECR-REG-0422.valueNotNumber")]
    [InlineData(15.0d, "err.ECR-REG-0422.valueNotDecimal")]
    public void Невалідний_тип_дає_ValueRejected_без_оновлення(object raw, string messageKey)
    {
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Permit_Limit", raw)),
            current: Values((LimitField, 12.5m, false)),
            Limit);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        var rejected = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ValueRejected, rejected.Kind);
        Assert.Equal("LIMIT", rejected.FieldCode);
        Assert.Equal("ECR-REG-0422", rejected.ErrorCode);
        Assert.Equal(messageKey, rejected.MessageKey);
        Assert.Equal(raw, rejected.SourceValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Елемент_без_зв_язку_лише_подія_ElementUnlinked()
    {
        var input = new RegistrySyncInput(
            RegistryDefId,
            RegistrySourceKind.External,
            IsCompleteSnapshot: true,
            Elements: [new RegistrySyncSourceElement("NEW-GUID", @"\\AF\ECR\Flares\FL-99", Attrs(("Permit_Limit", 1m)))],
            Links: [],
            Entries: [],
            Mappings: [Limit]);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        var unlinked = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ElementUnlinked, unlinked.Kind);
        Assert.Equal("NEW-GUID", unlinked.ExternalId);
        Assert.Null(unlinked.RegistryEntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void External_новий_елемент_повного_знімка_створює_запис_з_кодом_за_іменем()
    {
        var note = new RegistrySyncFieldMapping(NoteField, "NOTE", CellDataType.String, null, "Note", IsActive: false);
        var input = Unlinked(
            RegistrySourceKind.External,
            complete: true,
            new RegistrySyncSourceElement(
                "NEW-GUID",
                @"\\AF\ECR\Flares\FL-99",
                Attrs(("Permit_Limit", "7.5"), ("Name", null), ("Fuel", "GAS"), ("Note", "вимкнений мапінг")),
                Name: " FL-99 "),
            Limit, Name, Fuel, note) with
        {
            LookupCodes = Codes((FuelRegistry, "GAS", 9L)),
        };

        var plan = RegistrySyncPlanner.Plan(input);

        var create = Assert.Single(plan.Creates);
        Assert.Equal("NEW-GUID", create.ExternalId);
        Assert.Equal(@"\\AF\ECR\Flares\FL-99", create.ExternalPath);
        Assert.Equal("FL-99", create.Code);
        Assert.Equal("FL-99", create.DisplayName);

        // Порожнє значення (Name = null) і вимкнений мапінг у новий запис не йдуть.
        Assert.Equal(
            new[]
            {
                new RegistrySyncFieldValue(LimitField, "LIMIT", 7.5m),
                new RegistrySyncFieldValue(FuelField, "FUEL", 9L),
            },
            create.Values);
        Assert.Empty(plan.Events);
        Assert.Empty(plan.Updates);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void External_CodeMode_Auto_лишає_код_writer_у()
    {
        var input = Unlinked(
            RegistrySourceKind.External,
            complete: true,
            new RegistrySyncSourceElement("NEW-GUID", null, Attrs(), Name: "ПК-3 (370-220) лето"),
            Limit) with
        {
            CodeMode = RegistryCodeMode.Auto,
        };

        var create = Assert.Single(RegistrySyncPlanner.Plan(input).Creates);

        Assert.Null(create.Code);
        Assert.Equal("ПК-3 (370-220) лето", create.DisplayName);
        Assert.Empty(create.Values);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void External_відмова_поля_нового_запису_подія_а_запис_створюється_без_поля()
    {
        var input = Unlinked(
            RegistrySourceKind.External,
            complete: true,
            new RegistrySyncSourceElement("NEW-GUID", null, Attrs(("Permit_Limit", "не число"), ("Fuel", "COAL")), Name: "FL-99"),
            Limit, Fuel);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(Assert.Single(plan.Creates).Values);
        Assert.Equal(2, plan.Events.Count);
        Assert.All(plan.Events, e =>
        {
            Assert.Equal(RegistrySyncEventKind.ValueRejected, e.Kind);
            Assert.Equal("NEW-GUID", e.ExternalId);
            Assert.Null(e.RegistryEntryId);
        });
        Assert.Contains(plan.Events, e => e is { FieldCode: "FUEL", MessageKey: "err.ECR-REG-0422.entryRefNotFound" });
        Assert.Contains(plan.Events, e => e is { FieldCode: "LIMIT", MessageKey: "err.ECR-REG-0422.valueNotNumber" });
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External, false, "FL-99")]
    [InlineData(RegistrySourceKind.External, true, null)]
    [InlineData(RegistrySourceKind.External, true, "  ")]
    [InlineData(RegistrySourceKind.Hybrid, true, "FL-99")]
    [InlineData(RegistrySourceKind.Local, true, "FL-99")]
    public void Без_автостворення_новий_елемент_лише_подія_ElementUnlinked(
        RegistrySourceKind kind, bool complete, string? name)
    {
        // Неповний знімок (старий GUID міг не прочитатись — був би дубль), елемент без
        // імені (ні коду, ні назви), Hybrid — лише сповіщення (D-212 (2)), Local — звірка.
        var input = Unlinked(
            kind, complete, new RegistrySyncSourceElement("NEW-GUID", null, Attrs(("Permit_Limit", 1m)), name), Limit);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Creates);
        var unlinked = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ElementUnlinked, unlinked.Kind);
        Assert.Equal("NEW-GUID", unlinked.ExternalId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Перенесений_в_AF_елемент_дає_зміну_шляху_і_зіставляється_без_регістру()
    {
        var moved = new RegistrySyncSourceElement(
            Guid1.ToLowerInvariant(), @"\\AF\ECR\Onshore\FL-01", Attrs(("Permit_Limit", 12.5m)));
        var input = new RegistrySyncInput(
            RegistryDefId,
            RegistrySourceKind.External,
            IsCompleteSnapshot: true,
            Elements: [moved],
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1)],
            Entries: [new RegistrySyncEntryState(EntryId, Values((LimitField, 12.5m, false)))],
            Mappings: [Limit]);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Updates);
        Assert.Empty(plan.Events);
        var change = Assert.Single(plan.PathChanges);
        Assert.Equal(Path1, change.OldPath);
        Assert.Equal(@"\\AF\ECR\Onshore\FL-01", change.NewPath);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Відсутній_у_знімку_атрибут_не_стирає_значення()
    {
        var input = Input(
            RegistrySourceKind.External,
            element: Element(("Name", "Факел 1")),
            current: Values((LimitField, 12.5m, false), (NameField, "Факел 1", false)),
            Limit, Name);

        Assert.True(RegistrySyncPlanner.Plan(input).IsEmpty);
    }

    private static RegistrySyncInput Input(
        RegistrySourceKind kind,
        RegistrySyncSourceElement element,
        IReadOnlyDictionary<int, RegistrySyncCurrentValue> current,
        params RegistrySyncFieldMapping[] mappings)
        => new(
            RegistryDefId,
            kind,
            IsCompleteSnapshot: true,
            Elements: [element],
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1)],
            Entries: [new RegistrySyncEntryState(EntryId, current)],
            Mappings: mappings);

    private static RegistrySyncInput Unlinked(
        RegistrySourceKind kind,
        bool complete,
        RegistrySyncSourceElement element,
        params RegistrySyncFieldMapping[] mappings)
        => new(RegistryDefId, kind, complete, [element], Links: [], Entries: [], mappings);

    private static RegistrySyncSourceElement Element(params (string Attribute, object? Value)[] attributes)
        => new(Guid1, Path1, Attrs(attributes));

    private static Dictionary<string, object?> Attrs(params (string Attribute, object? Value)[] attributes)
        => attributes.ToDictionary(a => a.Attribute, a => a.Value, StringComparer.Ordinal);

    private static Dictionary<int, IReadOnlyDictionary<string, long>> Codes(
        params (int RegistryDefId, string Code, long Id)[] codes)
        => codes
            .GroupBy(c => c.RegistryDefId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, long>)g.ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase));

    private static Dictionary<int, RegistrySyncCurrentValue> Values(
        params (int FieldId, object? Value, bool Human)[] values)
        => values.ToDictionary(v => v.FieldId, v => new RegistrySyncCurrentValue(v.Value, v.Human));
}
