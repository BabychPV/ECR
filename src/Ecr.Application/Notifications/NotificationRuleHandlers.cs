// src/Ecr.Application/Notifications/NotificationRuleHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Errors;

namespace Ecr.Application.Notifications;

/// <summary>Клітинка матриці «подія × канал».</summary>
/// <param name="EventKind">Подія.</param>
/// <param name="ChannelId">Канал.</param>
/// <param name="MinSeverity">Межа серйозності: правило пропускає події не нижчі за неї.</param>
/// <param name="IsEnabled">Чи діє правило.</param>
public sealed record NotificationRuleView(
    NotificationEventKind EventKind, int ChannelId, NotificationSeverity MinSeverity, bool IsEnabled);

/// <summary>Матриця правил цілком.</summary>
/// <param name="EventKinds">
/// УСІ види подій, а не лише ті, на які правило вже є: інакше клієнт не мав би
/// з чого намалювати порожню клітинку, і «правила немає» виглядало б як
/// «події не існує».
/// </param>
/// <param name="Rules">Заповнені клітинки.</param>
public sealed record NotificationRuleMatrix(
    IReadOnlyList<NotificationEventKind> EventKinds, IReadOnlyList<NotificationRuleView> Rules);

/// <summary>Запис журналу доставок — рядок екрана.</summary>
/// <remarks>⛔ Ні секрету каналу, ні тіла повідомлення тут немає й не буде.</remarks>
/// <param name="Id">Ідентифікатор; він же курсор сторінки.</param>
/// <param name="At">Момент спроби, UTC.</param>
/// <param name="ChannelId">Канал.</param>
/// <param name="ChannelName">Назва каналу; <c>null</c> — канал уже видалено, а журнал лишився.</param>
/// <param name="EventKind">Подія.</param>
/// <param name="EventKey">Ключ дедуплікації.</param>
/// <param name="Status">Підсумок спроби.</param>
/// <param name="Error">Причина відмови — без стека й без секрету.</param>
public sealed record NotificationDeliveryView(
    long Id, DateTime At, int ChannelId, string? ChannelName, NotificationEventKind EventKind,
    string EventKey, NotificationDeliveryStatus Status, string? Error);

/// <summary>Звуження журналу доставок; <c>null</c> у полі — «будь-яке».</summary>
/// <remarks>
/// ⚠ Канал — просто ФІЛЬТР, а не адресація: неіснуючий ідентифікатор дає
/// порожню сторінку, а не <c>404</c>. Журнал переживає видалення каналу
/// (зовнішнього ключа немає навмисно), тож «каналу немає» тут не означає
/// «рядків немає».
/// </remarks>
/// <param name="ChannelId">Канал.</param>
/// <param name="Status">Підсумок спроби.</param>
public sealed record NotificationDeliveryFilter(
    int? ChannelId = null, NotificationDeliveryStatus? Status = null);

/// <summary>Матриця правил сповіщень. Право <c>System.ManageNotifications</c>.</summary>
public sealed class GetNotificationRulesHandler(
    INotificationStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Вісь подій — усі оголошені види, у порядку оголошення.</summary>
    public static readonly IReadOnlyList<NotificationEventKind> AllEventKinds = Enum.GetValues<NotificationEventKind>();

    /// <summary>Повертає матрицю: усі види подій плюс заповнені клітинки.</summary>
    public async Task<NotificationRuleMatrix> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var rules = await store.ListRulesAsync(ct).ConfigureAwait(false);

        return new NotificationRuleMatrix(AllEventKinds, [.. rules.Select(ToView)]);
    }

    internal static NotificationRuleView ToView(NotificationRule rule)
        => new(rule.EventKind, rule.ChannelId, rule.MinSeverity, rule.IsEnabled);
}

