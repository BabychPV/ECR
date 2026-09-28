using System.Globalization;
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
/// Аудит `C1`, шлях API: число, що прийшло РЯДКОМ, розбирається суворо —
/// <c>NumberStyles.Float</c> під Invariant, кома не приймається взагалі.
/// </summary>
/// <remarks>
/// ⛔ Доти тут стояв <c>NumberStyles.Number</c>: його <c>AllowThousands</c>
/// викидав кожну кому, і «12,5» через PATCH лягало в базу як 125. У API
/// значення машинні (клієнт шле <c>Decimal</c> канонічним рядком —
/// <c>decimalTextOf</c>), тож кома тут — або вже відхилений клієнтом текст,
/// або помилка зовнішнього споживача. Людський текст із комою приходить лише з
/// Excel, і його правило живе в <c>ImportDiffBuilder</c>.
/// </remarks>
public sealed class CellValueReaderCommaTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, string> Rejected() => Cross("12,5", "1,234", "1,2,3,4", "1,234.5", "-12,5");

    public static TheoryData<string, string> Accepted() => Cross("1234.5", "1E-05", "-7.25", " 42 ");

    /// <summary>
    /// ⛔ Мутація: повернути <c>NumberStyles.Number</c> — «12,5» знову 125,
    /// «1,234» — 1234.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "C1")]
    [MemberData(nameof(Rejected))]
    public void Кома_в_числі_з_API_відхиляється_з_назвою_колонки(string cultureName, string text)
    {
        var error = WithCulture(cultureName, () => Assert.Throws<BusinessRuleException>(
            () => CellValueReader.Read(FromWire(text), Column())));

        Assert.Equal(CellValueReader.TypeMismatch, error.ErrorCode);
        Assert.Equal("err.ECR-CELL-0422.expectsNumber", error.Details?["messageKey"]);
        Assert.Equal("C1", error.Details?["columnCode"]);
    }

    /// <summary>Регресія: число без коми читається тим самим числом під будь-якою культурою.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "C1")]
    [MemberData(nameof(Accepted))]
    public void Число_без_коми_з_API_читається_однаково(string cultureName, string text)
    {
        var data = WithCulture(cultureName, () => CellValueReader.Read(FromWire(text), Column()));

        Assert.NotNull(data);
        Assert.Equal(
            decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            data.ValueNumeric);
    }

    private static TheoryData<string, string> Cross(params string[] texts)
    {
        var data = new TheoryData<string, string>();

        foreach (var culture in new[] { "uk-UA", "" })
        {
            foreach (var text in texts)
            {
                data.Add(culture, text);
            }
        }

        return data;
    }

    /// <summary>Значення так, як воно приходить через API: після JSON.</summary>
    private static object? FromWire(string value)
    {
        var request = new PatchCellsRequest(
            1, 202603, "UserEdit", [new PatchRow("R1", "0x01", [new PatchCell("C1", value)])]);

        var json = JsonSerializer.Serialize(request, Web);
        var wire = JsonSerializer.Deserialize<PatchCellsRequest>(json, Web)!.Rows[0].Cells[0].Value;

        Assert.IsType<JsonElement>(wire);

        return wire;
    }

    private static ColumnDef Column()
        => new(tableDefId: 1, EcrCode.Create("C1"), new LocalizedText(), ordinal: 1, CellDataType.Decimal);

    private static T WithCulture<T>(string name, Func<T> action)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);

            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
