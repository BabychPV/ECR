// tests/Ecr.Adapters.Tests/Excel/ImportDiffBuilderLiveDefinitionTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Прев'ю імпорту звіряється з ЖИВИМ <see cref="ColumnDef"/>, а не з
/// метаданими, зафіксованими в карті воркбука на момент експорту (аудит
/// 2026-09-16, §8.1) — і вміє <c>CellDataType.Unit</c> (§8.3).
/// </summary>
/// <remarks>
/// ⛔ <c>ImportDiffBuilder.Build</c> — чиста функція без бази (Q-168), тож
/// перевірка тут теж без бази: предмет — рівно те, ЧИЇ метадані вона читає і
/// які типи розуміє.
/// </remarks>
public sealed class ImportDiffBuilderLiveDefinitionTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "7001001";
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Колонка_що_стала_обчислюваною_ПІСЛЯ_експорту_відхиляється()
    {
        // ⛔ Аудит §8.1. `column.IsCalculated` — прапорець із карти воркбука,
        // зафіксований на момент ЕКСПОРТУ. Між експортом і повторним імпортом
        // адмін перепублікував шаблон і зробив колонку обчислюваною: прев'ю
        // показувало зміну як ЗАСТОСОВНУ (застарілий прапорець `false`), і
        // `ApplyAsync` падав пізніше — або, гірше, значення записувалося поверх
        // формули й зникало при найближчому перерахунку.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var amount = builder.Column(table, "Amount", CellDataType.Decimal);
        builder.Row(table, RowKey, 1);

        // Живе визначення: колонка тепер ТІЛЬКИ ДЛЯ ЧИТАННЯ (те саме, що
        // `IsCalculated` у `ExcelExporter`).
        amount.SetReadOnly(true);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 777;

        // Карта воркбука несе ЗАСТАРІЛЕ `IsCalculated: false`.
        var block = Block([new ExcelColumnRef(amount.Id, "Amount", 1, IsCalculated: false, null)]);

        var diff = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            NoDecisions, NoLookups, RowIds, Versions, []);

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal("ECR-CELL-4221", rejection.ReasonCode);
        Assert.Equal("Amount", rejection.ColumnCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довідник_Lookup_береться_з_живого_визначення_а_не_з_карти_воркбука()
    {
        // ⛔ Аудит §8.1, найнебезпечніша половина. Якщо застарілий
        // `column.LookupRegistryDefId` із моменту експорту випадково збігається з
        // ІНШИМ довідником у знімку, введений користувачем код тихо резолвиться
        // у сутність ЧУЖОГО довідника — без помилки, з неправильними даними.
        const int LiveRegistry = 11;
        const int StaleRegistry = 22;

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var lookup = builder.Column(table, "Substance", CellDataType.Lookup);
        builder.Row(table, RowKey, 1);

        lookup.SetLookup(LiveRegistry);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = "CO2";

        // Карта воркбука вказує на СТАРИЙ довідник; обидва мають запис «CO2»,
        // але з різними ідентифікаторами — саме так тиха підміна і виглядає.
        var block = Block([new ExcelColumnRef(lookup.Id, "Substance", 1, false, StaleRegistry)]);

        var lookups = new Dictionary<int, IReadOnlyDictionary<string, long>>
        {
            [LiveRegistry] = new Dictionary<string, long>(StringComparer.Ordinal) { ["CO2"] = 5001 },
            [StaleRegistry] = new Dictionary<string, long>(StringComparer.Ordinal) { ["CO2"] = 9001 },
        };

        var diff = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            NoDecisions, lookups, RowIds, Versions, []);

        var change = Assert.Single(diff.Changes);

        // 5001 — запис ЖИВОГО довідника. 9001 був би записом чужого.
        Assert.Equal(5001L, change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Незмінна_Unit_комірка_не_позначається_зміненою()
    {
        // ⛔ Аудит §8.3. Unit-значення живе в `ValueUnitId` — числом, — але
        // `Read`/`Same` для Unit провалювалися в дефолтну гілку, що працює через
        // `ValueString`: для Unit-комірки він ЗАВЖДИ `null`, тож `Same()`
        // повертав `false` для будь-якої непорожньої Unit-комірки. Кожна
        // Unit-комірка позначалася зміненою навіть при повторному імпорті
        // НЕЗМІННОГО експорту — прев'ю засмічувалося, і довіряти йому ставало
        // неможливо.
        const int Tonne = 8;

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var unit = builder.Column(table, "Unit", CellDataType.Unit);
        builder.Row(table, RowKey, 1);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = Tonne;

        var block = Block([new ExcelColumnRef(unit.Id, "Unit", 1, false, null)]);

        var existing = new[]
        {
            new CellRecord(
                new CellAddress(Period, RowId, unit.Id), table.Id,
                new CellValueData { ValueUnitId = Tonne }),
        };

        var unchanged = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            NoDecisions, NoLookups, RowIds, Versions, existing);

        Assert.Empty(unchanged.Changes);
        Assert.Empty(unchanged.Rejected);

        // А СПРАВЖНЯ зміна одиниці й далі видна — інакше фікс просто
        // приглушив би прев'ю замість того, щоб зробити його правдивим.
        worksheet.Cell(2, 1).Value = 1;

        var changed = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            NoDecisions, NoLookups, RowIds, Versions, existing);

        var change = Assert.Single(changed.Changes);
        Assert.Equal(1, change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дата_текстом_у_природному_порядку_не_переставляє_день_і_місяць()
    {
        // ⛔ Аудит §8.2. `DateTime.TryParse("1.4.2024", InvariantCulture, …)`
        // повертає 4 СІЧНЯ, а не 1 квітня: InvariantCulture читає `M.d.yyyy`.
        // Комірка тут — ТЕКСТОВА (саме так виглядає вставлена або вручну
        // введена дата в не-Excel-нативну Date-колонку), тож
        // `cell.TryGetValue(out DateTime)` не спрацьовує і все вирішує розбір
        // рядка.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var measuredOn = builder.Column(table, "MeasuredOn", CellDataType.Date);
        builder.Row(table, RowKey, 1);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).SetValue("1.4.2024");

        var block = Block([new ExcelColumnRef(measuredOn.Id, "MeasuredOn", 1, false, null)]);

        var diff = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            NoDecisions, NoLookups, RowIds, Versions, []);

        var change = Assert.Single(diff.Changes);
        var parsed = Assert.IsType<DateTime>(change.NewValue);

        Assert.Equal(4, parsed.Month);
        Assert.Equal(1, parsed.Day);
    }

    private static ExcelTableBlock Block(IReadOnlyList<ExcelColumnRef> columns)
        => new(
            TableInstance, TableDefId: 0, "T0", "S0", HeaderRow: 1,
            Columns: columns,
            Rows: [new ExcelRowRef(RowKey, 2)]);

    private static Dictionary<CellAddress, EditDecision> NoDecisions => [];

    private static Dictionary<int, IReadOnlyDictionary<string, long>> NoLookups => [];

    private static Dictionary<string, long> RowIds
        => new(StringComparer.Ordinal) { [RowKey] = RowId };

    private static Dictionary<string, string> Versions
        => new(StringComparer.Ordinal) { [RowKey] = "0x0A" };
}
