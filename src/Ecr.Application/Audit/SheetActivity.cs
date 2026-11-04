// src/Ecr.Application/Audit/SheetActivity.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Audit;

/// <summary>Рядок журналу дій на аркуші: правка комірки (людини), додавання/видалення рядка або дія робочого процесу.</summary>
/// <param name="Kind"><c>CellEdit</c>, <c>RowChange</c> або <c>Workflow</c> (<see cref="SheetActivityKinds"/>).</param>
/// <param name="At">Момент дії, UTC.</param>
/// <param name="ByDisplayName">Ім'я виконавця; <c>system</c> — системний перехід; зниклий користувач — <c>#id</c>.</param>
/// <param name="Origin">Правка й рядок: <c>UserEdit</c>, <c>Import</c>, <c>ImportOverwrite</c>, <c>ApproverEdit</c>.</param>
/// <param name="PeriodKey">Звітний період.</param>
/// <param name="RowKey">Правка й рядок: ключ рядка.</param>
/// <param name="RowLabelL10n">Лише правка: підпис фіксованого рядка; <c>null</c> — динамічний.</param>
/// <param name="ColumnDefId">Лише правка: колонка.</param>
/// <param name="ColumnCode">Лише правка: код колонки.</param>
/// <param name="ColumnHeaderL10n">Лише правка: заголовок колонки.</param>
/// <param name="ColumnDataType">Лише правка: тип колонки (для показу значення за правилом показу).</param>
/// <param name="TableCode">Правка й рядок: код таблиці.</param>
/// <param name="TableNameL10n">Правка й рядок: назва таблиці.</param>
/// <param name="OldValue">Лише правка: значення до.</param>
/// <param name="NewValue">Лише правка: значення після.</param>
/// <param name="Action">
/// Процес: <c>Submit</c>, <c>Approve</c>, <c>ApproveStep</c>, <c>Reject</c>, <c>Reopen</c>, <c>Recall</c>;
/// рядок: <c>RowAdded</c>, <c>RowDeleted</c>.
/// </param>
/// <param name="FromState">Лише процес: стан до.</param>
/// <param name="ToState">Лише процес: стан після.</param>
/// <param name="Reason">Лише процес: коментар (причина відхилення, повернення, відкликання); звичайний текст.</param>
/// <param name="ColumnScale">
/// Лише правка: масштаб колонки — значення показується з НЕ МЕНШЕ стількома знаками після коми, як у сітці (U-05);
/// <c>null</c> — масштабу немає.
/// </param>
public sealed record SheetActivityItem(
    string Kind,
    DateTime At,
    string ByDisplayName,
    string? Origin,
    int PeriodKey,
    string? RowKey,
    LocalizedText? RowLabelL10n,
    int? ColumnDefId,
    string? ColumnCode,
    LocalizedText? ColumnHeaderL10n,
    string? ColumnDataType,
    string? TableCode,
    LocalizedText? TableNameL10n,
    string? OldValue,
    string? NewValue,
    string? Action,
    string? FromState,
    string? ToState,
    string? Reason,
    int? ColumnScale = null);

/// <summary>Сторінка журналу дій аркуша.</summary>
/// <param name="Items">Рядки, найновіші перші.</param>
/// <param name="NextCursor">Непрозорий курсор наступної сторінки; <c>null</c> — далі нічого немає.</param>
public sealed record SheetActivityPage(IReadOnlyList<SheetActivityItem> Items, string? NextCursor);

/// <summary>Види рядків <see cref="SheetActivityItem.Kind"/>.</summary>
public static class SheetActivityKinds
{
    /// <summary>Правка значення комірки людиною (сітка, імпорт, правка погоджувача).</summary>
    public const string CellEdit = "CellEdit";

    /// <summary>Подання, погодження, відхилення, повернення в роботу, відкликання.</summary>
    public const string Workflow = "Workflow";

    /// <summary>Додавання або видалення рядка таблиці (<c>aud.RowChange</c>); дія — у <c>Action</c>.</summary>
    public const string RowChange = "RowChange";

    /// <summary><c>Action</c> рядка <see cref="RowChange"/>: рядок додано.</summary>
    public const string RowAdded = "RowAdded";

    /// <summary><c>Action</c> рядка <see cref="RowChange"/>: рядок видалено.</summary>
    public const string RowDeleted = "RowDeleted";

