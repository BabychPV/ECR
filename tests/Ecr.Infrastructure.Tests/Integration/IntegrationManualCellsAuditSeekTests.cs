using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// R5-Q1-01: запити «чи правила комірку людина» інтеграції (<see cref="IntegrationCellPatcher.ManualCellsSql"/>,
/// <see cref="SourceEventSyncJob.ManualRowKeysSql"/>) читають <c>aud.CellChange</c> seek'ом за документом,
/// а не сканом усього журналу аудиту системи.
/// </summary>
/// <remarks>
/// ⛔ Міряється БОЙОВИЙ текст; контрольна форма «без документа» будується ВІДНІМАННЯМ предиката з нього
/// (<see cref="WithoutDocument"/>), і сам факт віднімання перевіряється. Контроль доводить, що середовище
/// здатне показати скан: без <c>DocumentId</c> жоден індекс <c>aud.CellChange</c> не дає seek.
/// <para>
/// ⚠ Перевіряється форма плану (немає <c>Scan</c> по <c>[CellChange]</c>) і набір рядків, а не логічні читання:
/// на малих даних числа читань залежать від кількості порожніх партицій <c>pf_AuditByMonth</c>, а форма — ні.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class IntegrationManualCellsAuditSeekTests(SqlServerFixture sql, ITestOutputHelper output)
{
    /// <summary>Місяці шуму чужого документа — три заповнені партиції журналу.</summary>
    private static readonly DateTime[] NoiseMonths =
    [
        new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc),
        new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc),
    ];

    /// <summary>Записів журналу чужого документа на місяць: щоб скан мав що читати, а seek — чим виграти.</summary>
    private const int NoiseRowsPerMonth = 5000;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static TheoryData<string, string> Queries => new()
    {
        { nameof(IntegrationCellPatcher.ManualCellsSql), IntegrationCellPatcher.ManualCellsSql },
        { nameof(SourceEventSyncJob.ManualRowKeysSql), SourceEventSyncJob.ManualRowKeysSql },
    };

    [Theory]
    [MemberData(nameof(Queries))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "R5-Q1-01")]
    public async Task Правка_людини_шукається_seekом_за_документом_а_не_сканом_журналу(string name, string text)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var target = await builder.BuildAsync(ct: CancellationToken.None);
        var noise = await builder.BuildAsync(ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            var writer = new AuditWriter(db);

            foreach (var month in NoiseMonths)
            {
                await writer.WriteCellChangesAsync(
                    [.. Enumerable.Range(0, NoiseRowsPerMonth)
                        .Select(i => Change(noise, i % noise.RowIds.Count, 1, month.AddSeconds(i), "UserEdit", $"N{i % 4}"))],
                    CancellationToken.None);
            }

            await writer.WriteCellChangesAsync(
                [
                    // R1: остання — людина.
                    Change(target, 0, 1, NoiseMonths[1], "UserEdit", "R1"),

                    // R2: людину пізніше перекрила інтеграція — не ручна.
                    Change(target, 1, 1, NoiseMonths[0], "UserEdit", "R2"),
                    Change(target, 1, 1, NoiseMonths[2], "Integration", "R2"),

                    // R3: імпорт книги — теж людина (D2-01).
                    Change(target, 2, 2, NoiseMonths[2], "Import", "R3"),
                ],
                CancellationToken.None);
        }

        var fixedRun = await RunAsync(target, text);
        var controlRun = await RunAsync(target, WithoutDocument(text));

        output.WriteLine(
            $"{name}: бойовий — {string.Join(", ", fixedRun.Ops)}; без документа — {string.Join(", ", controlRun.Ops)}");

        // Семантика та сама: лише рядки цільового екземпляра, остання зміна — людина.
        Assert.Equal(["R1", "R3"], fixedRun.RowKeys);
        Assert.Equal(controlRun.RowKeys, fixedRun.RowKeys);

        // Контроль: без документа — скан (середовище здатне показати те, від чого захищаємось).
        Assert.Contains(controlRun.Ops, op => op.Contains("Scan", StringComparison.Ordinal));

        // ⛔ Головне: бойовий текст не сканує журнал аудиту.
        Assert.NotEmpty(fixedRun.Ops);
        Assert.DoesNotContain(fixedRun.Ops, op => op.Contains("Scan", StringComparison.Ordinal));
    }

    /// <summary>Той самий текст без предиката документа.</summary>
    /// <exception cref="InvalidOperationException">Предиката в тексті немає — міряти нема чого.</exception>
    private static string WithoutDocument(string text)
    {
        var predicate = new Regex(@"c\.DocumentId = @document\s+AND ", RegexOptions.None, TimeSpan.FromSeconds(5));

        if (!predicate.IsMatch(text))
        {
            throw new InvalidOperationException(
                "У бойовому тексті немає `c.DocumentId = @document AND` — контрольна форма порівнювала б запит сама з собою.");
        }

        return predicate.Replace(text, string.Empty);
    }

    /// <summary>Виконує текст під <c>STATISTICS XML</c>: ключі рядків і фізичні оператори по <c>[CellChange]</c>.</summary>
    private async Task<(List<string> RowKeys, List<string> Ops)> RunAsync(TestDocument doc, string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + text + "\nSET STATISTICS XML OFF;";
        command.Parameters.Add("@period", System.Data.SqlDbType.Int).Value = doc.PeriodKey.Value;
        command.Parameters.Add("@instance", System.Data.SqlDbType.BigInt).Value = doc.TableInstanceId;
        command.Parameters.Add("@document", System.Data.SqlDbType.BigInt).Value = doc.DocumentId;

        var keys = new List<string>();
        var plans = new List<string>();

        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            do
            {
                var isPlan = reader.FieldCount == 1
                             && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal);

                while (await reader.ReadAsync(CancellationToken.None))
                {
                    if (isPlan)
                    {
                        plans.Add(reader.GetString(0));
                    }
                    else
                    {
                        keys.Add(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync(CancellationToken.None));
        }

        var ops = plans
            .SelectMany(p => XDocument.Parse(p).Descendants(Showplan + "RelOp"))
            .Where(op => op.Elements().Any(e => e.Elements(Showplan + "Object")
                .Any(o => (string?)o.Attribute("Table") == "[CellChange]")))
            .Select(op => (string)op.Attribute("PhysicalOp")!)
            .ToList();

        return ([.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], ops);
    }

    /// <summary>Запис журналу про комірку документа.</summary>
    private static CellChangeRecord Change(
        TestDocument doc, int row, int column, DateTime at, string origin, string rowKey)
        => new(
            at,
            new CellAddress(doc.PeriodKey, doc.RowIds[row], doc.ColumnDefIds[column]),
            doc.DocumentId,
            rowKey,
            OldValue: null,
            NewValue: "1",
            ChangedByUserId: 1,
            Origin: origin,
            IsLateEdit: false,
            CorrelationId: null);
}
