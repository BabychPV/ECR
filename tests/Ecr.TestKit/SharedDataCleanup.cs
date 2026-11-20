using Microsoft.Data.SqlClient;

namespace Ecr.TestKit;

/// <summary>
/// Прибирання великих заливок у СПІЛЬНІЙ базі колекції <c>SqlServer</c> — порціями з <c>CHECKPOINT</c>.
/// </summary>
/// <remarks>
/// ⛔ 20.11: повний прогін Infrastructure (і на <c>origin/main</c>, і на інтеграційній гілці) закінчувався «Test Collection
/// Cleanup Failure (SqlServer)»: файл <c>PRIMARY</c> спільної бази ріс до 232 МБ проти стелі 192 МБ
/// (<see cref="SqlServerFixture.MaxFileMbAtCollectionEnd"/>), і xUnit 2 записував цей виняток прибирання на КОЖЕН тест колекції
/// (3002-3042 тести по 1 мс). Файл росте на ПІК даних, а великі класи лишали свої рядки до кінця прогону: <c>ext.RawDataPoint</c>
/// 212 тис. рядків (6 розкладок <c>RawDataSeekTests</c> по 22 тис. + 2×40 тис. <c>RawDataPointReadersScanTests</c>) — 44 МБ,
/// з ними <c>itg.CollectionCoverage</c> — 17 МБ. Клас, що заливає багато, мусить прибрати за собою; видалені рядки звільняють
/// сторінки для наступних класів, а піку більше немає. Класи колекції виконуються послідовно, тож прибирання в
/// <c>DisposeAsync</c> класу не чіпає нічиїх живих даних.
/// </remarks>
public static class SharedDataCleanup
{
    /// <summary>Стеля порції видалення: журнал не росте від одного величезного DELETE.</summary>
    private const int ChunkRows = 10_000;

    /// <summary>
    /// Видаляє сирі точки (<c>ext.RawDataPoint</c>) і журнал покриття (<c>itg.CollectionCoverage</c>) заданих сутностей-джерел.
    /// </summary>
    /// <param name="connectionString">Рядок підключення до спільної тестової бази.</param>
    /// <param name="sourceEntityIds">Ідентифікатори сутностей-джерел, створених тестом.</param>
    /// <returns>Завдання.</returns>
    public static async Task DeleteRawPointsAndCoverageAsync(string connectionString, IReadOnlyCollection<int> sourceEntityIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(sourceEntityIds);
        if (sourceEntityIds.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = $"""
            DECLARE @ids TABLE (Id int NOT NULL PRIMARY KEY);
            INSERT @ids (Id) SELECT DISTINCT CAST(value AS int) FROM STRING_SPLIT(@list, N',') WHERE value <> N'';

            WHILE 1 = 1
            BEGIN
                DELETE TOP ({ChunkRows}) FROM ext.RawDataPoint WHERE SourceEntityId IN (SELECT Id FROM @ids);
                IF @@ROWCOUNT = 0 BREAK;
                CHECKPOINT;
            END;

            WHILE 1 = 1
            BEGIN
                DELETE TOP ({ChunkRows}) FROM itg.CollectionCoverage WHERE SourceEntityId IN (SELECT Id FROM @ids);
                IF @@ROWCOUNT = 0 BREAK;
                CHECKPOINT;
            END;
            """;
        command.Parameters.Add("@list", System.Data.SqlDbType.NVarChar, -1).Value = string.Join(',', sourceEntityIds);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