    /// <summary><c>Action</c> рядка <see cref="RowChange"/>: рядок відновлено (зарезервовано).</summary>
    public const string RowRestored = "RowRestored";
}

/// <summary>Вже авторизований запит до <see cref="SheetActivityQuery"/>: що саме читач має право бачити.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Звітний період.</param>
/// <param name="SheetCode">Код аркуша (журнал переходів знає код).</param>
/// <param name="VisibleColumnIds">Колонки аркуша, які читач бачить (<c>DocumentReadScope.VisibleColumnIdsOfSheet</c>).</param>
/// <param name="ByUserId">Лише дії цього користувача; <c>null</c> — усіх.</param>
/// <param name="IncludeSystem">
/// <c>true</c> - додатково системні зміни значень (перерахунок, інтеграція, значення з дозволу, міграція).
/// Класифікація - за <c>aud.CellChange.Origin</c>, спільним переліком <c>CellChangeOrigins.IsHuman</c>:
/// людина - <c>UserEdit</c>, <c>Import</c>, <c>ImportOverwrite</c>, <c>ApproverEdit</c> (імпорт - дія людини);
/// решта - система. Дії процесу (подання, погодження...) показуються завжди.
/// </param>
/// <param name="VisibleTableIds">
/// Таблиці аркуша, які читач бачить (<c>DocumentReadScope.VisibleTableIdsOfSheet</c>) — межі журналу рядків
/// (<c>aud.RowChange</c>); <c>null</c> або порожньо — рядків «додано/видалено» немає (закрито за замовчуванням).
/// </param>
public sealed record SheetActivityScope(
    long DocumentId, int PeriodKey, string SheetCode, IReadOnlyList<int> VisibleColumnIds, int? ByUserId = null,
    bool IncludeSystem = false, IReadOnlyList<int>? VisibleTableIds = null);

