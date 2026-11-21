// tests/Ecr.Infrastructure.Tests/Persistence/AuditStructureChangeEntityIndexTests.cs
using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q1-05: <c>IX_StructureChange_Entity (EntityType, EntityId, ChangedAt)</c> на <c>aud.StructureChange</c> і його
/// дзеркало в <c>arc.AuditStructureChange</c>: історія сутності - seek, а не скан усього журналу; SWITCH партицій
/// архіву лишається чинним; скрипти <c>11</c>/<c>12</c> ідемпотентні й доганяють наявну базу.
/// </summary>
/// <remarks>
/// ⛔ Дзеркало не косметика: <c>arc.usp_ArchiveAudit</c> перемикає партиції <c>ALTER TABLE … SWITCH</c>, який падає на
/// будь-якій розбіжності індексів джерела й цілі (Msg 4913). Тест реально перемикає партицію зі структурною зміною.
/// <para>
/// Мутації: прибрати індекс із <c>12-archive-tables.sql</c> - червоніє <see cref="Архівація_перемикає_партицію_структурних_змін_з_новим_індексом"/>
/// (SWITCH 4913); прибрати індекс із <c>11-audit-tables.sql</c> - червоніє план (скан кластерного ключа); змінити порядок
/// стовпців ключа - червоніє перевірка визначення.
/// </para>
/// <para>
/// B146 (D-536): <c>IX_StructureChange_Recalc (EntityType, Operation, CorrelationId, ChangedAt)</c> - позначка «перераховано після
/// подання» (вʼюхи rpt і картка документа) шукає запис за ключем стану. Сторожі ті самі: визначення в журналі й архіві,
/// ідемпотентність і догін бази без індексу, SWITCH партиції. План позначки (seek, без скану журналу) і межу читань стереже
/// <c>ReportViewRecalculatedAfterSubmissionTests</c> (тест обсягу). Мутації 16.11 (правило 410, збірка OK у кожній): індекс лише
/// в 11 (12 пропущено) - 3/4 цього класу (визначення, ідемпотентність, SWITCH Msg 4913); індексу немає ні в 11, ні в 12 - 2/4 тут
/// і всі три тести обсягу (FORCESEEK на відсутній індекс, Msg 308); без FORCESEEK (індекс є) - обидві точки тесту обсягу.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditStructureChangeEntityIndexTests
{
    private const string IndexName = "IX_StructureChange_Entity";

    private const string RecalcIndexName = "IX_StructureChange_Recalc";

    private static readonly DateTime Today = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "Q1-05")]
    public async Task Індекс_є_в_журналі_й_в_архіві_з_однаковим_визначенням_на_схемі_партиціонування()
    {
        var database = SqlServerFixture.WithOwnDatabase("_scidx");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            var live = await DefinitionAsync(cs, "aud.StructureChange");
            var archive = await DefinitionAsync(cs, "arc.AuditStructureChange");

            Assert.Equal("EntityType,EntityId,ChangedAt|ps_AuditByMonth|NONCLUSTERED|0", live);
            Assert.Equal(live, archive);

            var recalc = await DefinitionAsync(cs, "aud.StructureChange", RecalcIndexName);
            Assert.Equal("EntityType,Operation,CorrelationId,ChangedAt|ps_AuditByMonth|NONCLUSTERED|0", recalc);
            Assert.Equal(recalc, await DefinitionAsync(cs, "arc.AuditStructureChange", RecalcIndexName));

            // Підказка FORCESEEK у правилі позначки називає САМЕ цей індекс, і він є в розгорнутій базі (інакше кожне читання
            // вʼюх rpt падає Msg 308): ім'я береться з розгорнутого тексту функції, а не з константи тесту.
            var function = await ScalarAsync<string>(cs, "SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.fn_RecalculatedAfterSubmission'))");
            var hinted = System.Text.RegularExpressions.Regex.Match(function, @"FORCESEEK\s*\(\s*(\w+)").Groups[1].Value;
            Assert.Equal(RecalcIndexName, hinted);
            Assert.Equal(1, await CountAsync(cs, "aud.StructureChange", hinted));
            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM rpt.fn_RecalculatedAfterSubmission()"));   // читається
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "Q1-05")]
    public async Task Скрипти_ідемпотентні_і_доганяють_базу_без_індексу()
    {
        var database = SqlServerFixture.WithOwnDatabase("_scidx");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // Повторний прогін обох скриптів на вже оновленій базі - 0 змін, без помилок.
            await RunScriptAsync(cs, "11-audit-tables.sql");
            await RunScriptAsync(cs, "12-archive-tables.sql");
            var before = await IndexStampAsync(cs);
            await RunScriptAsync(cs, "11-audit-tables.sql");
            await RunScriptAsync(cs, "12-archive-tables.sql");
            Assert.Equal(before, await IndexStampAsync(cs));   // другий прогін - 0 змін (ті самі index_id, без перебудови)
            foreach (var name in new[] { IndexName, RecalcIndexName })
            {
                Assert.Equal(1, await CountAsync(cs, "aud.StructureChange", name));
                Assert.Equal(1, await CountAsync(cs, "arc.AuditStructureChange", name));
            }

            // База, розгорнута ДО цих індексів: їх немає ні в журналі, ні в архіві. Рядки журналу вже є.
            await ExecAsync(cs, "INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId) "
                + "VALUES ('2026-10-02', 1, N'cfg.RegistryDef', 7, 1, N'SaveRules', 1);");
            foreach (var name in new[] { IndexName, RecalcIndexName })
            {
                await ExecAsync(cs, $"DROP INDEX {name} ON aud.StructureChange; DROP INDEX {name} ON arc.AuditStructureChange;");
                Assert.Equal(0, await CountAsync(cs, "aud.StructureChange", name));
                Assert.Equal(0, await CountAsync(cs, "arc.AuditStructureChange", name));
            }

            // Звичайний порядок розгортання: 11, потім 12.
            await RunScriptAsync(cs, "11-audit-tables.sql");
            await RunScriptAsync(cs, "12-archive-tables.sql");
            foreach (var name in new[] { IndexName, RecalcIndexName })
            {
                Assert.Equal(1, await CountAsync(cs, "aud.StructureChange", name));
                Assert.Equal(1, await CountAsync(cs, "arc.AuditStructureChange", name));
                Assert.Equal(
                    await DefinitionAsync(cs, "aud.StructureChange", name),
                    await DefinitionAsync(cs, "arc.AuditStructureChange", name));
            }

            // Шлях оновлення: після догону правило позначки (FORCESEEK за іменем індексу) знову читається.
            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM rpt.fn_RecalculatedAfterSubmission()"));

            // Рядок журналу не зачеплено (створення індексу - DDL, тригер незмінності його не бачить).
            Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityId = 7"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "Q1-05")]
    public async Task Архівація_перемикає_партицію_структурних_змін_з_новим_індексом()
    {
        var database = SqlServerFixture.WithOwnDatabase("_scidx");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            await ExecAsync(cs, """
                INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId)
                VALUES ('2026-02-10', 1, N'cfg.RegistryDef', 7, 1, N'old-feb', 1),
                       ('2026-10-02', 1, N'cfg.RegistryDef', 7, 1, N'fresh-oct', 1);
                """);

            // Поріг 3 місяці від 2026-10-15: лютий старіший, жовтень - ні.
            var (partitions, rows) = await ArchiveAsync(cs, olderThanMonths: 3);

            Assert.Equal((1, 1L), (partitions, rows));
            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'old-feb'"));
            Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM arc.AuditStructureChange WHERE Operation = N'old-feb'"));
            Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'fresh-oct'"));

            // Перемкнутий рядок досяжний ПО ІНДЕКСУ архіву: дані партиції переїхали разом з індексом.
            Assert.Equal(1, await ScalarAsync<int>(
                cs,
                $"SELECT COUNT(*) FROM arc.AuditStructureChange WITH (INDEX({IndexName})) WHERE EntityType = N'cfg.RegistryDef' AND EntityId = 7"));

            // І назад (відновлення за runbook): SWITCH у зворотний бік теж проходить.
            await ExecAsync(cs, """
                DECLARE @p int = $PARTITION.pf_AuditByMonth('2026-02-10');
                DECLARE @sql nvarchar(400) = N'ALTER TABLE arc.AuditStructureChange SWITCH PARTITION ' + CAST(@p AS nvarchar(10))
                    + N' TO aud.StructureChange PARTITION ' + CAST(@p AS nvarchar(10));
                EXEC sp_executesql @sql;
                """);
            Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'old-feb'"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "Q1-05")]
    public async Task Історія_сутності_йде_по_індексу_а_не_скану_кластерного_ключа()
    {
        var database = SqlServerFixture.WithOwnDatabase("_scidx");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // 30 000 структурних змін на ≈3 000 сутностей (≈10 на сутність) у трьох місяцях: так журнал виглядає після
            // років роботи. На кількох десятках рядків оптимізатор і скан вважає дешевим - тест міряв би порожнечу.
            await ExecAsync(cs, """
                ;WITH n AS (SELECT TOP (30000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                              FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
                INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId)
                SELECT DATEADD(MINUTE, i, CASE i % 3 WHEN 0 THEN '2026-08-01' WHEN 1 THEN '2026-09-01' ELSE '2026-10-01' END),
                       1, N'cfg.RegistryDef', CAST(i % 2999 AS int), 1, N'SaveRules', 1
                  FROM n;
                """);

            // ⚠ БОЙОВИЙ текст (`AuditReader.StructureHistorySql`), а не копія в тесті.
            var plan = await PlanAsync(
                cs,
                AuditReader.StructureHistorySql(["@t0"]),
                new SqlParameter("@t0", SqlDbType.NVarChar, 64) { Value = "cfg.RegistryDef" },
                new SqlParameter("@take", SqlDbType.Int) { Value = 50 },
                new SqlParameter("@entityId", SqlDbType.Int) { Value = 17 });

            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var document = XDocument.Parse(plan);

            var usesIndex = document.Descendants(ns + "Object")
                .Any(o => (string?)o.Attribute("Index") == $"[{IndexName}]");
            Assert.True(usesIndex, "Історія сутності не користується IX_StructureChange_Entity.");

            var clusteredScans = document.Descendants(ns + "RelOp")
                .Where(r => (string?)r.Attribute("PhysicalOp") == "Clustered Index Scan"
                            && r.Descendants(ns + "Object").Any(o => (string?)o.Attribute("Table") == "[StructureChange]"))
                .ToList();
            Assert.Empty(clusteredScans);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// Визначення індексу: ключ (за порядком, зі спаданням), схема партиціонування, тип і ознака фільтра -
    /// у вигляді, придатному для порівняння між таблицями.
    /// </summary>
    private static async Task<string> DefinitionAsync(string cs, string table, string indexName = IndexName)
    {
        return await ScalarAsync<string>(
            cs,
            $"""
            SELECT (SELECT STRING_AGG(CAST(c.name AS nvarchar(128)) COLLATE DATABASE_DEFAULT
                                      + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END, N',')
                           WITHIN GROUP (ORDER BY ic.key_ordinal)
                      FROM sys.index_columns AS ic
                      JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0)
                   + N'|' + CAST(ds.name AS nvarchar(128)) COLLATE DATABASE_DEFAULT
                   + N'|' + CAST(i.type_desc AS nvarchar(60)) COLLATE DATABASE_DEFAULT
                   + N'|' + CAST(i.has_filter AS nvarchar(1))
              FROM sys.indexes AS i
              JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
             WHERE i.object_id = OBJECT_ID(N'{table}') AND i.name = N'{indexName}';
            """);
    }

    private static Task<int> CountAsync(string cs, string table, string indexName = IndexName)
        => ScalarAsync<int>(
            cs, $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{indexName}'");

    /// <summary>
    /// Відбиток стану обох індексів у журналі й архіві: index_id і дата останньої зміни статистики за ключем - перебудова чи
    /// перестворення індексу її змінює, повторний прогін скрипту, що нічого не робить, - ні.
    /// </summary>
    private static Task<string> IndexStampAsync(string cs)
        => ScalarAsync<string>(cs, $"""
            SELECT STRING_AGG(CONCAT(OBJECT_NAME(i.object_id), N'.', i.name, N'#', i.index_id, N'@',
                                     CONVERT(nvarchar(30), STATS_DATE(i.object_id, i.index_id), 126)), N';')
                   WITHIN GROUP (ORDER BY i.object_id, i.name)
              FROM sys.indexes AS i
             WHERE i.object_id IN (OBJECT_ID(N'aud.StructureChange'), OBJECT_ID(N'arc.AuditStructureChange'))
               AND i.name IN (N'{IndexName}', N'{RecalcIndexName}');
            """);

    /// <summary>Виконує скрипт із <c>Persistence/Sql</c> пакетами по <c>GO</c> (як <c>setup-dev-db.ps1</c> через sqlcmd).</summary>
    private static async Task RunScriptAsync(string cs, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", fileName);
        var script = await File.ReadAllTextAsync(path);

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        foreach (var batch in SqlBatches.Split(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(int Partitions, long Rows)> ArchiveAsync(string cs, int olderThanMonths)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        // ✎ PSS-27: механіка SWITCH — у внутрішній процедурі з явною межею (публічна тримає підлогу 120 міс.).
        cmd.CommandText = "EXEC arc.usp_ArchiveAuditBefore @Cutoff = @c, @OlderThanMonths = @m, @PartitionsSwitched = @p OUTPUT, @RowsSwitched = @r OUTPUT;";
        cmd.Parameters.Add("@c", SqlDbType.DateTime2).Value = new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-olderThanMonths);
        cmd.Parameters.AddWithValue("@m", olderThanMonths);
        var p = cmd.Parameters.Add("@p", SqlDbType.Int);
        p.Direction = ParameterDirection.Output;
        var r = cmd.Parameters.Add("@r", SqlDbType.BigInt);
        r.Direction = ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync();
        return ((int)p.Value, (long)r.Value);
    }

    private static async Task<string> PlanAsync(string cs, string text, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();

        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON;";
            await on.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddRange(parameters);

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 1
                && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase)
                && await reader.ReadAsync())
            {
                return reader.GetString(0);
            }
        }
        while (await reader.NextResultAsync());

        throw new InvalidOperationException("SQL Server не повернув план виконання.");
    }

    private static async Task ExecAsync(string cs, string text)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string cs, string text)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = text;
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
