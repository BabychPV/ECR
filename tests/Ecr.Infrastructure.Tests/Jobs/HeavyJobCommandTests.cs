using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Важкі фонові задачі під власним таймаутом і в межах партиції
/// (<c>S-09</c>, <c>S-19</c> директиви №14).
/// </summary>
/// <remarks>
/// ⚠ Перевіряється НЕ текст файлу, а команда, яку EF справді зібрав і подав
/// провайдеру: перехоплювач читає <c>DbCommand</c> у мить
/// <c>…Executing</c> — там уже стоїть і остаточний SQL, і остаточний
/// <c>CommandTimeout</c>. Текстова перевірка джерела тут була б хибнозеленою
/// так само, як у <c>Q-184</c>: рядок <c>SetCommandTimeout</c> можна лишити на
/// місці й зробити його марним (наприклад, поставивши таймаут ПІСЛЯ виклику).
/// <para>
/// ⛔ Обидва контексти піднімаються з ГЛОБАЛЬНИМ таймаутом 60 с — рівно тим,
/// що стоїть у <c>DependencyInjection.cs:58</c>. Без цього тест був би
/// хибнозеленим по-іншому: він відрізняв би власний таймаут задачі від
/// дефолтних 30 с драйвера, а не від тих 60 с, під якими задача падала.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class HeavyJobCommandTests(SqlServerFixture sql)
{
    /// <summary>Глобальний таймаут застосунку — <c>Database:CommandTimeoutSeconds</c>.</summary>
    private const int GlobalTimeoutSeconds = 60;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S-09")]
    public async Task Архівація_року_йде_під_власним_таймаутом_а_не_під_глобальним()
    {
        // ⛔ S-09: `EXEC arc.usp_ArchiveYear` ішов під глобальними 60 с. Рік —
        // десятки мільйонів рядків, тобто таймаут був гарантований, а кожен
        // таймаут вів до ретраю: до трьох архівацій того самого року поспіль.
        //
        // ⚠ Мутаційний доказ: прибери `db.Database.SetCommandTimeout(...)` в
        // `ArchiveJob.ExecuteAsync` (або перенеси його ПІСЛЯ
        // `ExecuteSqlRawAsync`) і перезбери — перехоплювач побачить 60, і
        // `Assert.Equal` почервоніє.
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        await MarkArchivedAsync(doc.ProjectId);

        // ⛔ Сама процедура ПРИГЛУШЕНА. Тест доводить властивість КОМАНДИ, а не
        // роботу архівації (її доводить `ArchiveJobTests`); справжній прогін
        // тут звільнив би партиції всього 2026 року в спільній базі колекції —
        // і падав би не той тест, який щось зламав.
        var recorder = new CommandRecorder(suppressWhen: text => text.Contains("usp_ArchiveYear", StringComparison.Ordinal));

        await using var db = Context(recorder);

        var capabilities = Substitute.For<ISqlCapabilities>();
        capabilities.ArchiveBatchSize.Returns(50_000);

        var job = new ArchiveJob(
            db, capabilities, new TestClock(new DateTime(2027, 6, 1, 2, 0, 0, DateTimeKind.Utc)));

        await job.ExecuteAsync(
            new ArchiveRequest(doc.ProjectId, 2026),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        var archive = Assert.Single(recorder.Matching("usp_ArchiveYear"));

        Assert.Equal(ArchiveJob.CommandTimeoutSeconds, archive.TimeoutSeconds);
        Assert.True(
            archive.TimeoutSeconds > GlobalTimeoutSeconds,
            $"Команда архівації пішла під {archive.TimeoutSeconds} с — це не більше за глобальні "
            + $"{GlobalTimeoutSeconds} с, тобто S-09 не виправлено.{recorder.Format()}");

        // ⚠ І стеля ПОВЕРНУТА: чотири години, лишені на контексті, сховали б
        // зависання будь-якого наступного запиту цієї ж задачі.
        Assert.Equal(GlobalTimeoutSeconds, db.Database.GetCommandTimeout() ?? -1);

        // Читання журналу прогонів після процедури пішло вже під глобальним
        // числом — саме це й означає «власний таймаут у команди, а не в задачі».
        var runLookup = Assert.Single(recorder.Matching("[ArchiveRun]"));
        Assert.Equal(GlobalTimeoutSeconds, runLookup.TimeoutSeconds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S-19")]
    public async Task Перевірка_консистентності_несе_предикат_PeriodKey_у_кожному_запиті()
    {
        // ⛔ S-19: анти-джойн ішов по ВСІЙ `doc.CellValue` без ключа партиції.
        // `WR-05` уже виміряв ціну цього на трьох партиціях: 53 логічні
        // читання проти 3.
        //
        // ⚠ Мутаційний доказ: прибери `where cell.PeriodKeyValue == periodKey`
        // в `ConsistencyCheckJob.OrphanedCellsAsync` (або
        // `row.PeriodKeyValue == periodKey` у `BrokenReferencesAsync`) і
        // перезбери — у згенерованому SQL зникне `[PeriodKey] =`, і тест
        // почервоніє з надрукованим запитом.
        await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202602, rowCount: 1, ct: CancellationToken.None);

        var recorder = new CommandRecorder();

        await using var db = Context(recorder);

        var job = new ConsistencyCheckJob(
            db,
            Substitute.For<IOrphanScanner>(),
            new TestClock(new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IConsistencyMetrics>());

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        // Обидві партиційовані таблиці, і кожна — окремим рядком: мовчазна
        // відсутність запиту по одній із них означала б, що перевірка просто
        // перестала її дивитися.
        AssertPartitionScoped(recorder, "[doc].[CellValue]");
        AssertPartitionScoped(recorder, "[doc].[TableRow]");
    }

    /// <summary>
    /// Кожен запит до партиційованої таблиці несе рівність по
    /// <c>PeriodKey</c> НА СВОЄМУ боці і йде під таймаутом перевірки.
    /// </summary>
    /// <param name="recorder">Перехоплені команди прогону.</param>
    /// <param name="table">Таблиця, як її пише EF: <c>[схема].[Таблиця]</c>.</param>
    /// <remarks>
    /// ⛔ Перевіряється <c>[аліас].[PeriodKey] =</c>, а не просто
    /// <c>[PeriodKey] =</c> десь у тексті — і це не педантизм, а виправлення
    /// хибнозеленого. Перша редакція цього тесту шукала підрядок по всьому
    /// запиту й ПЕРЕЖИЛА мутацію: в анти-джойні <c>BrokenReferencesAsync</c>
    /// свій <c>[t0].[PeriodKey] = @periodKey</c> стоїть на
    /// <c>doc.TableInstance</c>, тож прибраний предикат на САМІЙ
    /// <c>doc.TableRow</c> лишав підрядок на місці, а таблицю — читаною
    /// цілком.
    /// </remarks>
    private static void AssertPartitionScoped(CommandRecorder recorder, string table)
    {
        var queries = recorder.Matching(table);

        // ⛔ «Жодного запиту» не є успіхом: саме так виглядала б перевірка,
        // яку випадково вимкнули, і саме так вона проходила б мовчки.
        Assert.NotEmpty(queries);

        foreach (var query in queries)
        {
            var alias = OuterAlias(query.Text, table);

            Assert.True(
                query.Text.Contains($"[{alias}].[PeriodKey] =", StringComparison.Ordinal),
                $"Запит читає {table} без предиката ключа партиції на ній самій (S-19):\n{query.Text}");

            Assert.Equal(ConsistencyCheckJob.CommandTimeoutSeconds, query.TimeoutSeconds);
        }
    }

    /// <summary>Аліас, під яким запит читає саме цю таблицю.</summary>
    /// <param name="sql">Текст запиту.</param>
    /// <param name="table">Таблиця у формі <c>[схема].[Таблиця]</c>.</param>
    private static string OuterAlias(string sql, string table)
    {
        var marker = $"FROM {table} AS [";
        var at = sql.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(at >= 0, $"У запиті немає «{marker}»:\n{sql}");

        var start = at + marker.Length;

        return sql[start..sql.IndexOf(']', start)];
    }

    /// <summary>Контекст із глобальним таймаутом застосунку і перехоплювачем.</summary>
    private EcrDbContext Context(CommandRecorder recorder)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(GlobalTimeoutSeconds))
            .AddInterceptors(recorder)
            .Options);

    /// <summary>
    /// Ставить проєкту стан, у якому фізична архівація дозволена.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <remarks>
    /// ⚠ Сирим <c>UPDATE</c>, як і в <c>ArchiveJobTests</c>: <c>Status</c> і
    /// <c>ClosedAt</c> мають приватні сетери, а проводити проєкт через увесь
    /// життєвий цикл заради однієї властивості команди — це вже інший тест.
    /// </remarks>
    private async Task MarkArchivedAsync(int projectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01' WHERE Id = {projectId};";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

/// <summary>
/// Перехоплювач: запам'ятовує текст і таймаут кожної команди EF, а за
/// потреби приглушує ту, виконання якої в тесті небажане.
/// </summary>
/// <param name="suppressWhen">
/// Команди, для яких виконання підміняється нулем; <c>null</c> — виконувати всі.
/// </param>
/// <remarks>
/// ⚠ Приглушення не послаблює доказу: <c>CommandTimeout</c> і текст уже
/// остаточні в мить <c>…Executing</c> — це рівно те, що пішло б у драйвер.
/// </remarks>
internal sealed class CommandRecorder(Func<string, bool>? suppressWhen = null) : DbCommandInterceptor
{
    private readonly ConcurrentQueue<SeenCommand> _seen = new();

    /// <summary>Команди, чий текст містить підрядок.</summary>
    /// <param name="fragment">Підрядок; порівняння за ординалом.</param>
    public IReadOnlyList<SeenCommand> Matching(string fragment)
        => [.. _seen.Where(c => c.Text.Contains(fragment, StringComparison.Ordinal))];

    /// <summary>Усі перехоплені команди — для повідомлення тесту, що впав.</summary>
    public string Format()
    {
        var text = new StringBuilder().AppendLine().AppendLine("Перехоплені команди:");
        foreach (var command in _seen)
        {
            text.AppendLine(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  [{command.TimeoutSeconds,6} с] {First(command.Text)}");
        }

        return text.ToString();
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return suppressWhen is not null && suppressWhen(command.CommandText)
            ? ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0))
            : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static string First(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;

        return line.Length > 120 ? line[..120] : line.Trim();
    }

    private void Record(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _seen.Enqueue(new SeenCommand(command.CommandText, command.CommandTimeout));
    }
}

/// <summary>Перехоплена команда.</summary>
/// <param name="Text">Остаточний текст.</param>
/// <param name="TimeoutSeconds">Остаточний <c>CommandTimeout</c>.</param>
internal sealed record SeenCommand(string Text, int TimeoutSeconds);
