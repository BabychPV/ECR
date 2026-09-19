using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>BE-06</c>, стандарт доказу: скільки коштує вікно
/// <c>ChangedAt &gt;= @since</c> у запиті «остання зміна комірки».
/// </summary>
/// <remarks>
/// ⛔ Міряється БОЙОВИЙ текст — <see cref="AuditReader.LastChangesSql"/>, той
/// самий, який виконує продуктивний шлях; «без вікна» будується ВІДНІМАННЯМ
/// рядка з нього, і сам факт віднімання перевіряється
/// (<see cref="WithoutWindow"/>). Копія запиту в тесті розійшлася б з
/// оригіналом мовчки, і замір лишився б зеленим, доводячи властивість запиту,
/// якого більше немає (урок <c>PartitionScanTests</c>).
///
/// ⛔ <b>Замір виправив припущення директиви, і це названо, а не сховано.</b>
/// №15 каже про читання журналу без вікна просто «інакше — всі партиції». Це
/// правда НЕ ЗАВЖДИ, і перша редакція цього тесту впала саме на цьому:
/// <c>TOP (1) … ORDER BY ChangedAt DESC</c> по вирівняному індексу СУБД виконує
/// як зворотний упорядкований прохід партиціями й зупиняється на першому
/// знайденому рядку. Для комірки, яку щойно міняли, це 2 заходи — і з вікном,
/// і без (заміряно, числа в <c>output</c>). Ціна без вікна з'являється там, де
/// шукати НЕМА ЧОГО: щоб відповісти «змін немає», прохід мусить дійти до
/// найстарішої партиції журналу — і в продуктиві це роки історії плюс архів.
///
/// ⚠ Саме цей випадок — типовий для конфлікту, а не рідкісний: комірку, яку
/// востаннє чіпали давно (або не чіпали в журналі зовсім), батч зачіпає так
/// само часто, як свіжу, і платить за неї на КОЖНУ адресу в <c>CROSS APPLY</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditLastChangePartitionScanTests(SqlServerFixture sql, ITestOutputHelper output)
{
    /// <summary>Місяці історії-шуму. Усі — у межах <c>pf_AuditByMonth</c>.</summary>
    private static readonly DateTime[] NoiseMonths =
    [
        new(2026, 1, 12, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 2, 12, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 3, 12, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 4, 12, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc),
    ];

    /// <summary>Скільки записів журналу класти в кожен «шумний» місяць.</summary>
    /// <remarks>
    /// ⚠ Число підібране під диск і час, а не під красу: сенс шуму — щоб у
    /// партиціях БУЛО що читати (той самий урок «operating system error 112»,
    /// що в <c>PartitionScanTests</c>).
    /// </remarks>
    private const int NoiseRowsPerMonth = 200;

    /// <summary>Місяць свіжої зміни.</summary>
    private static readonly DateTime Recent = new(2027, 5, 14, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>Початок вікна: місяць свіжої зміни й далі.</summary>
    private static readonly DateTime WindowStart = new(2027, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-06")]
    public async Task Вікно_часу_знімає_прохід_по_всій_історії_журналу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        // Комірка, яку востаннє чіпали ДАВНО: історія є, у вікні — нічого.
        var staleCell = doc.RowIds[0];

        // Комірка зі свіжою зміною — для порівняння.
        var freshCell = doc.RowIds[1];

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);

        foreach (var month in NoiseMonths)
        {
            await writer.WriteCellChangesAsync(
                [.. Enumerable.Range(0, NoiseRowsPerMonth)
                    .SelectMany(i => new[]
                    {
                        Change(doc, staleCell, month.AddMinutes(i), "old"),
                        Change(doc, freshCell, month.AddMinutes(i), "old"),
                    })],
                CancellationToken.None);
        }

        await writer.WriteCellChangesAsync(
            [Change(doc, freshCell, Recent, "12.40")], CancellationToken.None);

        var filled = await FilledPartitionsAsync(doc);

        // ⛔ Перша перевірка — про СЕРЕДОВИЩЕ. На одній заповненій партиції
        // «прохід по всіх» і «прохід по одній» дають однакові числа, і зелений
        // замір означав би порожнечу, а не роботу.
        Assert.True(
            filled >= 3,
            $"Партицій aud.CellChange із рядками цього документа: {filled.ToString(CultureInfo.InvariantCulture)}. "
            + "Стандарт доказу вимагає щонайменше трьох — інакше замір ні про що.");

        var staleWith = await MeasureAsync(doc, staleCell, AuditReader.LastChangesSql(1));
        var staleWithout = await MeasureAsync(doc, staleCell, WithoutWindow(AuditReader.LastChangesSql(1)));
        var freshWith = await MeasureAsync(doc, freshCell, AuditReader.LastChangesSql(1));
        var freshWithout = await MeasureAsync(doc, freshCell, WithoutWindow(AuditReader.LastChangesSql(1)));

        var report =
            $"партицій із даними {filled}; "
            + $"давня комірка: з вікном — scan {staleWith.ScanCount}, читань {staleWith.LogicalReads}, "
            + $"рядків {staleWith.Rows}; без вікна — scan {staleWithout.ScanCount}, "
            + $"читань {staleWithout.LogicalReads}, рядків {staleWithout.Rows}; "
            + $"свіжа комірка: з вікном — scan {freshWith.ScanCount}, читань {freshWith.LogicalReads}; "
            + $"без вікна — scan {freshWithout.ScanCount}, читань {freshWithout.LogicalReads}";

        output.WriteLine(report);

        // ⛔ ГОЛОВНЕ. Комірка без змін у вікні: форма без вікна мусить дійти до
        // найстарішої партиції, щоб мати право сказати «немає», — і платить за
        // кожну. Форма з вікном зупиняється на його межі.
        Assert.True(
            staleWithout.ScanCount > staleWith.ScanCount,
            "Вікно не зменшило кількість заходів у таблицю: " + report);

        Assert.True(
            staleWithout.LogicalReads > staleWith.LogicalReads,
            "Вікно не зменшило логічних читань: " + report);

        // ⚠ Форма без вікна заходить у таблицю щонайменше стільки разів,
        // скільки партицій заповнено: це і є «по одному заходу на партицію».
        Assert.True(
            staleWithout.ScanCount >= filled,
            "Форма без вікна не обійшла всі заповнені партиції — замір втратив предмет: " + report);

        // ⚠ І різні набори рядків тут — не вада заміру, а САМ ПРЕДМЕТ вікна:
        // форма з вікном чесно каже «у вікні змін немає» (0 рядків), форма без
        // вікна дістає зміну піврічної давності, яку користувачеві в діалозі
        // конфлікту показувати нема сенсу — і платить за неї повним проходом.
        Assert.Equal(0, staleWith.Rows);
        Assert.Equal(1, staleWithout.Rows);

        // ⚠ Свіжа комірка: вікно НЕ дає виграшу, бо зворотний прохід і так
        // зупиняється на першому рядку. Тут перевіряється лише те, що гірше не
        // стало, — і це названо вголос, а не видано за перемогу.
        Assert.True(
            freshWith.ScanCount <= freshWithout.ScanCount,
            "Вікно зробило свіжу комірку дорожчою: " + report);

        Assert.Equal(1, freshWith.Rows);
    }

    /// <summary>Той самий текст без предиката вікна.</summary>
    /// <param name="sqlText">Бойовий текст запиту.</param>
    /// <exception cref="InvalidOperationException">
    /// Рядка з предикатом у тексті немає — отже міряти нема чого.
    /// </exception>
    private static string WithoutWindow(string sqlText)
    {
        const string predicate = "AND a.ChangedAt >= @since";

        if (!sqlText.Contains(predicate, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "У бойовому тексті немає предиката вікна — або його прибрали з `AuditReader`, "
                + "або змінили написання. Замір без цього рядка порівнював би запит сам із собою.");
        }

        return sqlText.Replace(predicate, string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Скільки партицій <c>aud.CellChange</c> містять рядки цього документа.</summary>
    private async Task<int> FilledPartitionsAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM (SELECT DISTINCT $PARTITION.pf_AuditByMonth(ChangedAt) AS p "
            + "FROM aud.CellChange WHERE DocumentId = @documentId) AS d;";
        command.Parameters.AddWithValue("@documentId", doc.DocumentId);

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    /// <summary>Виконує текст із увімкненим <c>SET STATISTICS IO</c> і знімає числа.</summary>
    /// <remarks>
    /// ⛔ <c>ExecuteNonQuery</c>, а не читач: повідомлення <c>STATISTICS IO</c>
    /// приходять услід за набором рядків і з асинхронним <c>SqlDataReader</c> до
    /// <c>InfoMessage</c> не долітають узагалі (перевірено в
    /// <c>PartitionScanTests</c>). Рядки рахуються окремим прогоном до заміру.
    /// </remarks>
    private async Task<IoStats> MeasureAsync(TestDocument doc, long tableRowId, string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        var messages = new List<string>();
        void Handler(object sender, SqlInfoMessageEventArgs args) => messages.Add(args.Message);

        connection.InfoMessage += Handler;

        int rows;
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "SET STATISTICS IO ON;";
                await setup.ExecuteNonQueryAsync(CancellationToken.None);
            }

            rows = await CountAsync(connection, doc, tableRowId, text);
            messages.Clear();

            await using var command = connection.CreateCommand();
            Bind(command, doc, tableRowId, text);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            connection.InfoMessage -= Handler;
        }

        return Parse(messages, rows);
    }

    private static async Task<int> CountAsync(
        SqlConnection connection, TestDocument doc, long tableRowId, string text)
    {
        await using var command = connection.CreateCommand();
        Bind(command, doc, tableRowId, text);

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows++;
        }

        return rows;
    }

    /// <summary>Підставляє в текст ті самі параметри, що й бойовий читач.</summary>
    private static void Bind(SqlCommand command, TestDocument doc, long tableRowId, string text)
    {
        command.CommandText = text;
        command.Parameters.Add("@r0", System.Data.SqlDbType.BigInt).Value = tableRowId;
        command.Parameters.Add("@c0", System.Data.SqlDbType.Int).Value = doc.ColumnDefIds[1];
        command.Parameters.Add("@documentId", System.Data.SqlDbType.BigInt).Value = doc.DocumentId;

        var since = command.Parameters.Add("@since", System.Data.SqlDbType.DateTime2);
        since.Scale = 3;
        since.Value = WindowStart;
    }

    /// <summary>Запис журналу про названу комірку.</summary>
    private static CellChangeRecord Change(TestDocument doc, long tableRowId, DateTime at, string newValue)
        => new(
            at,
            new CellAddress(doc.PeriodKey, tableRowId, doc.ColumnDefIds[1]),
            doc.DocumentId,
            "R1",
            OldValue: null,
            NewValue: newValue,
            ChangedByUserId: 1,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null);

    /// <summary>Розбирає рядок <c>STATISTICS IO</c> про <c>aud.CellChange</c>.</summary>
    /// <remarks>
    /// ⚠ Нерозпізнане повідомлення — падіння з ПОВНИМ текстом, а не нулі: нулі
    /// пройшли б будь-яку межу нижче й оголосили б перемогу тим голосніше, чим
    /// гірше працював розбір.
    /// </remarks>
    private static IoStats Parse(IReadOnlyList<string> messages, int rows)
    {
        foreach (var message in messages)
        {
            var match = Regex.Match(
                message,
                @"Table\s+'CellChange'\.\s+Scan count\s+(\d+),\s+logical reads\s+(\d+)",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));

            if (match.Success)
            {
                return new IoStats(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    rows);
            }
        }

        throw new InvalidOperationException(
            "У виводі SET STATISTICS IO немає рядка про aud.CellChange. Отримано:"
            + Environment.NewLine + string.Join(Environment.NewLine, messages));
    }

    /// <summary>Числа <c>STATISTICS IO</c> одного запиту.</summary>
    /// <param name="ScanCount">Скільки разів СУБД зайшла в таблицю — по одному на партицію.</param>
    /// <param name="LogicalReads">Логічні читання сторінок.</param>
    /// <param name="Rows">Скільки рядків віддав запит.</param>
    private sealed record IoStats(int ScanCount, int LogicalReads, int Rows);
}
