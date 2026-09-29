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
/// <see cref="Мапінг_без_start_чи_end_відхиляється"/> (випадок <c>$end</c>).
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
            [Start(), End(), new(Column("FLARE", CellDataType.String, id: 3), "Flare", SourceEventAttributeScope.PrimaryElement)],
            SourceEventVolumeMode.RowWindow);

        Assert.Equal(
            (EntityId, DocumentId, TableId, SourceEventVolumeMode.RowWindow, true),
            (map.SourceEntityId, map.DocumentId, map.TableDefId, map.VolumeMode, map.IsActive));
        Assert.Equal(["$start", "$end", "Flare"], map.Fields.Select(f => f.SourceAttribute));
        Assert.Equal(
            (3, SourceEventAttributeScope.PrimaryElement, SourceEventValueKind.Direct),
            (map.Fields[2].TargetColumnDefId, map.Fields[2].AttributeScope, map.Fields[2].ValueKind));
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
