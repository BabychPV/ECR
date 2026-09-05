// src/Ecr.Infrastructure/Integration/IntegrationCellPatcher.cs
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Запис комірок від інтеграції через <b>спільний</b> обробник (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Тут немає жодного власного SQL і не має бути. Директива забороняє другий
/// шлях запису в комірки прямо, і причина названа: саме другий шлях дав
/// `A7-27` — мапа колонок будувалася там інакше, ніж на основному, і
/// адресація розійшлася. Ця реалізація лише **готує запит** і віддає його
/// тому самому <see cref="PatchCellsHandler"/>, яким пише людина.
///
/// ⚠ Комірки з правкою людини відсіюються ДО виклику, а не після: обробник не
/// знає про походження попереднього значення, і питати його про це означало б
/// навчити основний шлях правилам інтеграції.
/// </remarks>
public sealed class IntegrationCellPatcher(
    EcrDbContext db, PatchCellsHandler patch, IClock clock) : ICellPatcher
{
    /// <inheritdoc />
    public async Task<IntegrationWriteResult> ApplyIntegrationAsync(
        long documentId,
        long tableInstanceId,
        PeriodKey periodKey,
        IReadOnlyList<IntegrationCellValue> cells,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count == 0)
        {
            return new IntegrationWriteResult(0, []);
        }

        // ⚠ Коди колонок беруться з опису таблиці, а не з мапінгу: мапінг
        // зберігає `ColumnDefId`, а шлях запису адресує КОДОМ. Переклад робимо
        // тут і в межах ЦІЄЇ таблиці — саме звуження до таблиці й було
        // виправленням `A7-27`.
        var instance = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.Id == tableInstanceId && t.PeriodKeyValue == periodKey.Value)
            .Select(t => new { t.TableDefId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Екземпляра таблиці {tableInstanceId} за період {periodKey.Value} не існує.");

        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => c.TableDefId == instance.TableDefId && !c.IsDeleted)
            .Select(c => new { c.Id, c.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var codeById = columns.ToDictionary(c => c.Id, c => c.Code);

        // ⛔ Комірки, що їх правила людина, не чіпаємо (`D-118`). Ознака —
        // походження останньої зміни в журналі комірок: `UserEdit` означає
        // свідоме рішення, і інтеграція не має права його стерти.
        var manual = await ManualCellsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);

        var rows = new List<PatchRow>();
        var kept = new List<string>();
        var applied = 0;

        foreach (var group in cells.GroupBy(c => c.RowKey, StringComparer.Ordinal))
        {
            var patchCells = new List<PatchCell>();

            foreach (var cell in group)
            {
                if (!codeById.TryGetValue(cell.ColumnDefId, out var code))
                {
                    // Колонка не належить цій таблиці: мапінг налаштований на
                    // чужу таблицю. Це помилка конфігурації, і мовчати про неї
                    // не можна — але й падати посеред перенесення теж.
                    kept.Add($"{group.Key}:columnDef={cell.ColumnDefId}");
                    continue;
                }

                if (manual.Contains($"{group.Key}:{code}"))
                {
                    kept.Add($"{group.Key}:{code}");
                    continue;
                }

                patchCells.Add(new PatchCell(code, cell.Value));
                applied++;
            }

            if (patchCells.Count > 0)
            {
                // ⚠ `baseVersion = null` означає «створити рядок, якщо його
                // немає» (R-B2). Для інтеграції це правильно: рядок-адресат
                // описаний у шаблоні, і його поява — не конфлікт.
                rows.Add(new PatchRow(group.Key, BaseVersion: null, patchCells));
            }
        }

        if (rows.Count == 0)
        {
            return new IntegrationWriteResult(0, kept);
        }

        await patch
            .HandleAsync(
                new PatchCellsRequest(tableInstanceId, periodKey.Value, "Integration", rows),
                ct)
            .ConfigureAwait(false);

        return new IntegrationWriteResult(applied, kept);
    }

    /// <summary>Комірки, останню зміну яких зробила людина.</summary>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Сирий запит, а не LINQ: <c>aud.CellChange</c> — незмінний журнал, і
    /// він навмисно НЕ є сутністю EF. Дати йому <c>DbSet</c> означало б
    /// відкрити можливість писати в нього з коду застосунку, а «незмінний
    /// журнал» тримається саме на тому, що такої можливості немає (B01 §6.4).
    ///
    /// ⚠ Береться ОСТАННЯ зміна кожної комірки, а не будь-яка: комірку могли
    /// спершу заповнити руками, а потім свідомо віддати інтеграції.
    /// </remarks>
    private async Task<HashSet<string>> ManualCellsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var manual = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(
            db.Database.GetConnectionString());

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH last_change AS (
                SELECT c.RowKey, c.ColumnDefId, c.Origin,
                       ROW_NUMBER() OVER (PARTITION BY c.RowKey, c.ColumnDefId
                                              ORDER BY c.ChangedAt DESC, c.Id DESC) AS rn
                  FROM aud.CellChange AS c
                  JOIN doc.TableRow  AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
                 WHERE c.PeriodKey = @period AND r.TableInstanceId = @instance
            )
            SELECT lc.RowKey, cd.Code
              FROM last_change AS lc
              JOIN cfg.ColumnDef AS cd ON cd.Id = lc.ColumnDefId
             WHERE lc.rn = 1 AND lc.Origin = N'UserEdit';
            """;

        command.Parameters.AddWithValue("@period", periodKey.Value);
        command.Parameters.AddWithValue("@instance", tableInstanceId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            manual.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }

        return manual;
    }
}

/// <summary>Журнал покриття збору поверх <c>itg.CollectionCoverage</c>.</summary>
public sealed class CoverageJournal(EcrDbContext db, IClock clock) : ICoverageJournal
{
    /// <inheritdoc />
    public async Task RecordAsync(
        int sourceEntityId, PeriodKey periodKey, string status, string details, CancellationToken ct)
    {
        db.CollectionCoverages.Add(
            CollectionCoverage.Skipped(sourceEntityId, periodKey.Value, status, details, clock.UtcNow));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
