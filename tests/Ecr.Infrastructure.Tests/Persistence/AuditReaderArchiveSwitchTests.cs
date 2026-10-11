using System.Data;
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// НФ-8.9.6 / D-247: читач бачить РЕАЛЬНИЙ вихід <c>arc.usp_ArchiveAudit</c> — партиції, перенесені справжнім
/// <c>SWITCH</c>, а не рядки, вставлені тестом у <c>arc.AuditCellChange</c> напряму.
/// </summary>
/// <remarks>
/// ⛔ Власна база: архівація зачіпає ВСІ старі партиції, а спільна БД колекції містить чужі рядки аудиту зі
/// старими датами (той самий мотив, що в <c>AuditArchiveSwitchTests</c>).
///
/// ⚠ Що доводить саме цей тест, чого не доводить <see cref="AuditReaderArchiveTests"/>: (1) рядок після SWITCH
/// лишається в журналі з тим самим значенням і позицією — читач не «втрачає» його в момент архівації;
/// (2) пізній запис у вже заархівований місяць (<c>Degraded</c>, runbook п. 7.3) лишається в <c>aud</c> поруч
/// з архівними рядками того ж місяця — обидві частини видно однією видачею в порядку <c>ChangedAt</c>.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати з <c>AuditReader.CellChangesWithArchiveSql</c> гілку <c>arc.AuditCellChange</c> —
/// червоніє перевірка «після SWITCH рядки на місці».
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderArchiveSwitchTests
{
    private static readonly DateTime Today = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Після_справжнього_SWITCH_журнал_віддає_ті_самі_рядки_з_архіву_і_пізній_запис_у_заархівований_місяць()
    {
        var database = SqlServerFixture.WithOwnDatabase("_audarcrd");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            await ExecAsync(cs, Insert("2026-02-10T09:00:00", "feb-1"));
            await ExecAsync(cs, Insert("2026-02-20T09:00:00", "feb-2"));
            await ExecAsync(cs, Insert("2026-09-10T09:00:00", "sep-1"));
            await ExecAsync(cs, Insert("2026-10-02T09:00:00", "oct-1"));

            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(cs).Options);
            var reader = new AuditReader(db);
            var filter = new CellChangeFilter(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                DocumentId: 1);

            // До архівації: усе гаряче.
            var before = await ReadAllAsync(reader, filter);
            Assert.Equal(["feb-1", "feb-2", "sep-1", "oct-1"], before.Select(c => c.NewValue));
            Assert.All(before, c => Assert.False(c.FromArchive));

            // Справжній SWITCH: лютий (старший за 3 міс. від 2026-10-15) переходить в arc.AuditCellChange.
            await ArchiveAsync(cs, olderThanMonths: 3);
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE ChangedAt < '2026-07-01'"));
            Assert.Equal(2L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange"));

            // Після: ті самі чотири зміни в тому самому порядку; лютневі — з архіву, решта — гарячі.
            var after = await ReadAllAsync(reader, filter);
            Assert.Equal(["feb-1", "feb-2", "sep-1", "oct-1"], after.Select(c => c.NewValue));
            Assert.Equal([true, true, false, false], after.Select(c => c.FromArchive));

            // Підрахунок бачить архів так само, як сторінка.
            var counts = await reader.CountCellChangesByColumnAsync(filter, Today, CancellationToken.None);
            Assert.Equal(4, counts.Sum(c => c.Total));

            // Історія однієї комірки (найновіші першими) — теж через межу гаряче/архів, сторінками по 1.
            var cell = filter with { RowKey = "k", ColumnDefId = 1 };
            var history = await ReadAllAsync(reader, cell, limit: 1);
            Assert.Equal(["oct-1", "sep-1", "feb-2", "feb-1"], history.Select(c => c.NewValue));

            // Пізній запис у вже заархівований лютий: партиція не переноситься (Degraded), рядок лишається в aud.
            await ExecAsync(cs, Insert("2026-02-15T09:00:00", "feb-late"));
            await ArchiveAsync(cs, olderThanMonths: 3);
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE NewValue = N'feb-late'"));

            // Лютий читається з ОБОХ таблиць одним порядком за ChangedAt.
            var february = await ReadAllAsync(
                reader,
                filter with
                {
                    From = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                    To = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                limit: 2);
            Assert.Equal(["feb-1", "feb-late", "feb-2"], february.Select(c => c.NewValue));
            Assert.Equal([true, false, true], february.Select(c => c.FromArchive));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task<List<CellChangeView>> ReadAllAsync(AuditReader reader, CellChangeFilter filter, int limit = 50)
    {
        var items = new List<CellChangeView>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await reader.ReadCellChangesAsync(filter, new CursorRequest(limit, cursor), CancellationToken.None);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(++pages < 20, "курсор не завершується");
        }
        while (cursor is not null);

        return items;
    }

    private static string Insert(string at, string newValue) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin)
         VALUES ('{at}', 202601, 1, 1, N'k', 1, N'o', N'{newValue}', 1, N'UserEdit')
         """);

    private static async Task ArchiveAsync(string cs, int olderThanMonths)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "EXEC arc.usp_ArchiveAudit @OlderThanMonths = @m, @Today = @t;";
        cmd.Parameters.AddWithValue("@m", olderThanMonths);
        cmd.Parameters.Add("@t", SqlDbType.Date).Value = Today;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(string cs, string sqlText)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sqlText;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string cs, string sqlText)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sqlText;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
