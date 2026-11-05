using System.Globalization;
using System.Xml.Linq;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>RV01</c>: «where used» запису довідника (кількість значень полів, що посилаються на нього) і перевірка
/// <c>FK_RegValue_Ref</c> читають <c>dic.RegistryValue</c> фільтрованим індексом <c>IX_RegistryValue_ValueRef</c>,
/// а не сканом таблиці.
/// </summary>
/// <remarks>
/// Тест кладе 20 000 значень (1000 записів × 20 полів): три чверті посилаються на сусідній запис, решта — NULL.
/// Перший запис не referenced нічим. Мутація (перевірено): без <c>HasIndex</c>/міграції (або після
/// <c>DROP INDEX</c>) план для цього запису — скан <c>PK_RegistryValue</c>/<c>UQ_RegistryValue</c> і логічні читання
/// на порядки більші, обидві перевірки червоніють.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryValueRefIndexTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private const string IndexName = "IX_RegistryValue_ValueRef";
    private const int Entries = 1000;
    private const int Fields = 20;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Індекс_посилань_існує_і_відфільтрований_по_NOT_NULL()
    {
        await using var connection = await OpenAsync().ConfigureAwait(true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.filter_definition, i.has_filter, c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dic.RegistryValue') AND i.name = @name;
            """;
        command.Parameters.AddWithValue("@name", IndexName);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
        Assert.True(await reader.ReadAsync().ConfigureAwait(true), $"Індекс {IndexName} відсутній.");
        Assert.True(reader.GetBoolean(1));
        Assert.Contains("ValueRefEntryId", reader.GetString(0), StringComparison.Ordinal);
        Assert.Contains("NOT NULL", reader.GetString(0), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ValueRefEntryId", reader.GetString(2));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Підрахунок_посилань_на_невикористаний_запис_іде_seek_ом_по_індексу()
    {
        var (defId, unusedEntryId) = await SeedAsync().ConfigureAwait(true);
        try
        {
            var (reads, seekOnIndex, scanned) = await MeasureAsync(unusedEntryId).ConfigureAwait(true);
            output.WriteLine($"RegistryValue logical reads={reads}; seek on {IndexName}={seekOnIndex}; scans={string.Join(",", scanned)}");

            Assert.True(seekOnIndex, $"План не містить Index Seek по {IndexName}; скановано: {string.Join(",", scanned)}.");
            Assert.DoesNotContain(scanned, s => s.Contains("PK_RegistryValue", StringComparison.Ordinal)
                                               || s.Contains("UQ_RegistryValue", StringComparison.Ordinal));

            // Таблиця ~20 000 рядків ≈ 150+ сторінок на кожен з індексів; seek по порожньому діапазону — кілька сторінок.
            Assert.True(reads <= 20, $"Логічні читання dic.RegistryValue = {reads}, очікувалось ≤ 20.");
        }
        finally
        {
            await CleanupAsync(defId).ConfigureAwait(true);
        }
    }

    /// <summary>Запит, еквівалентний <c>RegistryStore.CountReferencesAsync</c> (values) — той самий предикат і join.</summary>
    private async Task<(long Reads, bool SeekOnIndex, List<string> Scanned)> MeasureAsync(long entryId)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        long reads = 0;
        connection.InfoMessage += (_, e) =>
        {
            foreach (var line in e.Message.Split('\n'))
            {
                if (!line.StartsWith("Table 'RegistryValue'.", StringComparison.Ordinal))
                {
                    continue;
                }

                var marker = "logical reads ";
                var at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    var tail = line[(at + marker.Length)..];
                    var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
                    reads += long.Parse(digits, CultureInfo.InvariantCulture);
                }
            }
        };

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "UPDATE STATISTICS dic.RegistryValue; SET STATISTICS IO ON; SET STATISTICS XML ON;";
            await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dic.RegistryValue AS v
            INNER JOIN dic.RegistryEntry AS o ON v.RegistryEntryId = o.Id
            WHERE v.ValueRefEntryId = @id AND o.Id <> @id AND o.IsDeleted = 0;
            """;
        command.Parameters.AddWithValue("@id", entryId);

        var seek = false;
        var scanned = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
        {
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string)
                        && reader.GetString(0).Contains("ShowPlanXML", StringComparison.Ordinal))
                    {
                        var plan = XDocument.Parse(reader.GetString(0));
                        foreach (var rel in plan.Descendants(XName.Get("RelOp", ShowPlanNs)))
                        {
                            var op = (string?)rel.Attribute("PhysicalOp") ?? string.Empty;
                            foreach (var target in rel.Elements().SelectMany(e => e.Elements(XName.Get("Object", ShowPlanNs))))
                            {
                                if ((string?)target.Attribute("Table") != "[RegistryValue]")
                                {
                                    continue;
                                }

                                var index = (string?)target.Attribute("Index") ?? "(heap)";
                                if (op.Contains("Seek", StringComparison.Ordinal) && index == $"[{IndexName}]")
                                {
                                    seek = true;
                                }

                                if (op.Contains("Scan", StringComparison.Ordinal))
                                {
                                    scanned.Add(index);
                                }
                            }
                        }
                    }
                }
            }
            while (await reader.NextResultAsync().ConfigureAwait(false));
        }

        return (reads, seek, scanned);
    }

    private async Task<(int DefId, long UnusedEntryId)> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var def = new RegistryDef(EcrCode.Create($"RV{tag}"), Name($"RV01 {tag}"), isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entries = Enumerable.Range(0, Entries)
            .Select(i => new RegistryEntry(def.Id, EcrCode.Create($"E{tag}{i:D4}"), Name($"E{i}")))
            .ToList();
        db.RegistryEntries.AddRange(entries);
        var fields = Enumerable.Range(0, Fields)
            .Select(i => new RegistryFieldDef(def.Id, EcrCode.Create($"F{tag}{i:D2}"), Name($"F{i}"), CellDataType.Decimal, i))
            .ToList();
        db.RegistryFieldDefs.AddRange(fields);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var firstId = entries.Min(e => (long)e.Id);

        await using var connection = await OpenAsync().ConfigureAwait(false);
        foreach (var field in fields)
        {
            // Порція = одне поле = 1000 значень; 3 з 4 посилаються на наступний запис (перший не referenced), решта NULL.
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                ;WITH e AS (
                    SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS rn FROM dic.RegistryEntry WHERE RegistryDefId = @def)
                INSERT dic.RegistryValue (RegistryEntryId, RegistryFieldDefId, ValueRefEntryId)
                SELECT e.Id, @field, CASE WHEN e.rn % 4 = 0 THEN NULL ELSE nxt.Id END
                FROM e LEFT JOIN e AS nxt ON nxt.rn = e.rn + 1;
                CHECKPOINT;
                """;
            insert.Parameters.AddWithValue("@def", def.Id);
            insert.Parameters.AddWithValue("@field", field.Id);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return (def.Id, firstId);
    }

    private async Task CleanupAsync(int defId)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            WHILE 1 = 1
            BEGIN
                DELETE TOP (2000) v FROM dic.RegistryValue v
                  JOIN dic.RegistryEntry e ON e.Id = v.RegistryEntryId WHERE e.RegistryDefId = @def;
                IF @@ROWCOUNT = 0 BREAK;
            END;
            DELETE FROM cfg.RegistryFieldDef WHERE RegistryDefId = @def;
            DELETE FROM dic.RegistryEntry WHERE RegistryDefId = @def;
            DELETE FROM cfg.RegistryDef WHERE Id = @def;
            CHECKPOINT;
            """;
        command.Parameters.AddWithValue("@def", defId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
