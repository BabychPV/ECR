// tests/Ecr.Application.Tests/Permits/PermitBindingValidatorTests.cs
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Enums;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>Перевірка прив'язки аркуша до таблиці дозволу без БД: кожне правило має тест, що падає без нього (PM-09).</summary>
public sealed class PermitBindingValidatorTests
{
    private const int DecimalUnit = 7;

    [Fact]
    public void Коректна_прив_язка_без_зауважень()
    {
        var result = Validate(Input(Key(), Limit()), Ctx());

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Без_ключа_рядка_відхиляється()
        => Refused("bindingNoRowKey", Input(Limit()), Ctx());

    [Fact]
    public void Два_ліміти_відхиляються_а_два_ключі_ні()
    {
        Refused("bindingManyLimits", Input(Key(), Limit(), Limit("Limit2", 13)), Ctx());

        var twoKeys = Validate(
            Input(Key(), new BindingColumnInput(PermitColumnRole.RowKey, "Unit", 14)),
            Ctx(extraColumns: [new SheetColumnInfo(14, "UnitCol", CellDataType.String, null, null, null, true, null)],
                extraFields: [new PermitFieldInfo("Unit", CellDataType.String, null, null, null, null)]));
        Assert.True(twoKeys.IsValid);
    }

    [Fact]
    public void Інфо_і_ліміт_мають_бути_лише_для_читання_а_ключ_ні()
    {
        var editable = new SheetColumnInfo(12, "LimitCol", CellDataType.Decimal, DecimalUnit, 12, 3, false, null);
        Refused("bindingNotReadOnly", Input(Key(), Limit()), Ctx(limitColumn: editable));

        var editableKey = new SheetColumnInfo(11, "KeyCol", CellDataType.String, null, null, null, false, null);
        Assert.True(Validate(Input(Key(), Limit()), Ctx(keyColumn: editableKey)).IsValid);
    }

    [Theory]
    [InlineData(CellDataType.String)]
    [InlineData(CellDataType.Int)]
    [InlineData(CellDataType.Date)]
    public void Різний_тип_поля_й_колонки_відхиляється(CellDataType columnType)
    {
        var column = new SheetColumnInfo(12, "LimitCol", columnType, DecimalUnit, 12, 3, true, null);
        Refused("bindingTypeMismatch", Input(Key(), Limit()), Ctx(limitColumn: column));
    }

    [Fact]
    public void Різна_одиниця_відхиляється_а_однакова_і_порожня_проходять()
    {
        var other = new SheetColumnInfo(12, "LimitCol", CellDataType.Decimal, DecimalUnit + 1, 12, 3, true, null);
        Refused("bindingUnitMismatch", Input(Key(), Limit()), Ctx(limitColumn: other));

        var noUnit = new SheetColumnInfo(12, "LimitCol", CellDataType.Decimal, null, 12, 3, true, null);
        Refused("bindingUnitMismatch", Input(Key(), Limit()), Ctx(limitColumn: noUnit));

        var bothEmpty = Ctx(
            limitColumn: noUnit,
            limitField: new PermitFieldInfo("Limit", CellDataType.Decimal, null, 12, 3, null));
        Assert.True(Validate(Input(Key(), Limit()), bothEmpty).IsValid);
    }

    [Fact]
    public void Різна_точність_чи_масштаб_відхиляється()
    {
        var precision = new SheetColumnInfo(12, "LimitCol", CellDataType.Decimal, DecimalUnit, 14, 3, true, null);
        Refused("bindingPrecisionMismatch", Input(Key(), Limit()), Ctx(limitColumn: precision));

        var scale = new SheetColumnInfo(12, "LimitCol", CellDataType.Decimal, DecimalUnit, 12, 4, true, null);
        Refused("bindingPrecisionMismatch", Input(Key(), Limit()), Ctx(limitColumn: scale));
    }

    [Fact]
    public void Посилання_вимагає_того_самого_довідника()
    {
        var field = new PermitFieldInfo("Substance", CellDataType.Lookup, null, null, null, 5);
        var same = new SheetColumnInfo(11, "KeyCol", CellDataType.Lookup, null, null, null, true, 5);
        var other = same with { LookupRegistryDefId = 6 };

        Assert.True(Validate(Input(Key("Substance"), Limit()), Ctx(keyColumn: same, keyField: field)).IsValid);
        Refused("bindingLookupMismatch", Input(Key("Substance"), Limit()), Ctx(keyColumn: other, keyField: field));
    }

    /// <summary>
    /// Permits PRMG, обмеження v1: колонка-ГРУПА (ціль - прихований реєстр <c>PRMG_*</c>) не прив'язується до поля дозволу
    /// <c>PRM_&lt;TYPE&gt;</c> - цілі різні, відмова <c>bindingLookupMismatch</c>, як і для будь-якого іншого довідника.
    /// Мутація: зарахувати склад групи в порівнянні цілей - тест червоний.
    /// </summary>
    [Fact]
    public void Колонка_група_не_прив_язується_до_поля_дозволу_типу()
    {
        const int GroupRegistry = 900;
        var field = new PermitFieldInfo("Substance", CellDataType.Lookup, null, null, null, 5);
        var groupColumn = new SheetColumnInfo(11, "KeyCol", CellDataType.Lookup, null, null, null, true, GroupRegistry);

        var result = Validate(Input(Key("Substance"), Limit()), Ctx(keyColumn: groupColumn, keyField: field));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Case == "bindingLookupMismatch");
    }

