using System.Data;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Запис аудиту в схему <c>aud</c>.
/// </summary>
/// <remarks>
/// ⚠ Пакетно **навмисно**: окремий <c>INSERT</c> на кожну комірку не
/// вкладається в бюджет збереження діапазону (300 мс на 100 комірок). Один
/// багаторядковий <c>INSERT</c> на весь батч — це один похід до сервера
/// замість ста.
///
/// Аудит пишеться в **тій самій транзакції**, що й дані: журнал, який може
/// розійтися з тим, що він описує, доказом не є.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1 (`Q-050`); таблиці `aud.*`
/// створює `11-audit-tables.sql` (`Q-049`).
/// </remarks>
public sealed class AuditWriter(EcrDbContext db) : IAuditWriter
{
    /// <summary>Масштаб усіх стовпців <c>ChangedAt</c> — <c>datetime2(3)</c>.</summary>
    /// <remarks>
    /// ⛔ <c>WR-01</c>. <c>AddWithValue</c> із <c>DateTime</c> дає
    /// <c>SqlDbType.DateTime</c> — СТАРИЙ тип із роздільністю 1/300 секунди,
    /// тоді як стовпці <c>aud.*</c> оголошені <c>datetime2(3)</c>
    /// (<c>11-audit-tables.sql:44</c>). Тобто момент зміни їхав на сервер
    /// округленим до ~3.3 мс і лише там переводився в <c>datetime2</c>. Для
    /// таблиці, розділеної ПО ЦЬОМУ СТОВПЦЮ, це ще й зайва неявна конверсія в
    /// кожному запиті.
    /// </remarks>
    private const byte TimestampScale = 3;

    /// <summary>Довжини рядкових стовпців <c>aud.*</c> (<c>11-audit-tables.sql</c>).</summary>
    /// <remarks>
    /// ⛔ <c>WR-01</c>, головна причина рядка плану на стороні аудиту.
    /// <c>AddWithValue</c> для <c>string</c> оголошує параметр завдовжки як
    /// САМЕ ЗНАЧЕННЯ: <c>@k0 nvarchar(4)</c> для <c>"R123"</c> і
    /// <c>@k0 nvarchar(5)</c> для <c>"R1234"</c>. На батчі зі 150 рядків це
    /// добуток довжин трьох стовпців — тобто практично унікальна сигнатура на
    /// кожен <c>PATCH</c>, і окремий план на кожну.
    ///
    /// ⚠ Числа — зі скрипта, який створює таблиці, а не з голови: <c>aud.*</c>
    /// не мають доменних сутностей і міграція EF їх не створює
    /// (<c>Q-049</c>), тож «подивитися в конфігурацію EF» тут неможливо в
    /// принципі.
    /// </remarks>
    private const int RowKeyLength = 100;

    /// <summary><c>aud.CellChange.Origin</c> — <c>nvarchar(32)</c>.</summary>
    private const int OriginLength = 32;

    /// <summary><c>CorrelationId</c> в усіх таблицях <c>aud.*</c> — <c>nvarchar(64)</c>.</summary>
    private const int CorrelationIdLength = 64;

    /// <summary><c>EntityType</c>/<c>EventType</c> — <c>nvarchar(64)</c>.</summary>
    private const int EntityTypeLength = 64;

    /// <summary><c>Operation</c> — <c>nvarchar(32)</c>.</summary>
    private const int OperationLength = 32;

    /// <summary>
    /// Рядкові значення без власної стелі — <c>nvarchar(max)</c> у параметрі.
    /// </summary>
    /// <remarks>
    /// ⛔ Сюди йдуть <c>OldValue</c>/<c>NewValue</c> (стовпці
    /// <c>nvarchar(1000)</c>), <c>ChangeReason</c>, <c>*Json</c>. Стелю
    /// стовпця в параметрі тут ставити НЕ МОЖНА: <c>SqlParameter</c> із
    /// заданим <c>Size</c> ріже довше значення на клієнті, і журнал аудиту
    /// тихо зберігав би огризок замість того, що насправді записали. Це
    /// дослівно <c>DAT-03</c> (<c>NormalizedCellStore</c>, коментар до
    /// <c>AddNullable(string)</c>), і ціна помилки в журналі вища: комірку
    /// можна перечитати, а неправдивий рядок аудиту нічим не спростуєш.
    ///
    /// ⚠ Для <c>WR-01</c> цього досить: <c>-1</c> — сигнатура СТАЛА
    /// (<c>nvarchar(max)</c>) на будь-якому значенні, а саме сталості план і
    /// потребує.
    /// </remarks>
    private const int UnboundedLength = -1;

