using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Визначає можливості СУБД при старті (АРХ-7).
/// </summary>
/// <remarks>
/// Редакція впливає **лише на операційні стратегії**, ніколи — на модель даних,
/// семантику чи числа. Тому результат використовують тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>; у бізнес-коді звертатися сюди
/// заборонено.
/// </remarks>
public sealed class SqlCapabilitiesProbe : ISqlCapabilities
{
    /// <summary>Розмір батча архівації: Standard.</summary>
    private const int StandardArchiveBatch = 500_000;

    /// <summary>Розмір батча архівації: Enterprise.</summary>
    private const int EnterpriseArchiveBatch = 2_000_000;

    /// <summary>Виконує запит і заповнює властивості.</summary>
    public async Task ProbeAsync(string connectionString, SqlEditionMode configuredMode, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // ⚠ RCSI читається з sys.databases, а НЕ через
        // DATABASEPROPERTYEX(DB_NAME(), 'IsReadCommittedSnapshotOn'), як
        // пропонував скелет: такої властивості не існує, вона повертає NULL, і
        // health-перевірка довіку повідомляла б, що RCSI вимкнено (`Q-052`).
        command.CommandText = """
            SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(200))   AS Edition,
                   CAST(SERVERPROPERTY('EngineEdition') AS int)       AS EngineEdition,
                   CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS MajorVersion,
                   (SELECT CAST(is_read_committed_snapshot_on AS int)
                    FROM sys.databases WHERE name = DB_NAME())        AS Rcsi;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Не вдалося прочитати властивості сервера.");
        }

        EditionName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var engineEdition = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        ProductMajorVersion = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        IsReadCommittedSnapshotOn = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;

        // ⚠ Developer і Evaluation повідомляють EngineEdition = 3 і зовні
        // невідрізнювані від Enterprise. Тому автовизначення допустиме лише в
        // dev: у проді режим фіксують явно, інакше система вибере стратегії,
        // яких ліцензія не дозволяє.
        EffectiveMode = configuredMode != SqlEditionMode.Auto
            ? configuredMode
            : engineEdition == 3 ? SqlEditionMode.Enterprise : SqlEditionMode.Standard;

        var enterprise = EffectiveMode == SqlEditionMode.Enterprise;
        SupportsOnlineIndexRebuild = enterprise;
        SupportsResourceGovernor = enterprise;
        ArchiveBatchSize = enterprise ? EnterpriseArchiveBatch : StandardArchiveBatch;
    }

    /// <summary>Що недоступне в поточному режимі — для <c>/health/db</c>.</summary>
    /// <remarks>
    /// Health, який каже лише «healthy», не пояснює адміністраторові, чому
    /// нічна операція поводиться інакше, ніж на тесті (АРХ-7 п. 5).
    /// </remarks>
    public IReadOnlyList<string> Limitations()
    {
        var list = new List<string>();
        if (!SupportsOnlineIndexRebuild)
        {
            list.Add("перебудова індексів потребує вікна обслуговування (ONLINE = ON недоступний)");
        }

        if (!SupportsResourceGovernor)
        {
            list.Add("фонові задачі не ізольовані від інтерактивного піку (немає Resource Governor)");
        }

        if (!IsReadCommittedSnapshotOn)
        {
            list.Add("RCSI вимкнено: читання блокуватимуть запис у пік останнього дня періоду");
        }

        list.Add(string.Create(CultureInfo.InvariantCulture, $"розмір батча архівації: {ArchiveBatchSize}"));
        return list;
    }

    public SqlEditionMode EffectiveMode { get; private set; }
    public string EditionName { get; private set; } = string.Empty;
    public int ProductMajorVersion { get; private set; }
    public bool IsReadCommittedSnapshotOn { get; private set; }
    public bool SupportsOnlineIndexRebuild { get; private set; }
    public bool SupportsResourceGovernor { get; private set; }
    public int ArchiveBatchSize { get; private set; }
}
