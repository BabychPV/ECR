using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Читання значення комірки з запиту.
/// </summary>
/// <remarks>
/// ⛔ Тест народився з аудиту (`A7-01`). Обробник розбирав значення за
/// <b>типом CLR</b>, а через HTTP у <c>PatchCell.Value</c> завжди приходить
/// <see cref="JsonElement"/>: жодна гілка не спрацьовувала, і будь-яке число,
/// дата й булеве значення лягали в базу текстом.
/// <para>
/// ⚠ Тому тут значення проходять через <b>справжню серіалізацію</b>, а не
/// конструюються полями. Тест, який кладе в <c>PatchCell</c> готовий
/// <c>decimal</c>, перевіряє шлях, якого в продуктиві не існує — саме так
/// дефект і прожив три етапи.
/// </para>
/// </remarks>
public sealed class CellValueReaderTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Значення так, як воно приходить через API: після JSON.</summary>
    private static object? FromWire(object? value)
    {
        var request = new PatchCellsRequest(
            1, 202603, "UserEdit", [new PatchRow("R1", "0x01", [new PatchCell("C1", value)])]);

        var json = JsonSerializer.Serialize(request, Web);

        return JsonSerializer.Deserialize<PatchCellsRequest>(json, Web)!.Rows[0].Cells[0].Value;
    }

    private static ColumnDef Column(CellDataType type)
        => new(tableDefId: 1, EcrCode.Create("C1"), new LocalizedText(), ordinal: 1, type);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Число_з_HTTP_лягає_у_ValueNumeric_а_не_в_текст()
    {
        var wire = FromWire(12.5m);

        // Саме тут був дефект: JsonElement не є ані decimal, ані int.
        Assert.IsType<JsonElement>(wire);

        var data = CellValueReader.Read(wire, Column(CellDataType.Decimal));

        Assert.NotNull(data);
        Assert.Equal(12.5m, data.ValueNumeric);
        Assert.Null(data.ValueString);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Булеве_з_HTTP_лягає_у_ValueBool()
    {
        var data = CellValueReader.Read(FromWire(true), Column(CellDataType.Bool));

        Assert.NotNull(data);
        Assert.True(data.ValueBool);
        Assert.Null(data.ValueString);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дата_приходить_рядком_і_лягає_у_ValueDate()
    {
        // ⚠ JSON не має типу дати: вона завжди рядок. Розбір за типом CLR
        // клав би її в текст, і колонка Date лишалася б порожньою назавжди.
        var data = CellValueReader.Read(FromWire("2026-03-15T00:00:00Z"), Column(CellDataType.Date));

        Assert.NotNull(data);
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), data.ValueDate);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Довідникова_комірка_тримає_ідентифікатор_а_не_число()
    {
        // ⛔ Ідентифікатор запису в ValueNumeric — це втрачений зв'язок із
        // довідником: сканер осиротілих рядків такої комірки не бачить.
        var data = CellValueReader.Read(FromWire(42), Column(CellDataType.Lookup));

        Assert.NotNull(data);
        Assert.Equal(42, data.ValueRegistryEntryId);
        Assert.Null(data.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Одинична_комірка_тримає_ідентифікатор_одиниці()
    {
        var data = CellValueReader.Read(FromWire(7), Column(CellDataType.Unit));

        Assert.NotNull(data);
        Assert.Equal(7, data.ValueUnitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.11")]
    public void Текст_у_числовій_колонці_відхиляється_а_не_стає_нулем()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => CellValueReader.Read(FromWire("н/д"), Column(CellDataType.Decimal)));

        Assert.Equal("ECR-CELL-0422", error.ErrorCode);

        // ⚠ У повідомленні є колонка й очікуваний тип, але немає самого
        // значення: воно може бути персональними даними (ФВ-6.11).
        Assert.Contains("C1", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("н/д", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.11")]
    public void Порожнє_значення_означає_стерти_а_не_записати_порожнечу()
    {
        // R-B4: `value: null` — стерти; явна порожнеча — окремий прапорець.
        Assert.Null(CellValueReader.Read(FromWire(null), Column(CellDataType.Decimal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.11")]
    public void Розгортання_дає_валідації_число_а_не_JsonElement()
    {
        // Без цього правило «обсяг більший за нуль» не падало б, а мовчки
        // НЕ ЗНАХОДИЛО порушень: JsonElement не дорівнює жодному числу.
        Assert.Equal(12.5m, CellValueReader.Normalize(FromWire(12.5m)));
        Assert.Equal("текст", CellValueReader.Normalize(FromWire("текст")));
        Assert.Equal(true, CellValueReader.Normalize(FromWire(true)));
        Assert.Null(CellValueReader.Normalize(FromWire(null)));
    }
}
