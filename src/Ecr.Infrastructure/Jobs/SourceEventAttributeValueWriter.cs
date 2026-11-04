// src/Ecr.Infrastructure/Jobs/SourceEventAttributeValueWriter.cs
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Що прочитано про подію зв'язку цим прогоном.</summary>
/// <param name="LinkId">Зв'язок (уже збережений).</param>
/// <param name="Event">Подія, як її дало джерело.</param>
public sealed record SourceEventReading(long LinkId, SourceEvent Event);

/// <summary>Підсумок запису значень.</summary>
/// <param name="Statements">Скільки команд виконано (сторінки + зняття).</param>
/// <param name="Changed">Скільки рядків вставлено, оновлено чи видалено.</param>
public sealed record SourceEventAttributeWriteResult(int Statements, int Changed);

/// <summary>
/// Запис значень атрибутів подій за останнім прочитанням (<c>ext.SourceEventAttributeValue</c>, <c>D-465</c>).
/// </summary>
/// <remarks>
/// ⛔ Set-based: ОДИН <c>MERGE</c> на сторінку зв'язків (<see cref="PageSize"/>) з JSON-параметром — кількість
/// звернень залежить від кількості сторінок, а не подій чи атрибутів. Пише лише ЗМІНЕНЕ: збіг
/// <see cref="SourceEventAttributeValue.ValueHash"/> — рядок не чіпається (і його <c>ChangedAt</c> лишається моментом,
/// коли значення вперше побачили), тож щопрогінне перечитування всього року не дає ні записів, ні росту журналу
/// транзакцій. Атрибут, якого джерело для зв'язку сторінки більше не повертає, знімається (<c>NOT MATCHED BY SOURCE</c>
/// у межах зв'язків сторінки).
/// </remarks>
public static class SourceEventAttributeValueWriter
{
    /// <summary>Зв'язків на одну команду.</summary>
    public const int PageSize = 500;

    /// <summary>Тайм-аут команди, с.</summary>
    public const int CommandTimeoutSeconds = 60;

    /// <summary>Найбільше за модулем число, що вміщується в <c>decimal(34,16)</c>; більше — лише текстом.</summary>
    private static readonly decimal MaxStorable = 999_999_999_999_999_999m;

    /// <summary>Бойовий текст сторінки: ціль обмежено зв'язками сторінки (seek за префіксом ключа).</summary>
    public const string MergeSql = """
        WITH target AS (
            SELECT v.LinkId, v.Scope, v.Attribute, v.ValueNumeric, v.ValueText, v.IsTruncated, v.SourceUnit,
                   v.ValueHash, v.SourceModifiedUtc, v.ChangedAt, v.RunId
              FROM ext.SourceEventAttributeValue AS v
             WHERE v.LinkId IN (SELECT l.id FROM OPENJSON(@links) WITH (id bigint '$') AS l)
        )
        MERGE target AS t
        USING (SELECT j.l, j.s, j.a, j.n, j.x, j.tr, j.u, CONVERT(binary(32), j.h, 1) AS h, j.m
                 FROM OPENJSON(@values) WITH (
                        l bigint '$.l', s tinyint '$.s', a nvarchar(200) '$.a', n decimal(34,16) '$.n',
                        x nvarchar(4000) '$.x', tr bit '$.tr', u nvarchar(64) '$.u', h varchar(66) '$.h',
                        m datetime2(3) '$.m') AS j) AS s
           ON t.LinkId = s.l AND t.Scope = s.s AND t.Attribute = s.a
        WHEN MATCHED AND t.ValueHash <> s.h THEN
            UPDATE SET ValueNumeric = s.n, ValueText = s.x, IsTruncated = s.tr, SourceUnit = s.u, ValueHash = s.h,
                       SourceModifiedUtc = s.m, ChangedAt = @at, RunId = @run
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (LinkId, Scope, Attribute, ValueNumeric, ValueText, IsTruncated, SourceUnit, ValueHash,
                    SourceModifiedUtc, ChangedAt, RunId)
            VALUES (s.l, s.s, s.a, s.n, s.x, s.tr, s.u, s.h, s.m, @at, @run)
        WHEN NOT MATCHED BY SOURCE THEN DELETE;
        """;

