using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>WR-05</c>, стандарт доказу: <c>SET STATISTICS IO</c> на базі з ≥ 3
/// заповненими партиціями.
/// </summary>
/// <remarks>
/// ⛔ Сторож <c>PartitionKeyQueryTests</c> доводить лише те, що предикат
/// потрапив у SQL. Що він при цьому ЩОСЬ ДАЄ — доводить тільки замір, і тільки
/// на кількох заповнених партиціях: на одній партиції «скан усіх партицій» і
/// «засічка потрібної» дають однакове число, і зелений замір означав би
/// порожнечу, а не роботу.
///
/// ⚠ Порівнюються дві ФОРМИ одного запиту на ОДНИХ і тих самих даних, в одній
/// сесії, підряд: фільтр за <c>TableInstanceId</c> без <c>PeriodKey</c> (як
/// було в <c>AccessDecisionService.CanEditSliceAsync:273-278</c>) і з ним (як
/// стало). Обидві віддають один і той самий набір рядків — це теж
/// перевіряється, інакше «менше читань» могло б означати просто «менше
/// роботи».
///
/// ⚠ Періоди обрані так, щоб не перетнутися з <c>ArchiveJobTests</c>
/// (<c>202703</c>, <c>202704</c>) і <c>PeriodAccessSliceTests</c>
/// (<c>202609</c>): звільнення партиції йде ПО ПЕРІОДУ і про чужі проєкти не
/// знає.
/// </remarks>
[Collection("SqlServer")]
public sealed class PartitionScanTests(SqlServerFixture sql, ITestOutputHelper output)
{
    /// <summary>Періоди, у яких створюються дані. Рівно три — мінімум стандарту доказу.</summary>
    private static readonly int[] Periods = [202706, 202707, 202708];

    /// <summary>Рядків у СУСІДНІХ партиціях — тих, які запит без ключа читає дарма.</summary>
    /// <remarks>
    /// ⛔ Перша редакція клала однаково по 300 рядків у кожну з трьох партицій
    /// і давала 18 читань проти 6 — утричі, не на порядок. Це не слабкість
    /// виправлення, а хиба заміру: коли ⅓ прочитаного все одно потрібна
    /// відповіді, більш ніж утричі впасти НЕМА КУДИ. У продуктиві пропорція
    /// зовсім інша — один екземпляр таблиці проти двох років історії, — і саме
    /// вона тут і відтворюється: сусідні періоди важкі, цільовий легкий.
    ///
    /// ⚠ Числа підібрані під диск, а не під красу: ~4 тисячі рядків — це
    /// секунди вставки й мегабайти, тоді як «зробити по-справжньому велику
    /// базу» вже одного разу зупинило роботу помилкою «operating system
    /// error 112».
    /// </remarks>
    private const int RowsInNeighbourPeriod = 2000;

    /// <summary>Рядків у цільовій партиції — стільки, скільки віддасть запит.</summary>
    private const int RowsInTargetPeriod = 50;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PeriodKey_у_предикаті_знімає_скан_усіх_партицій()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var documents = new List<TestDocument>(Periods.Length);

        for (var i = 0; i < Periods.Length; i++)
        {
            documents.Add(await builder.BuildAsync(
                periodKey: Periods[i],
                columnCount: 3,
                rowCount: i == 1 ? RowsInTargetPeriod : RowsInNeighbourPeriod,
                ct: CancellationToken.None));
        }

        var filled = await FilledPartitionsAsync();

        // ⛔ Перша перевірка — про СЕРЕДОВИЩЕ, і вона перша не випадково.
        // Замір на одній заповненій партиції дав би «однакові числа» і
        // читався б як «виправлення нічого не дає», хоча насправді доказ
        // просто не побудували.
        Assert.True(
            filled >= 3,
            $"Партицій doc.TableRow із даними: {filled.ToString(CultureInfo.InvariantCulture)}. "
            + "Стандарт доказу WR-05 вимагає щонайменше трьох — інакше замір ні про що.");

        // Міряється СЕРЕДНІЙ документ — легкий, між двома важкими. У крайнього
        // партиція межова, і скан міг би зупинитися на ній раніше з причин, що
        // не стосуються предиката.
        var target = documents[1];

        var withoutKey = await MeasureAsync(
            builder,
            db => db.TableRows
                .AsNoTracking()
                .Where(r => r.TableInstanceId == target.TableInstanceId && !r.IsDeleted));

        var withKey = await MeasureAsync(
            builder,
            db => AccessDecisionService.SliceRowsQuery(db, target.TableInstanceId, target.PeriodKey));

        output.WriteLine(
            $"без PeriodKey: scan count {withoutKey.ScanCount}, логічних читань {withoutKey.LogicalReads}");
        output.WriteLine(
            $"з  PeriodKey: scan count {withKey.ScanCount}, логічних читань {withKey.LogicalReads}");
        output.WriteLine($"заповнених партицій doc.TableRow: {filled}");

        var report = $"партицій із даними {filled}; "
                     + $"без ключа — scan {withoutKey.ScanCount}, читань {withoutKey.LogicalReads}; "
                     + $"із ключем — scan {withKey.ScanCount}, читань {withKey.LogicalReads}";

        // Обидві форми бачать ОДНІ дані. Без цього менше читань могло б
        // означати, що другий запит просто не знайшов рядків.
        Assert.Equal(RowsInTargetPeriod, withoutKey.Rows);
        Assert.Equal(RowsInTargetPeriod, withKey.Rows);

        // Форма без ключа читає кілька партицій, форма з ключем — рівно одну.
        Assert.True(
            withoutKey.ScanCount >= filled,
            "Запит без PeriodKey мав просканувати всі партиції: " + report);
        Assert.Equal(1, withKey.ScanCount);

