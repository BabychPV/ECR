// src/Ecr.Infrastructure/Persistence/ColumnPathMapper.cs
using System.Data;
using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IColumnPathMapper"/> (C1): шлях «аркуш → таблиця → колонка» за кодами.</summary>
/// <remarks>
/// Два запити: шляхи вихідних колонок і колонки цільової версії з тими самими кодами. Якщо всі вихідні колонки вже
/// належать цільовій версії (документ на тій самій версії, що й правила), другий запит не потрібен — це найчастіший
/// випадок, і він не додає навантаження до гарячого читання зрізу.
/// </remarks>
public sealed class ColumnPathMapper(EcrDbContext db) : IColumnPathMapper
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> GetTemplateVersionsOfTablesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);

        if (tableDefIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var wanted = tableDefIds.Distinct().ToList();
        return await (
                from table in db.TableDefs.AsNoTracking()
                where wanted.Contains(table.Id)
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                select new { table.Id, sheet.TemplateVersionId })
            .ToDictionaryAsync(t => t.Id, t => t.TemplateVersionId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> MapToVersionAsync(
        IReadOnlyCollection<int> sourceColumnIds, int targetTemplateVersionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceColumnIds);

        if (sourceColumnIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var wanted = sourceColumnIds.Distinct().ToList();
        var sources = await (
                from column in db.ColumnDefs.AsNoTracking()
                where wanted.Contains(column.Id)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                select new { column.Id, sheet.TemplateVersionId, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<int, int>();

        // Колонка цільової версії відображається сама на себе (навіть вилучена: правило, що її згадує, — її власне).
        foreach (var own in sources.Where(s => s.TemplateVersionId == targetTemplateVersionId))
        {
            result[own.Id] = own.Id;
        }

        var foreign = sources.Where(s => s.TemplateVersionId != targetTemplateVersionId).ToList();
        if (foreign.Count == 0)
        {
            return result;
        }

        var columnCodes = foreign.Select(s => s.ColumnCode).Distinct().ToList();
        var targets = await (
                from column in db.ColumnDefs.AsNoTracking()
                where !column.IsDeleted && columnCodes.Contains(column.Code)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == targetTemplateVersionId && !table.IsDeleted && !sheet.IsDeleted
                select new { column.Id, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byPath = targets
            .GroupBy(t => (t.SheetCode, t.TableCode, t.ColumnCode))
            .ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var source in foreign)
        {
            if (byPath.TryGetValue((source.SheetCode, source.TableCode, source.ColumnCode), out var local))
            {
                result[source.Id] = local;
            }
        }

        return result;
    }

    /// <summary>Найбільше раундів розгортання ланцюжка перейменувань (A@v1 → B@v2 → C@v3 …).</summary>
    /// <remarks>Ланцюжок обмежений кількістю версій шаблону; межа — запобіжник від циклу в даних, а не бізнес-правило.</remarks>
    internal const int MaxLineageRounds = 8;

    /// <summary>
    /// V-4: ОДИН запит на раунд — шлях кожної названої колонки й Id того самого шляху в усіх версіях її шаблону,
    /// разом із джерелом перейменування (<c>cfg.ColumnRename</c>), що веде в будь-який із них.
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік Id — ОДИН JSON-параметр (<c>OPENJSON</c>), як <c>@visibleColumns</c> в <c>AuditReader</c>: видимих колонок
    /// документа Land тисячі, а окремий параметр на кожен Id упирається в стелю 2100 параметрів SQL Server.
    /// Усі з'єднання — пошуки за унікальними індексами: <c>UQ_SheetDef (TemplateVersionId, Code)</c>,
    /// <c>UQ_TableDef (SheetDefId, Code)</c>, <c>UQ_ColumnDef (TableDefId, Code)</c>, <c>IX_ColumnRename_To</c>,
    /// версії шаблону — за <c>(TemplateId, Version)</c>. Розмір виходу — колонки × версії шаблону.
    /// </remarks>
    private const string LineageSql = """
        SELECT c.Id AS SourceId, c2.Id AS LinkedId, r.FromColumnDefId AS RenamedFromId
          FROM OPENJSON(@ids) AS j
          JOIN cfg.ColumnDef AS c ON c.Id = CAST(j.[value] AS int)
          JOIN cfg.TableDef AS t ON t.Id = c.TableDefId
          JOIN cfg.SheetDef AS s ON s.Id = t.SheetDefId
          JOIN cfg.TemplateVersion AS v ON v.Id = s.TemplateVersionId
          JOIN cfg.TemplateVersion AS v2 ON v2.TemplateId = v.TemplateId
          JOIN cfg.SheetDef AS s2 ON s2.TemplateVersionId = v2.Id AND s2.Code = s.Code
          JOIN cfg.TableDef AS t2 ON t2.SheetDefId = s2.Id AND t2.Code = t.Code
          JOIN cfg.ColumnDef AS c2 ON c2.TableDefId = t2.Id AND c2.Code = c.Code
          LEFT JOIN cfg.ColumnRename AS r ON r.ToColumnDefId = c2.Id
        """;

    /// <inheritdoc />
    /// <remarks>
    /// Звичайний випадок (перейменувань немає) — один запит. Кожна пара перейменування додає раунд для джерела пари:
    /// його власний шлях у інших версіях і перейменування, що ведуть у нього.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> LineageAsync(
        IReadOnlyCollection<int> columnIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnIds);

        var result = new Dictionary<int, HashSet<int>>();
        foreach (var id in columnIds)
        {
            result.TryAdd(id, [id]);
        }

        // Межа розгортання: Id, які ще треба розгорнути, → колонки-запитувачі, до чиїх родоводів вони належать.
        var frontier = result.Keys.ToDictionary(id => id, id => new HashSet<int> { id });
        for (var round = 0; round < MaxLineageRounds && frontier.Count > 0; round++)
        {
            var json = "[" + string.Join(",", frontier.Keys.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
            var rows = await db.Database
                .SqlQueryRaw<LineageRow>(LineageSql, new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = json })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var next = new Dictionary<int, HashSet<int>>();
            foreach (var row in rows)
            {
                foreach (var owner in frontier[row.SourceId])
                {
                    result[owner].Add(row.LinkedId);
                    if (row.RenamedFromId is { } from && result[owner].Add(from))
                    {
                        if (!next.TryGetValue(from, out var owners))
                        {
                            next[from] = owners = [];
                        }

                        owners.Add(owner);
                    }
                }
            }

            frontier = next;
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<int>)[.. p.Value.Order()]);
    }

    /// <summary>
    /// ✎ B102: ОДИН запит на раунд — шлях «аркуш → таблиця» кожної названої таблиці й Id того самого шляху в усіх версіях
    /// її шаблону, разом із таблицями-джерелами пар <c>cfg.ColumnRename</c>, що ведуть у колонки будь-якої з них.
    /// </summary>
    /// <remarks>
    /// Те саме джерело ідентичності, що <see cref="LineageSql"/>: код аркуша + код таблиці в межах шаблону, а
    /// перейменування таблиці при переносі збережене лише як пари колонок (<c>VersionMigrationRenames</c> → <c>ColumnRename</c>),
    /// тож джерело — таблиця колонки <c>FromColumnDefId</c>. Пошуки за <c>UQ_SheetDef</c>, <c>UQ_TableDef</c>,
    /// <c>UQ_ColumnDef</c> (за <c>TableDefId</c>) і <c>IX_ColumnRename_To</c>.
    /// </remarks>
    private const string TableLineageSql = """
        SELECT t.Id AS SourceId, t2.Id AS LinkedId, rf.FromTableId AS RenamedFromId
          FROM OPENJSON(@ids) AS j
          JOIN cfg.TableDef AS t ON t.Id = CAST(j.[value] AS int)
          JOIN cfg.SheetDef AS s ON s.Id = t.SheetDefId
          JOIN cfg.TemplateVersion AS v ON v.Id = s.TemplateVersionId
          JOIN cfg.TemplateVersion AS v2 ON v2.TemplateId = v.TemplateId
          JOIN cfg.SheetDef AS s2 ON s2.TemplateVersionId = v2.Id AND s2.Code = s.Code
          JOIN cfg.TableDef AS t2 ON t2.SheetDefId = s2.Id AND t2.Code = t.Code
          OUTER APPLY (SELECT DISTINCT cf.TableDefId AS FromTableId
                         FROM cfg.ColumnDef AS c2
                         JOIN cfg.ColumnRename AS r ON r.ToColumnDefId = c2.Id
                         JOIN cfg.ColumnDef AS cf ON cf.Id = r.FromColumnDefId
                        WHERE c2.TableDefId = t2.Id AND cf.TableDefId <> t2.Id) AS rf
        """;

    /// <inheritdoc />
    /// <remarks>
    /// Як <see cref="LineageAsync"/>: без перейменувань — один запит; кожна таблиця-джерело перейменування додає раунд
    /// (її власний шлях у інших версіях і перейменування, що ведуть у неї).
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> TableLineageAsync(
        IReadOnlyCollection<int> tableIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableIds);

        var result = new Dictionary<int, HashSet<int>>();
        foreach (var id in tableIds)
        {
            result.TryAdd(id, [id]);
        }

        var frontier = result.Keys.ToDictionary(id => id, id => new HashSet<int> { id });
        for (var round = 0; round < MaxLineageRounds && frontier.Count > 0; round++)
        {
            var json = "[" + string.Join(",", frontier.Keys.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
            var rows = await db.Database
                .SqlQueryRaw<LineageRow>(TableLineageSql, new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = json })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var next = new Dictionary<int, HashSet<int>>();
            foreach (var row in rows)
            {
                foreach (var owner in frontier[row.SourceId])
                {
                    result[owner].Add(row.LinkedId);
                    if (row.RenamedFromId is { } from && result[owner].Add(from))
                    {
                        if (!next.TryGetValue(from, out var owners))
                        {
                            next[from] = owners = [];
                        }

                        owners.Add(owner);
                    }
                }
            }

            frontier = next;
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<int>)[.. p.Value.Order()]);
    }

    /// <summary>Рядок <see cref="LineageSql"/>; імена колонок — імена властивостей.</summary>
    /// <param name="SourceId">Названа (або розгортувана) колонка.</param>
    /// <param name="LinkedId">Колонка того самого шляху в одній з версій шаблону (включно з нею самою).</param>
    /// <param name="RenamedFromId">Джерело перейменування, що веде в <paramref name="LinkedId"/>; <c>null</c> — немає.</param>
    public sealed record LineageRow(int SourceId, int LinkedId, int? RenamedFromId);
}
