using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Зріз подання (ФВ-5.7) несе значення УСІХ типів клітинки, а старі зрізи читаються як були.
/// </summary>
public sealed class SubmissionPayloadTests
{
    private static CellRecord Cell(long row, int column, CellValueData value)
        => new(new CellAddress(new PeriodKey(202601), row, column), 3, value);

    [Fact]
    [Trait("Requirement", "ФВ-5.7")]
    public void Дата_булеве_запис_довідника_й_одиниця_потрапляють_у_зріз()
    {
        var json = SubmissionPayload.Write(
        [
            Cell(1, 1, new CellValueData { ValueDate = new DateTime(2026, 3, 31) }),
            Cell(1, 2, new CellValueData { ValueBool = false }),
            Cell(1, 3, new CellValueData { ValueRegistryEntryId = 4242 }),
            Cell(1, 4, new CellValueData { ValueUnitId = 17 }),
        ]);

        var cells = SubmissionPayload.Read(json);

        Assert.Equal(("2026-03-31T00:00:00.0000000", "date"), (cells[0].Value, cells[0].Type));
        Assert.Equal(("false", "bool"), (cells[1].Value, cells[1].Type));
        Assert.Equal(("4242", "ref"), (cells[2].Value, cells[2].Type));
        Assert.Equal(("17", "unit"), (cells[3].Value, cells[3].Type));
    }

    [Fact]
    [Trait("Requirement", "ФВ-5.7")]
    public void Число_текст_і_порожнеча_пишуться_байт_у_байт_як_до_виправлення()
    {
        // ⛔ Літерал — формат, яким уже записані подані зрізи і пораховано їхній ContentHash.
        const string Legacy = """[{"row":1,"column":1,"value":"12500.5"},{"row":1,"column":2,"value":"abc"},{"row":2,"column":1,"value":null}]""";

        var json = SubmissionPayload.Write(
        [
            Cell(2, 1, CellValueData.Empty),
            Cell(1, 2, new CellValueData { ValueString = "abc" }),
            Cell(1, 1, new CellValueData { ValueNumeric = 12500.5m }),
        ]);

        Assert.Equal(Legacy, json);
    }

    [Fact]
    [Trait("Requirement", "ФВ-5.7")]
    public void Старий_зріз_без_ключа_type_читається()
    {
        var cells = SubmissionPayload.Read("""[{"row":1001,"column":11,"value":"12500"},{"row":1002,"column":11,"value":null}]""");

        Assert.Equal(2, cells.Count);
        Assert.Equal(new SubmissionPayloadCell(1001, 11, "12500", null), cells[0]);
        Assert.Equal(new SubmissionPayloadCell(1002, 11, null, null), cells[1]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-9.4")]
    public void Шапка_документа_потрапляє_в_окрему_секцію_зрізу()
    {
        var json = SubmissionPayload.Write(
            [Cell(1, 1, new CellValueData { ValueNumeric = 12500.5m })],
            new Dictionary<string, DocumentHeaderValueData>
            {
                ["AREA"] = new() { ValueString = "Дніпровський" },
                ["INSPECTED"] = new() { ValueDate = new DateTime(2026, 3, 31) },
                ["ACTIVE"] = new() { ValueBool = true },
                ["CONTRACTOR"] = new() { ValueRegistryEntryId = 777 },
                ["FLOW_UNIT"] = new() { ValueUnitId = 5 },
            });

        // Клітинки читаються так само, як завжди — секція header їх не заступає.
        var cells = SubmissionPayload.Read(json);
        Assert.Single(cells);
        Assert.Equal("12500.5", cells[0].Value);

        var header = SubmissionPayload.ReadHeader(json);
        Assert.Equal(5, header.Count);
        Assert.Equal(new SubmissionPayloadHeaderValue("Дніпровський", null), header["AREA"]);
        Assert.Equal(new SubmissionPayloadHeaderValue("2026-03-31T00:00:00.0000000", "date"), header["INSPECTED"]);
        Assert.Equal(new SubmissionPayloadHeaderValue("true", "bool"), header["ACTIVE"]);
        Assert.Equal(new SubmissionPayloadHeaderValue("777", "ref"), header["CONTRACTOR"]);
        Assert.Equal(new SubmissionPayloadHeaderValue("5", "unit"), header["FLOW_UNIT"]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-9.4")]
    public void Без_значень_шапки_секція_header_відсутня_а_зріз_клітинок_не_міняється()
    {
        var cells = new[] { Cell(1, 1, new CellValueData { ValueNumeric = 1m }) };

        // ⚠ `null` (шаблон без полів шапки) і порожній словник (поля є, але жодне не
        // заповнене) мають дати РІВНО той самий payload — голий масив клітинок, як до
        // ФВ-9.4: інакше ContentHash документів без шапки зрушився б без потреби.
        var withoutHeaderArg = SubmissionPayload.Write(cells);
        var withNullHeader = SubmissionPayload.Write(cells, null);
        var withEmptyHeader = SubmissionPayload.Write(cells, new Dictionary<string, DocumentHeaderValueData>());

        Assert.Equal(withoutHeaderArg, withNullHeader);
        Assert.Equal(withoutHeaderArg, withEmptyHeader);
        Assert.StartsWith("[", withoutHeaderArg, StringComparison.Ordinal);

        Assert.Empty(SubmissionPayload.ReadHeader(withoutHeaderArg));
    }

    [Fact]
    [Trait("Requirement", "ФВ-9.4")]
    public void Старий_зріз_без_секції_header_дає_порожній_словник_і_не_падає()
    {
        // ⛔ Літерал у форматі, яким уже записані подані зрізи ДО ФВ-9.4 (та сама
        // фікстура, що вище, — жодної секції header у ній немає взагалі).
        const string Legacy = """[{"row":1,"column":1,"value":"12500.5"},{"row":1,"column":2,"value":"abc"},{"row":2,"column":1,"value":null}]""";

        var header = SubmissionPayload.ReadHeader(Legacy);

        Assert.Empty(header);

        // І клітинки з того самого зрізу читаються як і раніше.
        var cells = SubmissionPayload.Read(Legacy);
        Assert.Equal(3, cells.Count);
    }
}