/// <summary>
/// Спільний шар читання журналу дій на аркуші: злиття трьох джерел в один потік, найновіші перші.
/// </summary>
/// <remarks>
/// ⛔ Тут НЕМАЄ перевірки доступу: вхід — <see cref="SheetActivityScope"/>, який складає обробник після права,
/// гранта і меж читання. Хто кличе цей шар (журнал аудиту, інші екрани), зобов'язаний так само: сам по собі він
/// відповідає на «що було на цьому аркуші», а не на «кому можна це показати».
///
/// <para>
/// Порядок — <c>(At спадно, тег джерела зростно, Id спадно)</c>; тег: <c>0</c> — правка комірки
/// (<c>aud.CellChange</c>), <c>1</c> — дія процесу (<c>wf.ApprovalEvent</c>), <c>2</c> — додавання/видалення рядка
/// (<c>aud.RowChange</c>). Кожне джерело читається ОДНИМ запитом з власним keyset (<c>TOP</c> за ключем порядку),
/// після чого сторінка береться злиттям: ≤ <c>3 × limit + 1</c> рядків у пам'яті, вартість не росте з обсягом журналу.
/// Курсор — позиція ОСТАННЬОГО виданого рядка <c>(At, тег, Id)</c>; кожне джерело виводить із неї свою
/// умову «строго після», тож рівні <c>At</c> не губляться і не дублюються на межі сторінок.
/// </para>
///
/// <para>
/// Межі: вікно правок — <see cref="CellWindow"/> від поточного моменту (партиції <c>aud.CellChange</c>
/// відсікаються лише вікном); журнал процесу — до <see cref="WorkflowCap"/> найновіших подій документа й періоду.
/// Старіше за вікно у списку не з'явиться — це журнал «що щойно відбувалось», не архів аудиту.
/// </para>
/// </remarks>
public sealed class SheetActivityQuery(
    IAuditReader audit, IWorkflowStore workflow, TimeProvider? clock = null, IRowChangeAudit? rowChanges = null)
{
    /// <summary>Скільки днів назад читаються правки комірок (як вікно історії однієї комірки).</summary>
    public static readonly TimeSpan CellWindow = TimeSpan.FromDays(396);

    /// <summary>Скільки найновіших подій процесу документа за період читається (далі подій на аркуш ще менше).</summary>
    public const int WorkflowCap = 500;

    /// <summary>Підпис виконавця для системного переходу.</summary>
    public const string SystemActor = "system";

    private const int CellTag = 0;
    private const int WorkflowTag = 1;
    private const int RowTag = 2;

    /// <summary>Найбільший тег джерела (межа декодування курсора).</summary>
    internal const int MaxTag = RowTag;

    /// <summary>Повертає сторінку журналу.</summary>
    /// <param name="scope">Авторизований запит.</param>
    /// <param name="limit">Розмір сторінки, 1..<see cref="CursorRequest.MaxLimit"/>.</param>
    /// <param name="cursor">Курсор попередньої сторінки; зіпсований — початок.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SheetActivityPage> ReadAsync(
        SheetActivityScope scope, int limit, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var after = ActivityCursor.Decode(cursor);

        var cells = await ReadCellsAsync(scope, limit, after, ct).ConfigureAwait(false);
        var events = await ReadWorkflowAsync(scope, after, ct).ConfigureAwait(false);
        var rows = await ReadRowsAsync(scope, limit, after, ct).ConfigureAwait(false);

        var merged = cells.Items.Concat(events).Concat(rows)
            .OrderByDescending(x => x.At)
            .ThenBy(x => x.Tag)
            .ThenByDescending(x => x.Id)
            .ToList();

        var page = merged.Take(limit).ToList();

        // Журнал рядків читається з `limit + 1`: зайвий рядок сам дає `merged.Count > limit`.
        var hasMore = merged.Count > limit || cells.HasMore;

        return new SheetActivityPage(
            [.. page.Select(x => x.Item)],
            hasMore && page.Count > 0 ? ActivityCursor.Encode(page[^1].At, page[^1].Tag, page[^1].Id) : null);
    }

    private async Task<(IReadOnlyList<Row> Items, bool HasMore)> ReadCellsAsync(
        SheetActivityScope scope, int limit, (DateTime At, int Tag, long Id)? after, CancellationToken ct)
    {
        if (scope.VisibleColumnIds.Count == 0)
        {
            return ([], false);
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var filter = new CellChangeFilter(
            now - CellWindow, now.AddDays(1), scope.DocumentId,
            ChangedByUserId: scope.ByUserId,
            VisibleColumnIds: scope.VisibleColumnIds,
            PeriodKey: scope.PeriodKey,
            HumanOnly: !scope.IncludeSystem,
            Sort: AuditSort.ChangedAtDesc);

        // Курсор читача журналу правок — пара (At, Id) «строго після» в порядку DESC. Після рядка процесу (тег 1)
        // правки того ж At вже видано (тег 0 раніше за тег 1), тож Id = 0 дає «At строго менший».
        var position = after is { } a
            ? ActivityCursor.EncodeCellPosition(a.At, a.Tag == CellTag ? a.Id : 0)
            : null;

        var result = await audit.ReadCellChangesAsync(filter, new CursorRequest(limit, position), ct).ConfigureAwait(false);

        var rows = result.Items
            .Select(c => new Row(c.ChangedAt, CellTag, c.Id, new SheetActivityItem(
                SheetActivityKinds.CellEdit, c.ChangedAt,
                !Documents.CellChangeOrigins.IsHuman(c.Origin)
                    ? SystemActor
                    : c.ChangedByDisplayName ?? "#" + c.ChangedByUserId.ToString(CultureInfo.InvariantCulture),
                c.Origin, c.PeriodKey, c.RowKey, c.RowLabelL10n, c.ColumnDefId, c.ColumnCode, c.ColumnHeaderL10n,
                c.ColumnDataType, c.TableCode, c.TableNameL10n, c.OldValue, c.NewValue,
                Action: null, FromState: null, ToState: null, Reason: null, ColumnScale: c.ColumnScale)))
            .ToList();

        return (rows, result.NextCursor is not null);
    }

    private async Task<IReadOnlyList<Row>> ReadWorkflowAsync(
        SheetActivityScope scope, (DateTime At, int Tag, long Id)? after, CancellationToken ct)
    {
        var events = await workflow.GetHistoryAsync(
            scope.DocumentId, new PeriodKey(scope.PeriodKey), WorkflowCap, ct).ConfigureAwait(false);

        return [.. events
            .Where(e => string.Equals(e.SheetCode, scope.SheetCode, StringComparison.Ordinal))
            .Where(e => scope.ByUserId is null || e.ByUserId == scope.ByUserId)
            .Where(e => after is not { } a
                        || e.At < a.At
                        || (e.At == a.At && (a.Tag == CellTag || (a.Tag == WorkflowTag && e.Id < a.Id))))
            .Select(e => new Row(e.At, WorkflowTag, e.Id, new SheetActivityItem(
                SheetActivityKinds.Workflow, e.At,
                e.ByUserId is null ? SystemActor : e.ByDisplayName ?? "#" + e.ByUserId.Value.ToString(CultureInfo.InvariantCulture),
                Origin: null, scope.PeriodKey, RowKey: null, RowLabelL10n: null, ColumnDefId: null, ColumnCode: null,
                ColumnHeaderL10n: null, ColumnDataType: null, TableCode: null, TableNameL10n: null, OldValue: null,
                NewValue: null, e.Action.ToString(), e.FromStatus.ToString(), e.ToStatus.ToString(), e.Reason)))];
    }

    /// <summary>
    /// Третє джерело (тег 2): додавання й видалення рядків (<c>aud.RowChange</c>) — лише таблиць, які читач бачить
    /// (<see cref="SheetActivityScope.VisibleTableIds"/>, у SQL до <c>TOP</c>).
    /// </summary>
    /// <remarks>
    /// Курсор: після рядка з тегом 0/1 того самого <c>At</c> рядки журналу рядків ще НЕ видано (тег 2 — останній),
    /// тож «строго після» = <c>(At, Id ≤ max)</c>; після тегу 2 — звичайне <c>(At, Id)</c>.
    /// </remarks>
    private async Task<IReadOnlyList<Row>> ReadRowsAsync(
        SheetActivityScope scope, int limit, (DateTime At, int Tag, long Id)? after, CancellationToken ct)
    {
        if (rowChanges is null || scope.VisibleTableIds is not { Count: > 0 } tables)
        {
            return [];
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        (DateTime, long)? position = after is { } a ? (a.At, a.Tag == RowTag ? a.Id : long.MaxValue) : null;

        var items = await rowChanges
            .ReadRowChangesAsync(
                new RowChangeFilter(
                    now - CellWindow, now.AddDays(1), scope.DocumentId, scope.PeriodKey, tables, scope.ByUserId,
                    HumanOnly: !scope.IncludeSystem),
                limit + 1,
                position,
                ct)
            .ConfigureAwait(false);

        return [.. items.Select(r => new Row(r.ChangedAt, RowTag, r.Id, new SheetActivityItem(
            SheetActivityKinds.RowChange, r.ChangedAt,
            !Documents.CellChangeOrigins.IsHuman(r.Origin)
                ? SystemActor
                : r.ChangedByDisplayName ?? "#" + r.ChangedByUserId.ToString(CultureInfo.InvariantCulture),
            r.Origin, r.PeriodKey, r.RowKey, RowLabelL10n: null, ColumnDefId: null, ColumnCode: null,
            ColumnHeaderL10n: null, ColumnDataType: null, r.TableCode, r.TableNameL10n, OldValue: null, NewValue: null,
            Action: r.Action switch
            {
                RowChangeAction.Added => SheetActivityKinds.RowAdded,
                RowChangeAction.Deleted => SheetActivityKinds.RowDeleted,
                _ => SheetActivityKinds.RowRestored,
            },
            FromState: null, ToState: null, Reason: null)))];
    }

    private sealed record Row(DateTime At, int Tag, long Id, SheetActivityItem Item);
}

/// <summary>Курсор журналу аркуша: позиція <c>(At, тег джерела, Id)</c> останнього виданого рядка.</summary>
public static class ActivityCursor
{
    /// <summary>Кодує позицію.</summary>
    public static string Encode(DateTime at, int tag, long id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            at.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + tag.ToString(CultureInfo.InvariantCulture)
            + "/" + id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Декодує; зіпсований курсор — <c>null</c> (початок).</summary>
    public static (DateTime At, int Tag, long Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 80)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[64];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return null;
        }

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split('/');
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tag)
            || tag > SheetActivityQuery.MaxTag
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return (new DateTime(ticks, DateTimeKind.Utc), tag, id);
    }

    /// <summary>
    /// Курсор читача <c>aud.CellChange</c> порядку «найновіші першими» (<see cref="AuditCursorCodec"/>): позиція
    /// <c>(At, Id)</c> без автора. Збіг формату з читачем стереже тест.
    /// </summary>
    public static string EncodeCellPosition(DateTime at, long id)
        => AuditCursorCodec.Encode(AuditSort.ChangedAtDesc, at, id, 0, null);
}