/// <summary>Заміна матриці правил цілком. Право <c>System.ManageNotifications</c>.</summary>
/// <remarks>
/// ⛔ Заміна, а не додавання: клітинка, якої у вхідній матриці немає, зникає.
/// Тому та сама матриця, надіслана двічі, дає той самий стан — клітинка
/// шукається за парою «подія + канал» і ЗМІНЮЄТЬСЯ, а не додається вдруге.
/// </remarks>
public sealed class ReplaceNotificationRulesHandler(
    INotificationStore store, IAccessDecisionService access, IUnitOfWork uow, IAuditWriter audit,
    ICurrentUser currentUser, IClock clock)
{
    /// <summary>Застосовує матрицю й повертає її ж — упорядкованою.</summary>
    public async Task<NotificationRuleMatrix> HandleAsync(
        IReadOnlyList<NotificationRuleView>? rules, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var wanted = Validate(rules ?? []);
        await RequireChannelsExistAsync(wanted, ct).ConfigureAwait(false);

        var existing = await store.ListRulesAsync(ct).ConfigureAwait(false);
        var cells = existing.ToDictionary(r => (r.EventKind, r.ChannelId));
        var added = 0;

        foreach (var cell in wanted)
        {
            // ⛔ Саме тут і живе ідемпотентність PUT: наявну клітинку міняємо,
            // а не додаємо другу з тією самою парою.
            if (cells.TryGetValue((cell.EventKind, cell.ChannelId), out var rule))
            {
                rule.Update(cell.MinSeverity, cell.IsEnabled);
            }
            else
            {
                store.AddRule(new NotificationRule(cell.EventKind, cell.ChannelId, cell.MinSeverity, cell.IsEnabled));
                added++;
            }
        }

        var keep = wanted.Select(c => (c.EventKind, c.ChannelId)).ToHashSet();
        var removed = existing.Where(r => !keep.Contains((r.EventKind, r.ChannelId))).ToList();
        store.RemoveRules(removed);

        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "NotificationRulesReplaced",
            new { total = wanted.Count, added, updated = wanted.Count - added, removed = removed.Count }, ct)
            .ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new NotificationRuleMatrix(GetNotificationRulesHandler.AllEventKinds, wanted);
    }

    /// <summary>Відкидає невідомі значення переліків і дві клітинки з однією парою.</summary>
    private static List<NotificationRuleView> Validate(IReadOnlyList<NotificationRuleView> rules)
    {
        var ordered = rules.OrderBy(r => r.EventKind).ThenBy(r => r.ChannelId).ToList();

        var broken = ordered.Exists(r => !Enum.IsDefined(r.EventKind) || !Enum.IsDefined(r.MinSeverity))
            || ordered.Select(r => (r.EventKind, r.ChannelId)).Distinct().Count() != ordered.Count;

        if (broken)
        {
            throw ListNotificationChannelsHandler.Invalid(
                "err.ECR-REQ-0422.notificationRuleInvalid",
                "Матриця приймає відомі подію й серйозність, і не більш ніж одне правило на пару «подія + канал».");
        }

        return ordered;
    }

    /// <summary>Правило на неіснуючий канал — те саме <c>404</c>, що й звернення до самого каналу.</summary>
    private async Task RequireChannelsExistAsync(List<NotificationRuleView> rules, CancellationToken ct)
    {
        if (rules.Count == 0)
        {
            return;
        }

        var known = (await store.ListChannelsAsync(ct).ConfigureAwait(false)).Select(c => c.Id).ToHashSet();
        var missing = rules.Find(r => !known.Contains(r.ChannelId));

        if (missing is not null)
        {
            throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound, $"Каналу сповіщень {missing.ChannelId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0404.notificationChannel",
                    ["id"] = missing.ChannelId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }
}

/// <summary>Журнал доставок. Право <c>System.ManageNotifications</c>.</summary>
public sealed class ListNotificationDeliveriesHandler(
    INotificationStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>
    /// Стеля сторінки журналу — менша за спільну (<see cref="CursorRequest.MaxLimit"/>):
    /// журнал живе у вкладці шухляди каналу, і сторінка на 500 рядків там не
    /// читається ніким, а базі коштує повного сканування хвоста.
    /// </summary>
    public const int MaxLimit = 200;

    /// <summary>Повертає сторінку журналу, новіші першими.</summary>
    /// <param name="page">Розмір сторінки й курсор.</param>
    /// <param name="channelId">Звузити до одного каналу; <c>null</c> — усі.</param>
    /// <param name="status">
    /// Звузити до одного підсумку іменем зі списку
    /// <see cref="NotificationDeliveryStatus"/>; <c>null</c> або порожньо — усі.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<NotificationDeliveryView>> HandleAsync(
        CursorRequest page, int? channelId, string? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        if (page.Limit is < 1 or > MaxLimit)
        {
            // ⚠ Ключ той самий, що в решти переліків, — міняється лише `max`:
            // заводити окремий рядок каталогу заради іншого числа немає за що.
            var max = MaxLimit.ToString(CultureInfo.InvariantCulture);

            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = max,
                });
        }

        return await store
            .ReadDeliveriesAsync(page, new NotificationDeliveryFilter(channelId, StatusOf(status)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Підсумок за іменем; невідоме — <c>422</c>, а не мовчазне «усі».</summary>
    /// <remarks>
    /// ⚠ Та сама межа, що й у <c>state</c> переліку задач: друкарська помилка у
    /// фільтрі інакше відповідала б «таких доставок не було».
    /// </remarks>
    private static NotificationDeliveryStatus? StatusOf(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        // ⛔ Звірка з ІМЕНАМИ, а не `Enum.TryParse`: той приймає і число («2»),
        // і число поза переліком («99»), тобто фільтр мовчки пропускав би те,
        // чого в переліку немає.
        var known = Enum.GetValues<NotificationDeliveryStatus>()
            .Cast<NotificationDeliveryStatus?>()
            .FirstOrDefault(v => string.Equals(v.ToString(), status.Trim(), StringComparison.OrdinalIgnoreCase));

        return known
            ?? throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Підсумку доставки «{status}» не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.notificationDeliveryStatus",
                    ["status"] = status,
                });
    }
}