    [Fact]
    public void Невідомі_колонка_і_поле_відхиляються()
    {
        Refused("bindingColumnNotInSheet", Input(Key(), Limit(column: 99)), Ctx());
        Refused("bindingPermitFieldUnknown", Input(Key(), Limit(field: "Nope")), Ctx());
    }

    [Fact]
    public void Повтори_поля_й_колонки_відхиляються()
    {
        Refused("bindingDuplicateField", Input(Key(), new BindingColumnInput(PermitColumnRole.Info, "Substance", 12)), Ctx());
        Refused("bindingDuplicateColumn", Input(Key(), new BindingColumnInput(PermitColumnRole.Info, "Limit", 11)), Ctx());
    }

    [Fact]
    public void Таблиці_немає_у_версії_або_тип_не_опублікований()
    {
        Refused("bindingSheetNotInVersion", Input(Key()), Ctx() with { Sheet = null });
        Refused("bindingTypeNotPublished", Input(Key(), Limit()), Ctx() with { TypePublished = false });
    }

    [Fact]
    public void Режим_рядків_вимагає_динамічних_рядків_і_стелі_що_вміщує_дозвіл()
    {
        Refused("bindingRowsNeedDynamic", Input(Key(), Limit()), Ctx(rowMode: TableRowMode.Fixed));
        Assert.True(Validate(Input(Key(), Limit()), Ctx(rowMode: TableRowMode.Mixed, max: 10, rows: 10)).IsValid);
        Refused("bindingTooManyRows", Input(Key(), Limit()), Ctx(max: 10, rows: 11));

        var unknown = Validate(Input(Key(), Limit()), Ctx(max: 10, rows: null));
        Assert.True(unknown.IsValid);
        Assert.Single(unknown.Warnings);

        Assert.True(Validate(Input(Key(), Limit()), Ctx(max: null, rows: 500)).IsValid);
    }

    [Fact]
    public void Правило_з_шапки_вимагає_поля_посилання()
    {
        var lookupHeader = new HeaderFieldInfo(40, "Permit", CellDataType.Lookup, 9);
        var textHeader = new HeaderFieldInfo(41, "Note", CellDataType.String, null);

        Assert.True(Validate(FromHeader(40), Ctx(headers: [lookupHeader])).IsValid);
        Refused("bindingHeaderFieldNotLookup", FromHeader(41), Ctx(headers: [textHeader]));
        Refused("bindingHeaderFieldUnknown", FromHeader(77), Ctx(headers: [lookupHeader]));
        Refused("bindingHeaderFieldUnknown", FromHeader(null), Ctx(headers: [lookupHeader]));
    }

