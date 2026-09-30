// tests/Ecr.Domain.Tests/External/SourceEventMapTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Інваріанти мапінгу «шаблон подій джерела → динамічна таблиця»
/// (FEATURE-HSE301-VIEW §4.7.3, крок F9, V-17 → <c>D-186</c>).
/// </summary>
/// <remarks>
/// ⚠ Швидкий рівень: без бази. Що унікальність і ключі тримає й сама схема,
/// доводить <c>SourceEventSchemaTests</c> в <c>Ecr.Infrastructure.Tests</c>.
///
/// Мутаційний доказ (F9): у <see cref="SourceEventMap.Create"/> пропускати
/// <c>$end</c> у переліку обов'язкових — червоніє
/// <see cref="Мапінг_без_start_чи_end_відхиляється"/> (випадок <c>$end</c>) і
/// <see cref="Останній_end_прибрати_не_можна"/>.
/// </remarks>
public sealed class SourceEventMapTests
{
    private const int TableId = 10;
    private const int OtherTableId = 11;
    private const int EntityId = 7;
    private const long DocumentId = 42;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Мапінг_бере_таблицю_й_поля_і_починає_активним()
    {
        var map = SourceEventMap.Create(
            EntityId, DocumentId, DynamicTable(),
            [
                Start(),
                End(),
                new(Column("EVENT_NAME", CellDataType.String, id: 3), "$Name"),
                new(Column("CATEGORY", CellDataType.Lookup, id: 4), "Category", SourceEventAttributeScope.PrimaryElement,
                    SourceEventValueKind.LookupByCode),
            ],
            SourceEventVolumeMode.RowWindow);

        Assert.Equal(
            (EntityId, DocumentId, TableId, SourceEventVolumeMode.RowWindow, true),
            (map.SourceEntityId, map.DocumentId, map.TableDefId, map.VolumeMode, map.IsActive));
        Assert.Equal(
            ["$start", "$end", "$name", "Category"],
            map.Fields.Select(f => f.SourceAttribute));

        // «$Name» зберігається канонічним: синхронізація порівнює Ordinal.
        var category = map.Fields[3];
        Assert.Equal(
            (4, SourceEventAttributeScope.PrimaryElement, SourceEventValueKind.LookupByCode),
            (category.TargetColumnDefId, category.AttributeScope, category.ValueKind));
        Assert.Null(map.FilterAttribute);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData("$start")]
    [InlineData("$end")]
    public void Мапінг_без_start_чи_end_відхиляється(string missing)
    {
        // Мапінг без кінця дав би рядки без тривалості й без вікна об'єму —
        // помилку налаштування ловила б синхронізація, а не налаштування.
        var fields = new List<SourceEventFieldSpec> { Start(), End() }
            .Where(f => f.SourceAttribute != missing)
            .ToList();

        var error = Assert.Throws<DomainException>(() => SourceEventMap.Create(
            EntityId, DocumentId, DynamicTable(), fields, SourceEventVolumeMode.None));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapStartEndRequired");
        Assert.Equal(missing, error.Details!["attribute"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Останній_end_прибрати_не_можна()
    {
        var map = Create();

        var error = Assert.Throws<DomainException>(() => map.RemoveField(2));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapStartEndRequired");
        Assert.Equal("$end", error.Details!["attribute"]);
        Assert.Equal(2, map.Fields.Count);

        // Звичайне поле прибирається, і відсутнє — не помилка.
        map.AddField(new(Column("EVENT_NAME", CellDataType.String, id: 3), "$name"));
        Assert.True(map.RemoveField(3));
        Assert.False(map.RemoveField(99));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData(TableRowMode.Fixed)]
    [InlineData(TableRowMode.Mixed)]
    public void Ціль_лише_динамічна_таблиця(TableRowMode mode)
    {
        var error = Assert.Throws<DomainException>(() => SourceEventMap.Create(
            EntityId, DocumentId, Table(mode), [Start(), End()], SourceEventVolumeMode.None));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapTargetNotDynamic");
        Assert.Equal(mode.ToString(), error.Details!["rowMode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Колонка_з_іншої_таблиці_відхиляється()
    {
        var map = Create();

        var error = Assert.Throws<DomainException>(() => map.AddField(
            new(Column("FOREIGN", CellDataType.String, id: 30, tableDefId: OtherTableId), "Flare")));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapColumnNotInTable");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData(CellDataType.String)]
    [InlineData(CellDataType.Decimal)]
    public void Час_події_лише_в_Date_колонку(CellDataType type)
    {
        var error = Assert.Throws<DomainException>(() => SourceEventMap.Create(
            EntityId, DocumentId, DynamicTable(),
            [new(Column("START_AT", type, id: 1), "$start"), End()],
            SourceEventVolumeMode.None));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapStartEndNotDate");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData("$duration", SourceEventAttributeScope.Event, SourceEventValueKind.Direct)]
    [InlineData("$name", SourceEventAttributeScope.PrimaryElement, SourceEventValueKind.Direct)]
    [InlineData("$name", SourceEventAttributeScope.Event, SourceEventValueKind.LookupByName)]
    public void Зарезервований_атрибут_лише_відомий_і_прямий(
        string attribute, SourceEventAttributeScope scope, SourceEventValueKind kind)
    {
        var map = Create();

        var error = Assert.Throws<DomainException>(() => map.AddField(
            new(Column("EVENT_NAME", CellDataType.Lookup, id: 3), attribute, scope, kind)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapReservedAttributeInvalid");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData(CellDataType.Lookup, SourceEventValueKind.Direct)]
    [InlineData(CellDataType.String, SourceEventValueKind.LookupByCode)]
    [InlineData(CellDataType.Decimal, SourceEventValueKind.ValueMap)]
    [InlineData(CellDataType.Formula, SourceEventValueKind.Direct)]
    [InlineData(CellDataType.Calculated, SourceEventValueKind.Direct)]
    public void Вид_значення_має_пасувати_до_типу_колонки(CellDataType type, SourceEventValueKind kind)
    {
        var map = Create();

        var error = Assert.Throws<DomainException>(() => map.AddField(
            new(Column("TARGET", type, id: 3), "Attr", SourceEventAttributeScope.Event, kind)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.eventMapValueKindMismatch");
        Assert.Equal(kind.ToString(), error.Details!["valueKind"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Друге_поле_на_ту_саму_колонку_відхиляється()
    {
        var map = Create();
        map.AddField(new(Column("VOLUME", CellDataType.Decimal, id: 5), "Volume"));

        var error = Assert.Throws<DomainException>(() => map.AddField(
            new(Column("VOLUME", CellDataType.Decimal, id: 5), "VolumeCorrected")));

        AssertKey(error, "ECR-INT-0409", "err.ECR-INT-0409.eventMapColumnTaken");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Явна_відповідність_лише_для_ValueMap_і_без_дублів()
    {
        var map = Create();
        var byCode = map.AddField(new(Column("CATEGORY", CellDataType.Lookup, id: 4), "Category",
            SourceEventAttributeScope.Event, SourceEventValueKind.LookupByCode));
        var season = map.AddField(new(Column("SEASON", CellDataType.Lookup, id: 6), "Season",
            SourceEventAttributeScope.PrimaryElement, SourceEventValueKind.ValueMap));

        // На полі «за кодом» відповідність не діяла б ніколи.
        AssertKey(
            Assert.Throws<DomainException>(() => byCode.AddValue("V6", 100)),
            "ECR-INT-0422", "err.ECR-INT-0422.eventMapValueMapNotAllowed");

        var winter = season.AddValue("  зима ", 200);
        Assert.Equal(("зима", 200L), (winter.SourceValue, winter.RegistryEntryId));

        // Без регістру — як UQ_SEVM_Value у базі (_CI_).
        AssertKey(
            Assert.Throws<DomainException>(() => season.AddValue("ЗИМА", 201)),
            "ECR-INT-0409", "err.ECR-INT-0409.eventMapSourceValueTaken");
        Assert.Single(season.Values);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Звуження_задається_трьома_значеннями_разом()
    {
        var map = Create();

        map.SetFilter(" Flare ", SourceEventAttributeScope.PrimaryElement, " FL-370 ");
        Assert.Equal(
            ("Flare", (SourceEventAttributeScope?)SourceEventAttributeScope.PrimaryElement, "FL-370"),
            (map.FilterAttribute, map.FilterScope, map.FilterValue));

        AssertKey(
            Assert.Throws<DomainException>(() => map.SetFilter("Flare", null, "FL-370")),
            "ECR-INT-0422", "err.ECR-INT-0422.eventMapFilterIncomplete");
        AssertKey(
            Assert.Throws<DomainException>(() => map.SetFilter("Flare", SourceEventAttributeScope.Event, " ")),
            "ECR-INT-0422", "err.ECR-INT-0422.eventMapFilterIncomplete");

        // Відмова не зачепила чинного звуження; порожнє — знімає його.
        Assert.Equal("FL-370", map.FilterValue);
        map.SetFilter(null, null, null);
        Assert.Equal(((string?)null, (SourceEventAttributeScope?)null, (string?)null),
            (map.FilterAttribute, map.FilterScope, map.FilterValue));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Режим_об_єму_й_активність_перемикаються_даними()
    {
        var map = Create();

        map.SetVolumeMode(SourceEventVolumeMode.EventAttribute);
        map.Deactivate();
        Assert.Equal((SourceEventVolumeMode.EventAttribute, false), (map.VolumeMode, map.IsActive));

        map.Activate();
        Assert.True(map.IsActive);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetVolumeMode((SourceEventVolumeMode)9));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A6")]
    public void Заміна_полів_повна_і_дозволяє_поміняти_атрибут_початку_без_проміжного_стану()
    {
        var map = Create();

        // «$start» переїжджає на іншу колонку: через RemoveField+AddField це був би стан «без початку».
        map.ReplaceFields(
        [
            new(Column("START_2", CellDataType.Date, id: 5), "$start"),
            End(),
            new(Column("EVENT_NAME", CellDataType.String, id: 3), "$name"),
        ]);

        Assert.Equal(["$start", "$end", "$name"], map.Fields.Select(f => f.SourceAttribute));
        Assert.Equal([5, 2, 3], map.Fields.Select(f => f.TargetColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A6")]
    public void Відмовлена_заміна_полів_лишає_старі_поля_як_були()
    {
        var map = Create();

        // Без «$end» — відмова; поля не стають порожніми чи напівзамінними.
        AssertKey(
            Assert.Throws<DomainException>(() => map.ReplaceFields([Start()])),
            "ECR-INT-0422", "err.ECR-INT-0422.eventMapStartEndRequired");
        Assert.Equal(["$start", "$end"], map.Fields.Select(f => f.SourceAttribute));

        // Та сама колонка двічі — відмова домену, а не тиха втрата одного з полів.
        AssertKey(
            Assert.Throws<DomainException>(() => map.ReplaceFields(
                [Start(), End(), new(Column("START_AT", CellDataType.Date, id: 1), "$name")])),
            "ECR-INT-0409", "err.ECR-INT-0409.eventMapColumnTaken");
        Assert.Equal(2, map.Fields.Count);
    }

    private static SourceEventMap Create()
        => SourceEventMap.Create(EntityId, DocumentId, DynamicTable(), [Start(), End()], SourceEventVolumeMode.None);

    private static SourceEventFieldSpec Start() => new(Column("START_AT", CellDataType.Date, id: 1), "$start");

    private static SourceEventFieldSpec End() => new(Column("END_AT", CellDataType.Date, id: 2), "$end");

    private static TableDef DynamicTable() => Table(TableRowMode.Dynamic);

    private static TableDef Table(TableRowMode mode)
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("FLARE_EVENTS"), Text("Flare events"), 1,
            TableLayoutKind.PerPeriodInstance, mode);

        // Id присвоює база; тут — щоб мапінг мав що зберегти.
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(table, TableId);
        return table;
    }

    private static ColumnDef Column(string code, CellDataType type, int id, int tableDefId = TableId)
    {
        var column = new ColumnDef(tableDefId, EcrCode.Create(code), Text(code), id, type);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(column, id);
        return column;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void AssertKey(DomainException error, string code, string messageKey)
    {
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(messageKey, error.Details?["messageKey"]);
    }
}
