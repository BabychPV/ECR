// tests/Ecr.Infrastructure.Tests/Persistence/CellValueScale16Tests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>D-148</c>: шістнадцятий знак доживає від <see cref="NormalizedCellStore"/>
/// до <c>doc.CellValue</c> і назад.
/// </summary>
/// <remarks>
/// ⛔ Три місця мусять нести ОДИН масштаб, і жодне з них не падає, коли
/// розійдеться: стовпець (міграція <c>D148CellValueScale16</c>), табличний тип
/// <c>doc.CellValueTvp</c> (<c>15-cell-tvp.sql</c>) і <c>SqlMetaData</c> в
/// <see cref="NormalizedCellStore"/>. Вужче з них просто ОКРУГЛЮЄ — СУБД на
/// сервері, <c>SqlMetaData.Adjust</c> ще на клієнті, — тож «16 знаків
/// загубилися» виглядає як успішний запис. Саме тому доказом тут є ЧИСЛО
/// після кола через базу, а не збіг оголошених типів.
///
/// ⚠ Твердження про форму типу йде ПОРУЧ, а не замість: без нього падіння
/// кола не каже, ЯКА з трьох ланок звузилась, і пошук починається з нуля.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellValueScale16Tests(SqlServerFixture sql)
{
    /// <summary>
    /// Шістнадцять значущих знаків після коми, усі різні й жоден не нуль:
    /// обрізання на будь-якому знаку змінює РЯДОК, а не лише масштаб.
    /// </summary>
    private const string SixteenDigits = "0.1234567890123456";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Шістнадцять_знаків_доживають_через_TVP_до_бази_і_назад()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var store = new NormalizedCellStore(builder.CreateContext());
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var value = decimal.Parse(SixteenDigits, CultureInfo.InvariantCulture);

        await store.ApplyAsync(
            new CellChangeSet(
                doc.TableInstanceId,
                [new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = value })],
                [],
                [doc.RowIds[0]],
                ChangedByUserId: 1,
                IsLateEdit: false),
            CancellationToken.None);

        var read = await store.ReadCellsAsync([address], CancellationToken.None);
        var stored = read[address].ValueNumeric;

        Assert.NotNull(stored);

        // ⛔ Порівняння РЯДКІВ, а не decimal. `decimal` рівний незалежно від
        // масштабу: 0.1234567890 == 0.1234567890000000, тож `Assert.Equal` на
        // числах лишився б зеленим і на обрізаному значенні, якби воно
        // випадково збіглося хвостом. Рядок показує рівно те, що в базі.
        Assert.Equal(SixteenDigits, stored!.Value.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Стовпець_і_табличний_тип_оголошені_з_масштабом_16()
    {
        // Літералами, а не через константу продукту: масштаб — це вимога
        // (`D-148`), і твердження проти `NormalizedCellStore.NumericScale`
        // рухалося б разом із нею.
        Assert.Equal(
            (byte)28,
            await ScaleAsync("SELECT c.precision FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'doc.CellValue') AND c.name = N'ValueNumeric'"));

        Assert.Equal(
            (byte)16,
            await ScaleAsync("SELECT c.scale FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'doc.CellValue') AND c.name = N'ValueNumeric'"));

        // ⛔ Табличний тип у вже наявній базі не переписується сам:
        // `CREATE TYPE` стоїть під `IF TYPE_ID(...) IS NULL`, а `ALTER TYPE`
        // для табличних типів не існує. Саме тому `15-cell-tvp.sql` знімає
        // застарілий за ФОРМОЮ тип — і саме це перевіряє рядок нижче.
        Assert.Equal(
            (byte)16,
            await ScaleAsync("""
                SELECT c.scale
                FROM sys.table_types AS tt
                JOIN sys.columns AS c ON c.object_id = tt.type_table_object_id
                WHERE tt.name = N'CellValueTvp'
                  AND SCHEMA_NAME(tt.schema_id) = N'doc'
                  AND c.name = N'ValueNumeric'
                """));
    }

    private async Task<byte?> ScaleAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? null : (byte)value;
    }
}
