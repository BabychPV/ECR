using System.Globalization;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-247: <c>arc.usp_ArchiveAudit</c> перемикає (SWITCH) старі партиції <c>aud.*</c> в
/// <c>arc.Audit*</c>: рядки в архіві й відсутні в <c>aud</c>, свіжі на місці, журнал
/// <c>itg.MaintenanceRun</c> має слід, повтор нічого не робить, тригери незмінності
/// (THROW 50060) НЕ спрацьовують на SWITCH і діють на UPDATE/DELETE; перенесення назад — SWITCH.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати `ALTER TABLE … SWITCH` з процедури, або зсунути поріг
/// («старше N» → усе), — тест червоніє. Власна база: архівація зачіпає ВСІ старі партиції,
/// а спільна БД колекції містить чужі рядки аудиту зі старими датами.
/// <para>
/// ✎ PSS-27: механіку SWITCH на коротких порогах перевіряє ВНУТРІШНЯ <c>arc.usp_ArchiveAuditBefore</c> з явною межею;
/// публічна <c>arc.usp_ArchiveAudit</c> тримає підлогу 120 міс. і не приймає «сьогодні» з майбутнього —
/// її доводить <see cref="AuditArchiveHorizonTests"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditArchiveSwitchTests
{
    private static readonly DateTime Today = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старі_партиції_переходять_в_архів_свіжі_на_місці_слід_у_журналі_повтор_ідемпотентний_тригери_діють()
    {
        var database = SqlServerFixture.WithOwnDatabase("_audarc");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // Старі (до 2026-07-01 за поріг 3 міс. від 2026-10-15) і свіжі рядки.
            await ExecAsync(cs, CellChangeInsert("2026-02-10", "old-feb"));
            await ExecAsync(cs, CellChangeInsert("2026-05-20", "old-may"));
            await ExecAsync(cs, CellChangeInsert("2026-08-10", "mid-aug"));
            await ExecAsync(cs, CellChangeInsert("2026-10-02", "fresh-oct"));
            await ExecAsync(cs, "INSERT INTO aud.SecurityEvent (ChangedAt, EventType, ChangedByUserId) VALUES ('2026-03-05', N'old-mar', 1), ('2026-10-03', N'fresh-oct', 1)");
            await ExecAsync(cs, "INSERT INTO aud.PublicationEvent (ChangedAt, EntityType, EntityId, ChangeReason, ChangedByUserId) VALUES ('2026-04-01', N'TemplateVersion', 7, N'old-apr', 1)");

            var idBefore = await ScalarAsync(cs, "SELECT Id FROM aud.CellChange WHERE NewValue = N'old-feb'");

            var (partitions, rows) = await ArchiveAsync(cs, olderThanMonths: 3);

            // Чотири старі партиції-таблиці: CellChange (лют., трав.), SecurityEvent (бер.), PublicationEvent (квіт.).
            Assert.Equal(4, partitions);
            Assert.Equal(4, rows);

            // Старе — в архіві, відсутнє в aud; значення не втрачені, Id збережено.
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE ChangedAt < '2026-07-01'"));
            Assert.Equal(2L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange WHERE NewValue IN (N'old-feb', N'old-may')"));
            Assert.Equal(idBefore, await ScalarAsync(cs, "SELECT Id FROM arc.AuditCellChange WHERE NewValue = N'old-feb'"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'old-mar'"));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditSecurityEvent WHERE EventType = N'old-mar'"));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditPublicationEvent WHERE ChangeReason = N'old-apr'"));

            // Свіжі й «гарячі» на місці, в архів не потрапили.
            Assert.Equal(2L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE NewValue IN (N'mid-aug', N'fresh-oct')"));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'fresh-oct'"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange WHERE NewValue IN (N'mid-aug', N'fresh-oct')"));

            // Слід у журналі: що, який період, скільки рядків, коли, хто.
            Assert.Equal(4L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.MaintenanceRun WHERE JobCode = N'audit-archive' AND Status = N'Succeeded' AND FinishedAt IS NOT NULL"));
            Assert.Equal(1L, await ScalarAsync(cs, """
                SELECT COUNT(*) FROM itg.MaintenanceRun
                WHERE JobCode = N'audit-archive'
                  AND JSON_VALUE(DetailsJson, '$.table') = N'aud.CellChange'
                  AND JSON_VALUE(DetailsJson, '$.periodStart') = N'2026-02-01'
                  AND JSON_VALUE(DetailsJson, '$.rows') = N'1'
                  AND JSON_VALUE(DetailsJson, '$.by') IS NOT NULL
                """));

            // Повтор — нічого: ні рядків, ні журналу, без помилок.
            var (p2, r2) = await ArchiveAsync(cs, olderThanMonths: 3);
            Assert.Equal((0, 0L), (p2, r2));
            Assert.Equal(4L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.MaintenanceRun WHERE JobCode = N'audit-archive'"));
            Assert.Equal(2L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange"));

            // Тригери незмінності досі діють — і в aud, і в архіві; SWITCH їх не вмикав і не вимикав.
            Assert.Equal(50060, (await Assert.ThrowsAsync<SqlException>(() =>
                ExecAsync(cs, "UPDATE aud.CellChange SET NewValue = N'x' WHERE NewValue = N'fresh-oct'"))).Number);
            Assert.Equal(50060, (await Assert.ThrowsAsync<SqlException>(() =>
                ExecAsync(cs, "UPDATE arc.AuditCellChange SET NewValue = N'x'"))).Number);
            Assert.Equal(50060, (await Assert.ThrowsAsync<SqlException>(() =>
                ExecAsync(cs, "DELETE FROM arc.AuditSecurityEvent"))).Number);
            Assert.Equal(8L, await ScalarAsync(cs, """
                SELECT COUNT(*) FROM sys.triggers
                WHERE name IN (N'TR_CellChange_Immutable', N'TR_StructureChange_Immutable', N'TR_SecurityEvent_Immutable',
                               N'TR_PublicationEvent_Immutable', N'TR_AuditCellChange_Immutable',
                               N'TR_AuditStructureChange_Immutable', N'TR_AuditSecurityEvent_Immutable',
                               N'TR_AuditPublicationEvent_Immutable')
                  AND is_disabled = 0
                """));

            // Межа — перше число поточного місяця (поріг 1): жовтень на місці, серпневий рядок — в архіві.
            var (p3, r3) = await ArchiveAsync(cs, olderThanMonths: 1);
            Assert.Equal((1, 1L), (p3, r3));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange WHERE NewValue = N'mid-aug'"));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE NewValue = N'fresh-oct'"));

            // Ціль не порожня (пізній рядок у вже архівованому лютому) — партиція НЕ переноситься,
            // у журналі Degraded, дані на місці.
            await ExecAsync(cs, CellChangeInsert("2026-02-12", "late-feb"));
            var (p4, _) = await ArchiveAsync(cs, olderThanMonths: 3);
            Assert.Equal(0, p4);
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE NewValue = N'late-feb'"));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.MaintenanceRun WHERE JobCode = N'audit-archive' AND Status = N'Degraded'"));

            // Відновлення (runbook): SWITCH назад, тригери не заважають. Спершу прибираємо
            // пізній рядок із непорожньої цілі неможливо (незмінний) — тому відновлюємо березневу
            // партицію SecurityEvent, що не має конфліктів.
            await ExecAsync(cs, """
                DECLARE @p int = $PARTITION.pf_AuditByMonth('2026-03-05');
                DECLARE @sql nvarchar(400) = N'ALTER TABLE arc.AuditSecurityEvent SWITCH PARTITION ' + CAST(@p AS nvarchar(10))
                    + N' TO aud.SecurityEvent PARTITION ' + CAST(@p AS nvarchar(10));
                EXEC sp_executesql @sql;
                """);
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'old-mar'"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditSecurityEvent"));

            // Нічна задача сама викликає архівацію — ✎ PSS-27: з глибиною 120 міс. (підлога), тож рядки 2026 року
            // лишаються в aud.*, а в журналі прогону partition-check — лічильники (нуль) і чинна глибина.
            await using var db = new EcrDbContext(
                new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(cs).Options);
            var job = new PartitionCheckJob(
                db, Substitute.For<Ecr.Application.Ports.ISqlCapabilities>(),
                new TestClock(DateTime.UtcNow));
            await job.ExecuteAsync(null, Substitute.For<Ecr.Application.Ports.IJobProgress>(), CancellationToken.None);

            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'old-mar'"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditSecurityEvent WHERE EventType = N'old-mar'"));
            Assert.Equal(1L, await ScalarAsync(cs, """
                SELECT COUNT(*) FROM itg.MaintenanceRun
                WHERE JobCode = N'partition-check' AND JSON_VALUE(DetailsJson, '$.auditArchivedPartitions') = N'0'
                  AND JSON_VALUE(DetailsJson, '$.auditArchiveMonths') = N'120'
                """));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static string CellChangeInsert(string at, string newValue) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin)
         VALUES ('{at}', 202601, 1, 1, N'k', 1, N'o', N'{newValue}', 1, N'UserEdit')
         """);

    private static async Task<(int Partitions, long Rows)> ArchiveAsync(string cs, int olderThanMonths)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "EXEC arc.usp_ArchiveAuditBefore @Cutoff = @c, @OlderThanMonths = @m, @PartitionsSwitched = @p OUTPUT, @RowsSwitched = @r OUTPUT;";
        cmd.Parameters.Add("@c", System.Data.SqlDbType.DateTime2).Value =
            new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-olderThanMonths);
        cmd.Parameters.AddWithValue("@m", olderThanMonths);
        var p = cmd.Parameters.Add("@p", System.Data.SqlDbType.Int);
        p.Direction = System.Data.ParameterDirection.Output;
        var r = cmd.Parameters.Add("@r", System.Data.SqlDbType.BigInt);
        r.Direction = System.Data.ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync();
        return ((int)p.Value, (long)r.Value);
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
