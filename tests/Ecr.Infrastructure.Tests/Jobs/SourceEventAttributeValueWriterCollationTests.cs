// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventAttributeValueWriterCollationTests.cs
using System.Data;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-4 (аудит 2026-11, 02-data-layer): ключ дедуплікації значень атрибутів — рівність КОЛАЦІЇ БАЗИ, як у PK цілі.
/// </summary>
/// <remarks>
/// ⛔ C# відсіював повтори за <c>ToUpperInvariant</c>, а MERGE зіставляє в колації бази
/// (<c>Latin1_General_100_CI_AS_SC</c> нечутлива до ширини): <c>Flow</c> і <c>Ｆｌｏｗ</c> для C# різні, для бази — один
/// ключ. Обидва потрапляли в <c>@values</c>, і MERGE падав (8672/2627) на всю сторінку з 500 зв'язків — щопрогону.
/// Доказ: прибрати <c>ROW_NUMBER() … WHERE d.rn = 1</c> у <see cref="SourceEventAttributeValueWriter.MergeSql"/> —
/// тест червоніє <see cref="SqlException"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceEventAttributeValueWriterCollationTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 2, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-4")]
    public async Task Attributes_equal_in_database_collation_keep_first_and_do_not_fail()
    {
        // Зв'язок без рядка ext.SourceEventLink (FK немає) — діапазон, якого не займають інші тести.
        var linkId = 9_300_000_000L + (Random.Shared.NextInt64(1, 1_000_000) * 10);
        try
        {
            var reading = new SourceEventReading(
                linkId,
                new SourceEvent(
                    $"E{linkId}", "FlareEvent", "Flaring", At, At.AddHours(1), At, null, null,
                    [
                        new SourceEventAttribute("Flow", SourceEventAttributeScope.Event, 1m, null, "Sm3"),
                        new SourceEventAttribute("Ｆｌｏｗ", SourceEventAttributeScope.Event, 2m, null, "Sm3"),
                    ]));

            // ⚠ Передумова: для C# це ДВА різні ключі — інакше тест не перевіряв би розбіжність.
            Assert.Equal(2, SourceEventAttributeValueWriter.Rows(reading.Event).Count());

            await using (var db = sql.CreateContext())
            {
                var result = await SourceEventAttributeValueWriter.WriteAsync(
                    db, [reading], [], Guid.NewGuid().ToString("N"), At, CancellationToken.None);
                Assert.Equal(1, result.Changed);
            }

            var stored = await StoredAsync(linkId);
            var row = Assert.Single(stored);
            Assert.Equal("Flow", row.Attribute);
            Assert.Equal(1m, row.Numeric);
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM ext.SourceEventAttributeValue WHERE LinkId = @link;";
            cleanup.Parameters.Add("@link", SqlDbType.BigInt).Value = linkId;
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private async Task<List<(string Attribute, decimal? Numeric)>> StoredAsync(long linkId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Attribute, ValueNumeric FROM ext.SourceEventAttributeValue WHERE LinkId = @link;";
        command.Parameters.Add("@link", SqlDbType.BigInt).Value = linkId;
        var result = new List<(string, decimal?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1)));
        }

        return result;
    }
}
