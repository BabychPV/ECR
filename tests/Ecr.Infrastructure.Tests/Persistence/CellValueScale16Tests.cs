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
///
/// ✎ 2026-09-21: ті самі три ланки перейшли з precision 28 на 34
/// (<c>D148CellValuePrecision34</c>), і клас покриває обидві половини вимоги.
/// Різниця між ними принципова, і на неї спирається форма тестів нижче:
/// замалий МАСШТАБ ріже мовчки, замала PRECISION відхиляє гучно
/// («Arithmetic overflow»). Тому масштаб доводиться порівнянням РЯДКІВ після
/// кола, а розширення — самим фактом, що запис проходить.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellValueScale16Tests(SqlServerFixture sql)
{
    /// <summary>
    /// Шістнадцять значущих знаків після коми, усі різні й жоден не нуль:
    /// обрізання на будь-якому знаку змінює РЯДОК, а не лише масштаб.
    /// </summary>
    private const string SixteenDigits = "0.1234567890123456";

    /// <summary>
    /// 278 МВт·год у базовій одиниці (джоуль) плюс шістнадцять знаків:
    /// тринадцять цілих розрядів, яких <c>decimal(28,16)</c> НЕ вміщав.
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є число, заради якого precision розширено 28 → 34. Каталог
    /// одиниць має множник <c>MWh → 3 600 000 000</c>, тож 278 МВт·год — це вже
    /// 1.0008·10¹² в базовій одиниці, а precision 28 при масштабі 16 лишає
    /// рівно 12 цілих розрядів. На <c>(28,16)</c> запис цього значення не
    /// округлюється, а ВІДХИЛЯЄТЬСЯ: «Arithmetic overflow error converting
    /// numeric to data type numeric» (перевірено запитом до СУБД).
    /// </remarks>
    private const string ThirteenIntegerDigits = "1000800000000.1234567890123456";

    /// <summary>
    /// Вісімнадцять цілих розрядів і повний масштаб — стеля САМОГО стовпця
    /// <c>decimal(34,16)</c>, тобто рівно 34 значущі цифри.
    /// </summary>
    private const string EighteenIntegerDigits = "123456789012345678.1234567890123456";

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
            (byte)34,
            await ScaleAsync("SELECT c.precision FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'doc.CellValue') AND c.name = N'ValueNumeric'"));

        Assert.Equal(
            (byte)16,
            await ScaleAsync("SELECT c.scale FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'doc.CellValue') AND c.name = N'ValueNumeric'"));

        // ⛔ Табличний тип у вже наявній базі не переписується сам:
        // `CREATE TYPE` стоїть під `IF TYPE_ID(...) IS NULL`, а `ALTER TYPE`
        // для табличних типів не існує. Саме тому `15-cell-tvp.sql` знімає
        // застарілий за ФОРМОЮ тип — і саме це перевіряє рядок нижче.
        //
        // ⚠ Precision перевіряється теж, і не для симетрії: перехід 28 → 34
        // спирається на ТОЙ САМИЙ блок зняття, а він звіряє обидва числа.
        // Тест лише на масштаб лишився б зеленим на типі, який відстав від
        // стовпця по precision, — тобто на тому, що відхиляє законні значення.
        Assert.Equal(
            (byte)16,
            await ScaleAsync(TvpFacet("scale")));

        Assert.Equal(
            (byte)34,
            await ScaleAsync(TvpFacet("precision")));
    }

    /// <summary>
    /// Тринадцять цілих розрядів доживають до бази й назад — те, чого
    /// <c>decimal(28,16)</c> не вміщав.
    /// </summary>
    /// <remarks>
    /// ⛔ Доказ РОЗШИРЕННЯ, а не масштабу: тут ламається не останній знак, а
    /// сам запис. На <c>(28,16)</c> `ApplyAsync` кидає `SqlException`
    /// «Arithmetic overflow», тобто тест червоніє гучно й одразу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Тринадцять_цілих_розрядів_із_шістнадцятьма_знаками_доживають_до_бази()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var store = new NormalizedCellStore(builder.CreateContext());
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var value = decimal.Parse(ThirteenIntegerDigits, CultureInfo.InvariantCulture);

        // ⚠ Контроль засновку: значення мусить бути ЦІЛИМ уже в CLR. 13 + 16 =
        // 29 значущих цифр — це рівно стеля `System.Decimal`, і якби воно її
        // перевищувало, `decimal.Parse` округлив би МОВЧКИ, а тест порівнював
        // би огризок сам із собою.
        Assert.Equal(ThirteenIntegerDigits, value.ToString(CultureInfo.InvariantCulture));

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

        Assert.Equal(
            ThirteenIntegerDigits,
            read[address].ValueNumeric!.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Стовпець тримає всі 18 цілих розрядів, але <c>System.Decimal</c> їх не
    /// читає — і це названо тут, а не з'ясовується вдруге.
    /// </summary>
    /// <remarks>
    /// ⛔ Межа розширення лежить НЕ в стовпці. <c>decimal(34,16)</c> приймає
    /// всі 34 значущі цифри, а <c>System.Decimal</c> несе лише 29 — тож при
    /// масштабі 16 із бази читається щонайбільше 13 цілих розрядів.
    ///
    /// ⚠ Поведінку <c>Microsoft.Data.SqlClient</c> на межі виміряно, а не
    /// припущено, і вона НЕ однакова:
    /// <list type="bullet">
    /// <item>хвіст із самих нулів (<c>…678.0000000000000000</c>) — драйвер
    /// сам ЗМЕНШУЄ масштаб і повертає точне <c>123456789012345678.0000000000</c>;</item>
    /// <item>значущий хвіст — <c>OverflowException</c> «Conversion overflows»
    /// із <c>SqlBuffer.get_Decimal</c>.</item>
    /// </list>
    /// Тобто мовчазної втрати немає в ЖОДНОМУ з випадків, і це головне. Але
    /// значення, записане повз застосунок (ETL, ручний <c>INSERT</c>, сира
    /// міграція), стає для нього нечитабельним — падінням, не нулем. Тест
    /// робить цю стелю виконуваною: інакше її знайдуть утретє, і знову на
    /// живих даних.
    ///
    /// ⚠ Сам застосунок такого значення НЕ створить: воно не існує в CLR, а
    /// <c>decimal.Parse</c> округлив би його ще на вході. Тому це межа
    /// сумісності з зовнішнім записом, а не дефект шляху запису.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Вісімнадцять_цілих_розрядів_стовпець_приймає_а_System_Decimal_не_читає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var store = new NormalizedCellStore(builder.CreateContext());
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);

        await store.ApplyAsync(
            new CellChangeSet(
                doc.TableInstanceId,
                [new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 1m })],
                [],
                [doc.RowIds[0]],
                ChangedByUserId: 1,
                IsLateEdit: false),
            CancellationToken.None);

        var where =
            $"WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {doc.RowIds[0]} "
            + $"AND ColumnDefId = {doc.ColumnDefIds[1]}";

        try
        {
            // ⚠ Запис — сирим SQL, бо через застосунок таке значення НЕ
            // проходить у принципі: `System.Decimal` його не збудує.
            await ExecuteAsync(
                $"UPDATE doc.CellValue SET ValueNumeric = {EighteenIntegerDigits} {where};");

            // Стовпець його взяв — доказ рядком, а не числом: `CONVERT` у текст
            // іде на боці СУБД і через CLR-тип не проходить узагалі.
            Assert.Equal(
                EighteenIntegerDigits,
                await ScalarAsync<string>(
                    $"SELECT CONVERT(nvarchar(50), ValueNumeric) FROM doc.CellValue {where};"));

            // А CLR його не читає — і КИДАЄ, а не ріже. Саме це твердження
            // відрізняє названу межу від тихої втрати даних.
            await Assert.ThrowsAsync<OverflowException>(
                () => ScalarAsync<decimal>($"SELECT ValueNumeric FROM doc.CellValue {where};"));
        }
        finally
        {
            // ⛔ Значення прибирається обов'язково: база тестів спільна на
            // збірку, і рядок, нечитабельний для `SqlClient`, ламав би сусідні
            // тести причиною, яка не має до них стосунку.
            await ExecuteAsync($"UPDATE doc.CellValue SET ValueNumeric = 1 {where};");
        }
    }

    /// <summary>Запит на фасет стовпця <c>ValueNumeric</c> табличного типу.</summary>
    private static string TvpFacet(string facet) => $"""
        SELECT c.{facet}
        FROM sys.table_types AS tt
        JOIN sys.columns AS c ON c.object_id = tt.type_table_object_id
        WHERE tt.name = N'CellValueTvp'
          AND SCHEMA_NAME(tt.schema_id) = N'doc'
          AND c.name = N'ValueNumeric'
        """;

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        // ⚠ Через `SqlDataReader.GetFieldValue<T>`, а не `ExecuteScalar`:
        // конверсію в CLR-тип робить саме він, і саме вона має кинути.
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false), "Запит не повернув рядка.");
        return await reader.GetFieldValueAsync<T>(0).ConfigureAwait(false);
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