    /// <summary>Ім'я табличного типу журналу комірок.</summary>
    private const string CellChangeTvpTypeName = "aud.CellChangeTvp";

    /// <summary>
    /// Форма <c>aud.CellChangeTvp</c> — колонка в колонку зі скриптом
    /// <c>15-cell-tvp.sql</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>WR-02</c>/<c>WR-08</c>. Типи збігаються зі стовпцями
    /// <c>aud.CellChange</c> (<c>11-audit-tables.sql</c>) точно — інакше
    /// SQL Server додав би неявну конверсію на КОЖЕН рядок журналу, а для
    /// <c>ChangedAt</c> це ще й партиційний стовпець.
    ///
    /// ⛔ Два винятки: <c>OldValue</c>/<c>NewValue</c> оголошені
    /// <c>nvarchar(max)</c>, хоча стовпці — <c>nvarchar(1000)</c>. Причина та
    /// сама, що вела до <see cref="UnboundedLength"/>: описувач із оголошеною
    /// довжиною ОБРІЗАЄ довше значення на клієнті (<c>SqlMetaData.Adjust</c>),
    /// і журнал тихо зберігав би огризок замість того, що насправді записали.
    /// Стовпець лишається <c>nvarchar(1000)</c> — задовге значення валить батч
    /// на сервері (2628), тобто гучно.
    ///
    /// ⚠ <c>RowKey</c>/<c>Origin</c>/<c>CorrelationId</c>, навпаки, оголошені
    /// ТОЧНО: їхню межу стереже <see cref="EnsureFits"/> ще до того, як
    /// значення дійде до <see cref="SqlDataRecord"/>.
    /// </remarks>
    private static readonly SqlMetaData[] CellChangeTvpShape =
    [
        new("ChangedAt", SqlDbType.DateTime2, 0, TimestampScale),
        new("PeriodKey", SqlDbType.Int),
        new("DocumentId", SqlDbType.BigInt),
        new("TableRowId", SqlDbType.BigInt),
        new("RowKey", SqlDbType.NVarChar, RowKeyLength),
        new("ColumnDefId", SqlDbType.Int),
        new("OldValue", SqlDbType.NVarChar, SqlMetaData.Max),
        new("NewValue", SqlDbType.NVarChar, SqlMetaData.Max),
        new("ChangedByUserId", SqlDbType.Int),
        new("Origin", SqlDbType.NVarChar, OriginLength),
        new("IsLateEdit", SqlDbType.Bit),
        new("CorrelationId", SqlDbType.NVarChar, CorrelationIdLength),
    ];

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ <c>WR-02</c> (і та частина <c>WR-08</c>, що каже «запис через TVP у
    /// тій самій транзакції»). Було: чанк 150 рядків, 12 параметрів на рядок,
    /// тобто вставка 500×60 = 30 000 комірок давала <b>200 послідовних</b>
    /// <c>INSERT</c> — і кожен розмір чанка компілював власний план, бо список
    /// <c>VALUES</c> входить у ТЕКСТ запиту. Стало: одна команда, один
    /// параметр, один план на будь-який розмір батчу.
    ///
    /// ⚠ Транзакція не змінилася ні на крок: команда, як і раніше, бере
    /// поточну транзакцію контексту (<see cref="CreateCommand"/>), тож журнал
    /// і дані комітяться разом. Це не оптимізація «заразом», а умова
    /// <c>WR-08</c>.
    ///
    /// ⚠ <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c> на <c>PK_CellChange</c> —
    /// СВІДОМО не тут: це друга половина <c>WR-08</c>, окрема зміна схеми,
    /// і міряється вона після <c>DAT-02</c>, коли обсяг аудиту перерахунку
    /// впаде сам.
    /// </remarks>
    public async Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        // ⛔ Перевірка довжин — ДО відправки, а не всередині послідовності.
        // SqlClient перебирає рядки вже під час передавання команди: виняток
        // звідти застав би її напіввідправленою, і замість зрозумілого
        // ArgumentException викликач дістав би зламане з'єднання.
        foreach (var change in changes)
        {
            EnsureFits(nameof(CellChangeRecord.RowKey), change.RowKey, RowKeyLength);
            EnsureFits(nameof(CellChangeRecord.Origin), change.Origin, OriginLength);
            EnsureFits(nameof(CellChangeRecord.CorrelationId), change.CorrelationId, CorrelationIdLength);
        }

        await using var command = CreateCommand();

        var rows = command.Parameters.Add("@changes", SqlDbType.Structured);
        rows.TypeName = CellChangeTvpTypeName;
        rows.Value = ToRecords(changes);

        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit, CorrelationId)
            SELECT ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                   OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit, CorrelationId
            FROM @changes;
            """;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Перекладає журнал у рядки табличного параметра.</summary>
    /// <param name="changes">Записи журналу; порожнім не буває.</param>
    /// <returns>Лінива послідовність рядків <c>aud.CellChangeTvp</c>.</returns>
    /// <remarks>
    /// ⚠ Один <see cref="SqlDataRecord"/> на всю послідовність — документована
    /// форма: провайдер читає рядок повністю, перш ніж попросити наступний.
    ///
    /// ⛔ Кожна колонка присвоюється на КОЖНОМУ рядку, навіть порожня. Інакше в
    /// буфері лишилося б значення ПОПЕРЕДНЬОГО запису, і в журналі з'явився б
    /// рядок, який стверджує чужу зміну — найдорожчий різновид неправди, бо
    /// журнал нічим не спростуєш.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> ToRecords(IReadOnlyList<CellChangeRecord> changes)
    {
        var row = new SqlDataRecord(CellChangeTvpShape);

        foreach (var c in changes)
        {
            row.SetDateTime(0, c.ChangedAt);
            row.SetInt32(1, c.Address.PeriodKey.Value);
            row.SetInt64(2, c.DocumentId);
            row.SetInt64(3, c.Address.TableRowId);
            SetText(row, 4, c.RowKey);
            row.SetInt32(5, c.Address.ColumnDefId);
            SetText(row, 6, c.OldValue);
            SetText(row, 7, c.NewValue);
            row.SetInt32(8, c.ChangedByUserId);
            SetText(row, 9, c.Origin);
            row.SetBoolean(10, c.IsLateEdit);
            SetText(row, 11, c.CorrelationId);

            yield return row;
        }
    }

    /// <summary>Рядкове поле табличного параметра або <c>NULL</c>.</summary>
    /// <param name="row">Рядок табличного параметра.</param>
    /// <param name="ordinal">Номер колонки.</param>
    /// <param name="value">Значення або <c>null</c>.</param>
    private static void SetText(SqlDataRecord row, int ordinal, string? value)
    {
        if (value is null)
        {
            row.SetDBNull(ordinal);
        }
        else
        {
            row.SetString(ordinal, value);
        }
    }

    /// <summary>Кидає, якщо значення довше за стовпець.</summary>
    /// <param name="name">Ім'я поля — для тексту відмови.</param>
    /// <param name="value">Значення.</param>
    /// <param name="max">Довжина стовпця.</param>
    /// <exception cref="ArgumentException">Значення довше за стовпець.</exception>
    /// <remarks>
    /// ⛔ Та сама перевірка, що стояла в <see cref="AddText"/>, і причина її та
    /// сама — лише механізм обрізання змінився з <c>SqlParameter.Size</c> на
    /// <see cref="SqlMetaData"/>. <c>RowKey</c> на 101 символ ліг би в журнал
    /// огризком на 100, і рядок аудиту показував би на комірку, якої немає.
    ///
    /// ⚠ Сьогодні жоден викликач до межі не доходить (<c>RowKey</c> береться з
    /// <c>doc.TableRow.RowKey</c>, теж <c>nvarchar(100)</c>; <c>Origin</c> — із
    /// замкненого переліку). Перевірка стереже ЗАВТРАШНЬОГО викликача, який
    /// про цю відповідність не знатиме.
    /// </remarks>
    private static void EnsureFits(string name, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            throw new ArgumentException(
                $"Значення {name} довше за стовпець: {value.Length} символів проти {max}.",
                nameof(value));
        }
    }

    /// <inheritdoc />
    public async Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO aud.StructureChange
                (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation,
                 OldJson, NewJson, ChangeReason, ChangedByUserId, CorrelationId)
            VALUES (@t, @v, @et, @ei, @cc, @op, @oj, @nj, @cr, @u, @x);
            """;
        AddTimestamp(command, "@t", change.ChangedAt);
        AddInt32(command, "@v", change.TemplateVersionId);
        AddText(command, "@et", change.EntityType, EntityTypeLength);
        AddInt32(command, "@ei", change.EntityId);
        command.Parameters.Add("@cc", SqlDbType.TinyInt).Value = (byte)change.ChangeClass;
        AddText(command, "@op", change.Operation, OperationLength);
        AddText(command, "@oj", change.OldJson, UnboundedLength);
        AddText(command, "@nj", change.NewJson, UnboundedLength);
        AddText(command, "@cr", change.ChangeReason, UnboundedLength);
        AddInt32(command, "@u", change.ChangedByUserId);
        AddText(command, "@x", change.CorrelationId, CorrelationIdLength);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO aud.SecurityEvent
                (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
            VALUES (@t, @e, @tu, @tr, @d, @u, @x);
            """;
        AddTimestamp(command, "@t", evt.ChangedAt);
        AddText(command, "@e", evt.EventType, EntityTypeLength);
        AddNullableInt32(command, "@tu", evt.TargetUserId);
        AddNullableInt32(command, "@tr", evt.TargetRoleId);
        AddText(command, "@d", evt.DetailsJson, UnboundedLength);
        AddInt32(command, "@u", evt.ChangedByUserId);
        AddText(command, "@x", evt.CorrelationId, CorrelationIdLength);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Максимум рядків за один <c>INSERT</c> подій безпеки.
    /// </summary>
    /// <remarks>
    /// ⚠ На відміну від <see cref="WriteCellChangesAsync"/>, тут немає TVP
    /// (<c>aud.SecurityEventTvp</c>): це вимагало б нового типу в схемі, а
    /// пакетний запис для цього завдання — форма виклику, не нова сутність
    /// схеми. Замість табличного параметра — один багаторядковий <c>INSERT</c>
    /// із текстом запиту, що росте з розміром батчу (сигнатура НЕ стала, на
    /// відміну від TVP-шляху) — прийнятно, бо на відміну від комірок (сотні за
    /// збереження діапазону) подій безпеки за один імпорт довідника — одиниці
    /// й десятки, не сотні. Ліміт SQL Server на параметри команди — 2100; при
    /// 7 параметрах на рядок це ~300 рядків, тож межа взята з запасом і батч
    /// рубається на кілька послідовних команд, якщо рядків більше.
    /// </remarks>
    private const int MaxSecurityEventsPerBatch = 250;

    /// <inheritdoc />
    public async Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        for (var offset = 0; offset < events.Count; offset += MaxSecurityEventsPerBatch)
        {
            var count = Math.Min(MaxSecurityEventsPerBatch, events.Count - offset);
            await WriteSecurityEventsChunkAsync(events, offset, count, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Один похід до сервера для до <see cref="MaxSecurityEventsPerBatch"/> подій.</summary>
    private async Task WriteSecurityEventsChunkAsync(
        IReadOnlyList<SecurityEventRecord> events, int offset, int count, CancellationToken ct)
    {
        await using var command = CreateCommand();
        var values = new string[count];

        for (var i = 0; i < count; i++)
        {
            var evt = events[offset + i];
            var t = $"@t{i}";
            var e = $"@e{i}";
            var tu = $"@tu{i}";
            var tr = $"@tr{i}";
            var d = $"@d{i}";
            var u = $"@u{i}";
            var x = $"@x{i}";

            AddTimestamp(command, t, evt.ChangedAt);
            AddText(command, e, evt.EventType, EntityTypeLength);
            AddNullableInt32(command, tu, evt.TargetUserId);
            AddNullableInt32(command, tr, evt.TargetRoleId);
            AddText(command, d, evt.DetailsJson, UnboundedLength);
            AddInt32(command, u, evt.ChangedByUserId);
            AddText(command, x, evt.CorrelationId, CorrelationIdLength);

            values[i] = $"({t}, {e}, {tu}, {tr}, {d}, {u}, {x})";
        }

        command.CommandText = $"""
            INSERT INTO aud.SecurityEvent
                (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
            VALUES {string.Join(",\n", values)};
            """;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO aud.PublicationEvent
                (ChangedAt, EntityType, EntityId, ResultDiffJson, ChangeReason, ChangedByUserId)
            VALUES (@t, @et, @ei, @rd, @cr, @u);
            """;
        AddTimestamp(command, "@t", evt.ChangedAt);
        AddText(command, "@et", evt.EntityType, EntityTypeLength);
        AddInt32(command, "@ei", evt.EntityId);
        AddText(command, "@rd", evt.ResultDiffJson, UnboundedLength);
        AddText(command, "@cr", evt.ChangeReason, UnboundedLength);
        AddInt32(command, "@u", evt.ChangedByUserId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Команда на з'єднанні контексту, у поточній транзакції.</summary>
    private SqlCommand CreateCommand()
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } tx)
        {
            command.Transaction = (SqlTransaction)tx.GetDbTransaction();
        }

        return command;
    }