    [Fact]
    public void Правило_за_об_єктом_вимагає_поля_Facility_і_забороняє_поле_шапки()
    {
        Refused("bindingFacilityFieldMissing", Input(Key(), Limit()), Ctx(headers: []));
        Refused("bindingFacilityFieldType", Input(Key(), Limit()), Ctx(headers: [new HeaderFieldInfo(1, "Facility", CellDataType.Date, null)]));
        Assert.True(Validate(Input(Key(), Limit()), Ctx(headers: [new HeaderFieldInfo(1, "facility", CellDataType.Lookup, 3)])).IsValid);
        Refused("bindingHeaderFieldUnexpected", Input(Key(), Limit()) with { HeaderFieldDefId = 1 }, Ctx());
    }

    [Fact]
    public void Режим_формул_не_приймає_колонок_і_не_перевіряє_рядків()
    {
        var formulas = Input() with { Mode = PermitBindingMode.Formulas };
        Assert.True(Validate(formulas, Ctx(rowMode: TableRowMode.Fixed)).IsValid);

        Refused("bindingFormulasHaveColumns", formulas with { Columns = [Key()] }, Ctx());
    }

    private static PermitBindingValidation Validate(PermitBindingInput input, PermitBindingContext context)
        => PermitBindingValidator.Validate(input, context);

    private static void Refused(string expectedCase, PermitBindingInput input, PermitBindingContext context)
    {
        var result = Validate(input, context);
        Assert.Contains(result.Errors, e => e.Case == expectedCase);
        Assert.All(result.Errors, e => Assert.StartsWith("err.ECR-PRM-0422.binding", e.MessageKey, StringComparison.Ordinal));
    }

    private static PermitBindingInput FromHeader(int? headerId) => new(
        10, PermitBindingMode.Rows, PermitSelectionRule.FromHeaderLookup, headerId, [Key(), Limit()]);

    private static PermitBindingInput Input(params BindingColumnInput[] columns) => new(
        10, PermitBindingMode.Rows, PermitSelectionRule.ByFacilityPeriodEnd, null, columns);

    private static BindingColumnInput Key(string field = "Substance") => new(PermitColumnRole.RowKey, field, 11);

    private static BindingColumnInput Limit(string field = "Limit", int column = 12) => new(PermitColumnRole.Limit, field, column);

    private static PermitBindingContext Ctx(
        TableRowMode rowMode = TableRowMode.Dynamic,
        int? max = null,
        int? rows = 3,
        SheetColumnInfo? keyColumn = null,
        SheetColumnInfo? limitColumn = null,
        PermitFieldInfo? keyField = null,
        PermitFieldInfo? limitField = null,
        IReadOnlyList<HeaderFieldInfo>? headers = null,
        IReadOnlyList<SheetColumnInfo>? extraColumns = null,
        IReadOnlyList<PermitFieldInfo>? extraFields = null)
    {
        var columns = new List<SheetColumnInfo>
        {
            keyColumn ?? new SheetColumnInfo(11, "KeyCol", CellDataType.String, null, null, null, true, null),
            limitColumn ?? new SheetColumnInfo(12, "LimitCol", CellDataType.Decimal, DecimalUnit, 12, 3, true, null),
            new SheetColumnInfo(13, "Limit2", CellDataType.Decimal, DecimalUnit, 12, 3, true, null),
        };
        columns.AddRange(extraColumns ?? []);

        var fields = new List<PermitFieldInfo>
        {
            keyField ?? new PermitFieldInfo("Substance", CellDataType.String, null, null, null, null),
            limitField ?? new PermitFieldInfo("Limit", CellDataType.Decimal, DecimalUnit, 12, 3, null),
            new PermitFieldInfo("Limit2", CellDataType.Decimal, DecimalUnit, 12, 3, null),
        };
        fields.AddRange(extraFields ?? []);

        return new PermitBindingContext(
            new SheetTableInfo(10, "Emissions", rowMode, max, columns),
            headers ?? [new HeaderFieldInfo(1, "Facility", CellDataType.String, null)],
            true,
            fields,
            rows);
    }
}