    /// <summary>Зняття значень зв'язків, яких більше немає.</summary>
    public const string DropSql = """
        DELETE FROM ext.SourceEventAttributeValue
         WHERE LinkId IN (SELECT l.id FROM OPENJSON(@links) WITH (id bigint '$') AS l);
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ✎ Аудит 31.10 (G7): знімає значення зв'язків, які прогін знімає без рядка, — у ПОТОЧНІЙ транзакції контексту
    /// (тій самій, що видаляє самі зв'язки), одна команда.
    /// </summary>
    /// <param name="db">Контекст задачі.</param>
    /// <param name="linkIds">Зв'язки.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки значень знято.</returns>
    public static async Task<int> DropAsync(EcrDbContext db, IReadOnlyCollection<long> linkIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(linkIds);

        var ids = linkIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        await using var command = await CommandAsync(db, DropSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@links", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(ids, Json);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Записує прочитане й знімає значення видалених зв'язків.</summary>
    /// <param name="db">Контекст задачі (його з'єднання).</param>
    /// <param name="readings">Зв'язки з подіями цього прогону; повтор зв'язку — береться останній.</param>
    /// <param name="droppedLinkIds">Зв'язки, зняті цим прогоном без рядка (їхні значення — сирітки).</param>
    /// <param name="runId">Прогін.</param>
    /// <param name="now">Момент прогону.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<SourceEventAttributeWriteResult> WriteAsync(
        EcrDbContext db,
        IReadOnlyList<SourceEventReading> readings,
        IReadOnlyCollection<long> droppedLinkIds,
        string runId,
        DateTime now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(droppedLinkIds);

        var statements = 0;
        var changed = 0;
        var byLink = new Dictionary<long, SourceEvent>();
        foreach (var reading in readings.Where(r => r.LinkId > 0))
        {
            byLink[reading.LinkId] = reading.Event;
        }

        var dropped = droppedLinkIds.Where(id => id > 0 && !byLink.ContainsKey(id)).Distinct().ToList();
        if (dropped.Count > 0)
        {
            await using var command = await CommandAsync(db, DropSql, ct).ConfigureAwait(false);
            command.Parameters.Add("@links", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(dropped, Json);
            changed += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            statements++;
        }

        foreach (var page in byLink.OrderBy(p => p.Key).Chunk(PageSize))
        {
            var values = new List<object>();
            foreach (var (linkId, ev) in page)
            {
                foreach (var row in Rows(ev))
                {
                    values.Add(new
                    {
                        l = linkId,
                        s = (byte)row.Scope,
                        a = row.Attribute,
                        n = row.Numeric?.ToString(CultureInfo.InvariantCulture),
                        x = row.Text,
                        tr = row.Truncated,
                        u = row.Unit,
                        h = "0x" + Convert.ToHexString(row.Hash),
                        m = ev.ModifiedUtc,
                    });
                }
            }

            await using var command = await CommandAsync(db, MergeSql, ct).ConfigureAwait(false);
            command.Parameters.Add("@links", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(page.Select(p => p.Key), Json);
            command.Parameters.Add("@values", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(values, Json);
            command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Scale = 3, Value = now });
            command.Parameters.Add("@run", SqlDbType.VarChar, 32).Value = runId;
            changed += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            statements++;
        }

        return new SourceEventAttributeWriteResult(statements, changed);
    }

    /// <summary>Рядок сховища одного атрибута.</summary>
    /// <param name="Scope">Область.</param>
    /// <param name="Attribute">Ім'я (до 200).</param>
    /// <param name="Numeric">Число, якщо вміщується в сховище.</param>
    /// <param name="Text">Текст (до 4000).</param>
    /// <param name="Truncated">Текст обрізано.</param>
    /// <param name="Unit">UOM джерела (до 64).</param>
    /// <param name="Hash">SHA-256 канонічного вигляду ПОВНОГО значення.</param>
    public sealed record StoredAttribute(
        Domain.Enums.SourceEventAttributeScope Scope,
        string Attribute,
        decimal? Numeric,
        string? Text,
        bool Truncated,
        string? Unit,
        byte[] Hash);

    /// <summary>
    /// Атрибути події у вигляді сховища: без порожніх імен, одне значення на «область + ім'я без регістру» (перше —
    /// як і <see cref="Application.Integration.SourceEvents.SourceEventRowBuilder"/> бере перше).
    /// </summary>
    /// <param name="ev">Подія.</param>
    public static IEnumerable<StoredAttribute> Rows(SourceEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var seen = new HashSet<(Domain.Enums.SourceEventAttributeScope, string)>();
        foreach (var attribute in ev.Attributes)
        {
            var name = attribute.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > SourceEventMap.MaxAttributeLength
                || !seen.Add((attribute.Scope, name.ToUpperInvariant())))
            {
                continue;
            }

            var numeric = attribute.ValueNumeric;
            var text = attribute.ValueString;

            // Число поза decimal(34,16) — текстом (інваріантно), не обрізаним числом.
            if (numeric is { } big && Math.Abs(big) > MaxStorable)
            {
                text ??= big.ToString(CultureInfo.InvariantCulture);
                numeric = null;
            }
            else if (numeric is { } n)
            {
                numeric = decimal.Round(n, 16, MidpointRounding.ToEven);
            }

            var truncated = text is { Length: > SourceEventAttributeValue.MaxTextLength };
            var unit = attribute.SourceUnitSymbol is { Length: > SourceEventAttributeValue.MaxUnitLength } u
                ? u[..SourceEventAttributeValue.MaxUnitLength]
                : attribute.SourceUnitSymbol;

            yield return new StoredAttribute(
                attribute.Scope,
                name,
                numeric,
                truncated ? text![..SourceEventAttributeValue.MaxTextLength] : text,
                truncated,
                unit,
                Hash(numeric, attribute.ValueString, attribute.SourceUnitSymbol));
        }
    }

    /// <summary>SHA-256 канонічного вигляду значення (число інваріантно без хвостових нулів, повний текст, UOM).</summary>
    public static byte[] Hash(decimal? numeric, string? text, string? unit)
    {
        var canonical = string.Join(
            '\u001f',
            numeric is { } n ? n.ToString("G29", CultureInfo.InvariantCulture) : "␀",
            text ?? "␀",
            unit ?? "␀");
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private static async Task<SqlCommand> CommandAsync(EcrDbContext db, string sql, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        if (db.Database.CurrentTransaction?.GetDbTransaction() is SqlTransaction transaction)
        {
            command.Transaction = transaction;
        }

        return command;
    }
}