        // Стандарт доказу директиви: падіння НА ПОРЯДОК.
        Assert.True(
            withKey.LogicalReads * 10 <= withoutKey.LogicalReads,
            "Логічні читання не впали на порядок: " + report);
    }

    /// <summary>Скільки партицій <c>doc.TableRow</c> містять рядки.</summary>
    /// <remarks>
    /// ⚠ Рахується по <c>$PARTITION</c>, а не по <c>DISTINCT PeriodKey</c>:
    /// доказ WR-05 про ПАРТИЦІЇ, і збіг «один період = одна партиція» тримає
    /// <c>02-partitions.sql</c>, а не цей тест. Якщо межі колись зміняться,
    /// краще, щоб число говорило правду про фізику.
    /// </remarks>
    private async Task<int> FilledPartitionsAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM (SELECT DISTINCT $PARTITION.pf_ByPeriodKey(PeriodKey) AS p "
            + "FROM doc.TableRow) AS d;";

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    /// <summary>Виконує запит із увімкненим <c>SET STATISTICS IO</c> і знімає числа.</summary>
    /// <param name="builder">Будівник — джерело контексту на ту саму базу.</param>
    /// <param name="query">Форма запиту, яку міряємо.</param>
    /// <remarks>
    /// ⛔ Міряється <b>текст</b>, який віддає EF (<c>ToQueryString()</c>), а
    /// виконується він на окремому з'єднанні ADO.NET. Перша редакція ганяла сам
    /// запит EF на з'єднанні, відкритому зовні, і <c>SET STATISTICS IO ON</c>
    /// на нього не діяв: подій <c>InfoMessage</c> не приходило ЖОДНОЇ. Це той
    /// самий клас пастки, від якого застерігає решта коментарів тут — нуль
    /// повідомлень розібрався б у «нуль читань», а нуль читань пройшов би
    /// будь-яку межу нижче й оголосив би перемогу. Саме тому
    /// <see cref="Parse"/> на порожньому виводі ПАДАЄ.
    ///
    /// ⚠ Текст лишається бойовим: <c>ToQueryString()</c> — це рівно те, що EF
    /// відправив би в СУБД, разом із <c>DECLARE</c> параметрів.
    ///
    /// ⚠ Логічні читання, на відміну від часу, не залежать ні від кешу, ні від
    /// того, що ще робить машина. Саме тому стандарт доказу — вони, а не
    /// мілісекунди.
    /// </remarks>
    private async Task<IoStats> MeasureAsync(
        TestDocumentBuilder builder, Func<EcrDbContext, IQueryable<TableRow>> query)
    {
        string text;
        await using (var db = builder.CreateContext())
        {
            text = query(db).Select(r => new { r.Id, r.RowKeyValue }).ToQueryString();
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        var messages = new List<string>();
        void Handler(object sender, SqlInfoMessageEventArgs args) => messages.Add(args.Message);

        connection.InfoMessage += Handler;

        var rows = 0;
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "SET STATISTICS IO ON;";
                await setup.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // Рядки рахуються ОКРЕМИМ прогоном того самого тексту, до вмикання
            // заміру: див. примітку про `ExecuteNonQuery` у <see cref="MeasureAsync"/>.
            rows = await CountAsync(connection, text);

            messages.Clear();

            await using var command = connection.CreateCommand();
            command.CommandText = text;

            // ⛔ `ExecuteNonQuery`, а не читач. Повідомлення STATISTICS IO
            // приходять услід за набором рядків, і з асинхронним
            // `SqlDataReader` вони не долітають до `InfoMessage` взагалі —
            // перевірено тут-таки: подій було НУЛЬ, хоча той самий запит у
            // `sqlcmd` друкує рядок про `doc.TableRow`. `ExecuteNonQuery`
            // виконує той самий план і ту саму роботу вводу-виводу, просто не
            // віддає рядки клієнтові.
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            connection.InfoMessage -= Handler;
        }

        return Parse(messages, rows);
    }

    /// <summary>Скільки рядків віддає текст запиту.</summary>
    /// <param name="connection">Відкрите з'єднання.</param>
    /// <param name="text">Текст запиту з <c>ToQueryString()</c>.</param>
    /// <remarks>
    /// ⚠ Потрібно рівно для того, щоб «менше читань» не могло виявитися
    /// «нічого не знайшли». Виконується ДО заміру й у його підрахунок не
    /// входить.
    /// </remarks>
    private static async Task<int> CountAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows++;
        }

        return rows;
    }

    /// <summary>Розбирає рядок <c>STATISTICS IO</c> про <c>doc.TableRow</c>.</summary>
    /// <remarks>
    /// ⚠ Нерозпізнане повідомлення — це падіння з ПОВНИМ текстом, а не нулі.
    /// Нулі пройшли б усі межі нижче й оголосили б перемогу тим голосніше, чим
    /// гірше працював розбір.
    /// </remarks>
    private static IoStats Parse(IReadOnlyList<string> messages, int rows)
    {
        foreach (var message in messages)
        {
            var match = Regex.Match(
                message,
                @"Table\s+'TableRow'\.\s+Scan count\s+(\d+),\s+logical reads\s+(\d+)",
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
            "У виводі SET STATISTICS IO немає рядка про doc.TableRow. Отримано:"
            + Environment.NewLine + string.Join(Environment.NewLine, messages));
    }

    /// <summary>Числа <c>STATISTICS IO</c> одного запиту.</summary>
    /// <param name="ScanCount">Скільки разів СУБД пройшла таблицю — по одному на партицію.</param>
    /// <param name="LogicalReads">Логічні читання сторінок.</param>
    /// <param name="Rows">Скільки рядків віддав запит.</param>
    private sealed record IoStats(int ScanCount, int LogicalReads, int Rows);
}

