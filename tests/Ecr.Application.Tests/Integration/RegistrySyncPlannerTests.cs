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
/// <see cref="External_новий_елемент_повного_знімка_створює_запис_з_назвою_за_іменем"/>;</item>
/// <item>Manual створює запис з імені AF (L4-13, Q6=C) — червоний
/// <see cref="L4_13_Manual_не_створює_запис_з_імені_AF_а_пише_подію_з_причиною"/>;</item>
/// <item><c>Ignore</c> як <c>MarkOrphaned</c> — червоні обидва <c>Ignore</c>-випадки
/// <see cref="Ignore_і_Local_лише_подія_SourceMissing"/>;</item>
/// <item>позначка зникнення щоразу, а не лише першого разу — червоні <c>alreadyMarked</c>-випадки
/// <see cref="MarkOrphaned_позначає_зв_язок_один_раз_і_пише_SourceMissing"/> і всі
/// <see cref="Deactivate_на_вже_позначеному_зв_язку"/>;</item>
/// <item><c>Hybrid</c> вимикає запис, який людина ввімкнула після позначки — червоний
/// <c>(Hybrid, true, false)</c> <see cref="Deactivate_на_вже_позначеному_зв_язку"/>;</item>
/// <item><c>Hybrid</c> вмикає сам — червоний <c>(Hybrid, false)</c>
/// <see cref="Повернення_елемента_знімає_позначку_External_вмикає_Hybrid_лише_подія"/>;</item>
/// <item>перепривʼязка й за кількох кандидатів — червоні
/// <see cref="Кілька_кандидатів_на_шляху_нічого_не_пишуть_лише_події"/> і
/// <see cref="Два_зниклі_зв_язки_з_одним_шляхом_неоднозначні"/>;</item>
/// <item>перепривʼязка на неповному знімку — червоний <see cref="Неповний_знімок_не_переприв_язує"/>;</item>
/// <item>кандидатом стає зв'язок, чий старий GUID ще є у знімку — червоний
/// <see cref="Старий_GUID_ще_є_новий_елемент_на_тому_самому_шляху_не_переприв_язується"/>.</item>
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
    public void External_новий_елемент_повного_знімка_створює_запис_з_назвою_за_іменем()
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
        Assert.Null(create.Code);
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

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("FL-99")]
    [InlineData("ПК-3 (370-220) лето")]
    public void L4_13_Manual_не_створює_запис_з_імені_AF_а_пише_подію_з_причиною(string name)
    {
        // Q6=C (HU-11): ім'я не нормалізується й не стає кодом; створення лише в Auto.
        var input = Unlinked(
            RegistrySourceKind.External,
            complete: true,
            new RegistrySyncSourceElement("NEW-GUID", null, Attrs(), Name: name),
            Limit) with
        {
            CodeMode = RegistryCodeMode.Manual,
        };

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Creates);
        var unlinked = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ElementUnlinked, unlinked.Kind);
        Assert.Equal("NEW-GUID", unlinked.ExternalId);
        Assert.Equal(RegistrySyncPlanner.CodeModeManualReason, unlinked.Reason);
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

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External, false)]
    [InlineData(RegistrySourceKind.External, true)]
    [InlineData(RegistrySourceKind.Hybrid, false)]
    [InlineData(RegistrySourceKind.Hybrid, true)]
    public void MarkOrphaned_позначає_зв_язок_один_раз_і_пише_SourceMissing(RegistrySourceKind kind, bool alreadyMarked)
    {
        var plan = RegistrySyncPlanner.Plan(Missing(kind, RegistryMissingPolicy.MarkOrphaned, alreadyMarked ? Since : null));

        // MissingMark лише для ще не позначеного: «відколи» не зсувається щоночі.
        Assert.Equal(
            alreadyMarked ? Array.Empty<RegistrySyncLinkMark>() : new[] { new RegistrySyncLinkMark(Guid1, EntryId) },
            plan.MissingMarks);
        Assert.Equal(RegistrySyncEventKind.SourceMissing, Assert.Single(plan.Events).Kind);
        Assert.Empty(plan.Deactivations);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External)]
    [InlineData(RegistrySourceKind.Hybrid)]
    public void Deactivate_позначає_і_вимикає_запис_без_події_SourceMissing(RegistrySourceKind kind)
    {
        var plan = RegistrySyncPlanner.Plan(Missing(kind, RegistryMissingPolicy.Deactivate, since: null));

        Assert.Equal(new RegistrySyncLinkMark(Guid1, EntryId), Assert.Single(plan.MissingMarks));
        Assert.Equal(new RegistrySyncActivation(EntryId, Guid1), Assert.Single(plan.Deactivations));

        // Подію Deactivated пише виконавець після успіху — у плані подій немає.
        Assert.Empty(plan.Events);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External, true, true)]
    [InlineData(RegistrySourceKind.Hybrid, true, false)]
    [InlineData(RegistrySourceKind.External, false, false)]
    [InlineData(RegistrySourceKind.Hybrid, false, false)]
    public void Deactivate_на_вже_позначеному_зв_язку(RegistrySourceKind kind, bool entryActive, bool deactivates)
    {
        // Вимкнений уже — нічого (ідемпотентно). Увімкнений після позначки: External —
        // вимкнути знову (людини там немає), Hybrid — людина ввімкнула, вона виграє.
        var plan = RegistrySyncPlanner.Plan(
            Missing(kind, RegistryMissingPolicy.Deactivate, Since, entryActive));

        Assert.Empty(plan.MissingMarks);
        Assert.Equal(deactivates, plan.Deactivations.Count == 1);
        Assert.Equal(!deactivates, plan.IsEmpty);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External, RegistryMissingPolicy.Ignore)]
    [InlineData(RegistrySourceKind.Hybrid, RegistryMissingPolicy.Ignore)]
    [InlineData(RegistrySourceKind.Local, RegistryMissingPolicy.MarkOrphaned)]
    [InlineData(RegistrySourceKind.Local, RegistryMissingPolicy.Deactivate)]
    public void Ignore_і_Local_лише_подія_SourceMissing(RegistrySourceKind kind, RegistryMissingPolicy policy)
    {
        var plan = RegistrySyncPlanner.Plan(Missing(kind, policy, since: null));

        var missing = Assert.Single(plan.Events);
        Assert.Equal(new RegistrySyncEvent(RegistrySyncEventKind.SourceMissing, Guid1, EntryId), missing);
        Assert.Empty(plan.MissingMarks);
        Assert.Empty(plan.Deactivations);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External, RegistryMissingPolicy.MarkOrphaned)]
    [InlineData(RegistrySourceKind.External, RegistryMissingPolicy.Deactivate)]
    [InlineData(RegistrySourceKind.External, RegistryMissingPolicy.Ignore)]
    [InlineData(RegistrySourceKind.Hybrid, RegistryMissingPolicy.Deactivate)]
    public void Неповний_знімок_жодна_політика_нічого_не_робить(RegistrySourceKind kind, RegistryMissingPolicy policy)
    {
        var input = Missing(kind, policy, since: null) with { IsCompleteSnapshot = false };

        Assert.True(RegistrySyncPlanner.Plan(input).IsEmpty);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External, false)]
    [InlineData(RegistrySourceKind.External, true)]
    [InlineData(RegistrySourceKind.Hybrid, false)]
    [InlineData(RegistrySourceKind.Hybrid, true)]
    public void Повернення_елемента_знімає_позначку_External_вмикає_Hybrid_лише_подія(
        RegistrySourceKind kind, bool entryActive)
    {
        var input = Returned(kind, since: Since, entryActive);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Equal(new RegistrySyncLinkMark(Guid1, EntryId), Assert.Single(plan.MissingClears));

        if (entryActive)
        {
            Assert.Empty(plan.Reactivations);
            Assert.Empty(plan.Events);
        }
        else if (kind == RegistrySourceKind.External)
        {
            Assert.Equal(new RegistrySyncActivation(EntryId, Guid1), Assert.Single(plan.Reactivations));
            Assert.Empty(plan.Events);
        }
        else
        {
            // Hybrid (Q6): вмикає людина — синк лише показує розбіжність.
            Assert.Empty(plan.Reactivations);
            Assert.Equal(
                new RegistrySyncEvent(
                    RegistrySyncEventKind.Diverged, Guid1, EntryId, "@active", CurrentValue: false, SourceValue: true),
                Assert.Single(plan.Events));
        }
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External)]
    [InlineData(RegistrySourceKind.Hybrid)]
    [InlineData(RegistrySourceKind.Local)]
    public void Вимкнений_без_позначки_вимкнула_людина_синк_не_вмикає(RegistrySourceKind kind)
    {
        Assert.True(RegistrySyncPlanner.Plan(Returned(kind, since: null, entryActive: false)).IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Local_повернення_нічого_не_пише()
    {
        Assert.True(RegistrySyncPlanner.Plan(Returned(RegistrySourceKind.Local, Since, entryActive: false)).IsEmpty);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(RegistrySourceKind.External)]
    [InlineData(RegistrySourceKind.Hybrid)]
    public void Перестворений_в_AF_елемент_переприв_язується_за_шляхом(RegistrySourceKind kind)
    {
        // Старого GUID у повному знімку немає, новий — рівно один на тому самому шляху
        // (шлях AF регістронезалежний).
        var input = Relink(kind, [new RegistrySyncSourceElement("NEW-GUID", Path1.ToUpperInvariant(), Attrs(), "FL-01")]);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Equal(
            new RegistrySyncRelink(EntryId, Guid1, "NEW-GUID", Path1.ToUpperInvariant()),
            Assert.Single(plan.Relinks));

        // Ні автостворення, ні «зник»/«неприв'язаний», ні позначки.
        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Events);
        Assert.Empty(plan.MissingMarks);
        Assert.Empty(plan.Deactivations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Кілька_кандидатів_на_шляху_нічого_не_пишуть_лише_події()
    {
        var input = Relink(
            RegistrySourceKind.External,
            [
                new RegistrySyncSourceElement("NEW-A", Path1, Attrs(), "FL-01"),
                new RegistrySyncSourceElement("NEW-B", Path1, Attrs(), "FL-01b"),
            ]);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Relinks);
        Assert.Empty(plan.Creates);
        Assert.Empty(plan.MissingMarks);
        Assert.Empty(plan.Deactivations);
        Assert.Equal(
            new[]
            {
                new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, "NEW-A", null),
                new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, "NEW-B", null),
                new RegistrySyncEvent(RegistrySyncEventKind.SourceMissing, Guid1, EntryId),
            },
            plan.Events);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Два_зниклі_зв_язки_з_одним_шляхом_неоднозначні()
    {
        var input = Relink(
            RegistrySourceKind.External,
            [new RegistrySyncSourceElement("NEW-GUID", Path1, Attrs(), "FL-01")]) with
        {
            Links = [new RegistrySyncLink(Guid1, EntryId, Path1), new RegistrySyncLink("OLD-2", EntryId + 1, Path1)],
        };

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Relinks);
        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Deactivations);
        Assert.Equal(2, plan.Events.Count(e => e.Kind == RegistrySyncEventKind.SourceMissing));
        Assert.Single(plan.Events, e => e.Kind == RegistrySyncEventKind.ElementUnlinked);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Старий_GUID_ще_є_новий_елемент_на_тому_самому_шляху_не_переприв_язується()
    {
        // Старий елемент перенесли, а на його місце поставили новий: зв'язок живий
        // (зміна шляху), новий елемент — новий запис.
        var input = Relink(
            RegistrySourceKind.External,
            [
                new RegistrySyncSourceElement(Guid1, @"\\AF\ECR\Onshore\FL-01", Attrs(), "FL-01"),
                new RegistrySyncSourceElement("NEW-GUID", Path1, Attrs(), "FL-01N"),
            ]);

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Relinks);
        Assert.Equal("NEW-GUID", Assert.Single(plan.Creates).ExternalId);
        Assert.Equal(Guid1, Assert.Single(plan.PathChanges).ExternalId);
        Assert.Empty(plan.MissingMarks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.10")]
    public void Неповний_знімок_не_переприв_язує()
    {
        var input = Relink(
            RegistrySourceKind.External,
            [new RegistrySyncSourceElement("NEW-GUID", Path1, Attrs(), "FL-01")]) with
        {
            IsCompleteSnapshot = false,
        };

        var plan = RegistrySyncPlanner.Plan(input);

        Assert.Empty(plan.Relinks);
        Assert.Empty(plan.Creates);
        Assert.Equal(
            new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, "NEW-GUID", null), Assert.Single(plan.Events));
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

    private static readonly DateTime Since = new(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>Повний знімок без елемента зв'язку Guid1 ↔ EntryId.</summary>
    private static RegistrySyncInput Missing(
        RegistrySourceKind kind, RegistryMissingPolicy policy, DateTime? since, bool entryActive = true)
        => new(
            RegistryDefId,
            kind,
            IsCompleteSnapshot: true,
            Elements: [],
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1, since)],
            Entries: [new RegistrySyncEntryState(EntryId, Values((LimitField, 12.5m, false)), entryActive)],
            Mappings: [Limit],
            OnMissingInSource: policy);

    /// <summary>Елемент зв'язку Guid1 ↔ EntryId знову в знімку (значення ті самі).</summary>
    private static RegistrySyncInput Returned(RegistrySourceKind kind, DateTime? since, bool entryActive)
        => new(
            RegistryDefId,
            kind,
            IsCompleteSnapshot: true,
            Elements: [Element(("Permit_Limit", 12.5m))],
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1, since)],
            Entries: [new RegistrySyncEntryState(EntryId, Values((LimitField, 12.5m, false)), entryActive)],
            Mappings: [Limit],
            OnMissingInSource: RegistryMissingPolicy.Deactivate);

    /// <summary>Зв'язок Guid1 ↔ EntryId на шляху Path1 і задані елементи знімка; політика Deactivate.</summary>
    private static RegistrySyncInput Relink(RegistrySourceKind kind, RegistrySyncSourceElement[] elements)
        => new(
            RegistryDefId,
            kind,
            IsCompleteSnapshot: true,
            Elements: elements,
            Links: [new RegistrySyncLink(Guid1, EntryId, Path1)],
            Entries: [new RegistrySyncEntryState(EntryId, Values())],
            Mappings: [Limit],
            CodeMode: RegistryCodeMode.Auto,
            OnMissingInSource: RegistryMissingPolicy.Deactivate);

    private static RegistrySyncInput Unlinked(
        RegistrySourceKind kind,
        bool complete,
        RegistrySyncSourceElement element,
        params RegistrySyncFieldMapping[] mappings)
        => new(RegistryDefId, kind, complete, [element], Links: [], Entries: [], mappings, CodeMode: RegistryCodeMode.Auto);

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
