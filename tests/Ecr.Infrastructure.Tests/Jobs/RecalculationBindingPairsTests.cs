// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationBindingPairsTests.cs
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// W15z (репетиція на копії живої бази 23.11): після переносу doc 96 на LAND_UI 1.0.20.0 методологія ECW_C11_10 на
/// T9a_28 перестала рахуватись - 28 результатів зникли без помилки. Причина: розклад прогону читав РЯДКИ прив'язок за Id
/// зі стелею 5 000, а річна розкладка має 9 300 прив'язок (по одній на місяць кожного виходу); прив'язки за межею мовчки
/// відрізались. Тепер читаються різні пари «таблиця × методологія».
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>RecalculationJob.BindingPairsAsync</c> повернути <c>OrderBy(b =&gt; b.Id).Take(MaxBindings)</c> по
/// РЯДКАХ прив'язок до <c>Distinct</c> - методологія з найновішою прив'язкою зникає з пар, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationBindingPairsTests(SqlServerFixture sql)
{
    private const int ManyBindings = 5_100;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Методологія_з_найновішою_прив_язкою_за_межею_5000_рядків_лишається_в_розкладі_прогону()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8];

        var (early, late) = (await ScalarAsync<int>(Methodology($"RB_EARLY_{tag}")), await ScalarAsync<int>(Methodology($"RB_LATE_{tag}")));

        // 5 100 рядків прив'язок «ранньої» методології (як місячні прив'язки річного шаблону), потім одна «пізня».
        await ExecuteAsync($$"""
            INSERT INTO cfg.CalculationBinding (TableDefId, ColumnDefId, MethodologyId, OutputCode, MatchJson, IsActive)
            SELECT TOP ({{ManyBindings}}) {{doc.TableDefId}}, {{doc.ColumnDefIds[0]}}, {{early}}, CONCAT(N'O', n), N'{}', 1
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;
            INSERT INTO cfg.CalculationBinding (TableDefId, ColumnDefId, MethodologyId, OutputCode, MatchJson, IsActive)
            VALUES ({{doc.TableDefId}}, {{doc.ColumnDefIds[1]}}, {{late}}, N'OUT', N'{}', 1);
            """);

        await using var db = builder.CreateContext();
        var pairs = await RecalculationJob.BindingPairsAsync(db, [doc.TableDefId], ct);

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.MethodologyId == early && p.TableDefId == doc.TableDefId);
        Assert.Contains(pairs, p => p.MethodologyId == late && p.TableDefId == doc.TableDefId);
    }

    private static string Methodology(string code) => $$"""
        INSERT INTO calc.Methodology (Code, NameL10n)
        VALUES (N'{{code}}', N'{"en":"{{code}}"}');
        SELECT CAST(SCOPE_IDENTITY() AS int);
        """;

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