    /// <summary>Рядковий параметр сталої сигнатури.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення; <c>null</c> → <c>DBNull</c>.</param>
    /// <param name="size">
    /// Довжина стовпця або <see cref="UnboundedLength"/> для значень без стелі.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Значення довше за оголошений стовпець.
    /// </exception>
    /// <remarks>
    /// ⛔ Перевірка довжини тут ОБОВ'ЯЗКОВА, і без неї заміна
    /// <c>AddWithValue</c> на типізований параметр була б не оптимізацією, а
    /// новим дефектом: <c>SqlParameter</c> із заданим <c>Size</c> ріже довше
    /// значення НА КЛІЄНТІ, тобто <c>RowKey</c> на 101 символ ліг би в журнал
    /// огризком на 100 — і рядок аудиту показував би на комірку, якої немає.
    /// <c>AddWithValue</c> цього не робив (довжина параметра дорівнювала
    /// довжині значення, і задовге падало на сервері помилкою 2628), тож без
    /// цієї перевірки зміна ПОГІРШИЛА б поведінку. Той самий урок, що
    /// <c>DAT-03</c>, і саме тому він тут повторений, а не мається на увазі.
    ///
    /// ⚠ Сьогодні жоден викликач до межі не доходить (<c>RowKey</c> береться з
    /// <c>doc.TableRow.RowKey</c>, теж <c>nvarchar(100)</c>; <c>Origin</c> —
    /// із замкненого переліку). Перевірка на це й не розрахована: вона
    /// стереже ЗАВТРАШНЬОГО викликача, який про цю відповідність не знатиме.
    /// </remarks>
    private static void AddText(SqlCommand command, string name, string? value, int size)
    {
        if (size != UnboundedLength && value is not null && value.Length > size)
        {
            throw new ArgumentException(
                $"Значення параметра {name} довше за стовпець: {value.Length} символів проти {size}.",
                nameof(value));
        }

        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;
    }

    /// <summary><c>datetime2(3)</c> — тип і масштаб стовпців <c>ChangedAt</c>.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Момент зміни.</param>
    private static void AddTimestamp(SqlCommand command, string name, DateTime value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = TimestampScale;
        parameter.Value = value;
    }

    /// <summary><c>int</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення.</param>
    private static void AddInt32(SqlCommand command, string name, int value)
        => command.Parameters.Add(name, SqlDbType.Int).Value = value;

    /// <summary><c>int NULL</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення або <c>null</c>.</param>
    private static void AddNullableInt32(SqlCommand command, string name, int? value)
        => command.Parameters.Add(name, SqlDbType.Int).Value = value.HasValue ? value.Value : DBNull.Value;

}
