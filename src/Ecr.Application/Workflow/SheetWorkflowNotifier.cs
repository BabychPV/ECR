// src/Ecr.Application/Workflow/SheetWorkflowNotifier.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Application.Workflow;

/// <summary>
/// ✎ D-451: листи «Аркуш подано» (погоджувачам поточного кроку) і «Аркуш повернуто» (власнику документа й
/// учасникам із наданням Write/Submit) — рішення людини 24.10, за замовчуванням вимкнені.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Вмикає лист ІСНУЮЧА матриця правил: подія <see cref="NotificationEventKind.SheetSubmitted"/> /
/// <see cref="NotificationEventKind.SheetReturned"/> × УВІМКНЕНИЙ канал пошти (<see cref="NotificationChannelKind.Smtp"/>),
/// межа серйозності — <c>Info</c>. Правил на ці події сід не створює, тож оновлена інсталяція не починає слати.
/// Лист АДРЕСНИЙ: адресати каналу (явні адреси й ролі) його не отримують — канал лише каже «пошта ввімкнена».
/// Правило на канал Teams ігнорується: картка в груповий чат показала б аркуш тим, хто його не бачить.
/// </para>
/// <para>
/// ⛔ Подія кладеться в ЧЕРГУ <c>itg.NotificationOutbox</c> (<see cref="INotificationOutbox"/>, той самий шлях, що й
/// листи передач зрізу), а надсилає її <c>NotificationJob</c> → <c>OutboxDispatcher</c>: стан доставки (Sent/Failed,
/// спроби) — у рядку черги, не в журналі доставок матриці.
/// </para>
/// <para>
/// ⛔ ПІСЛЯ коміту дії і без права її валити (той самий принцип, що <c>PeriodStateJob.NotifyAsync</c>): будь-який збій
/// тут — запис у журнал сервера, а подання/повернення вже зафіксоване й лишається успішним. Ціна — процес, убитий
/// між комітом дії й постановкою листа, лист втрачає; лист інформаційний, а не умова дії. Збір адресатів
/// ВСЕРЕДИНІ транзакції подання тримав би виняткове блокування аркуша на час N рішень про доступ.
/// </para>
/// <para>
/// ⛔ Адресатів вирішує <see cref="IAccessDecisionService"/> профілем КОЖНОГО кандидата: «подано» — той, кому
/// <c>CanApproveAsync</c> дозволяє погодити аркуш зараз (роль поточного кроку маршруту + грант ≥ Approve, у тому
/// числі учасник із наданням Approve, D-444), «повернуто» — той, хто бачить документ і аркуш. Сховище дає лише
/// передфільтр кандидатів. Хто діяв — листа про власну дію не отримує.
/// </para>
/// </remarks>
public sealed partial class SheetWorkflowNotifier(
    INotificationDispatchStore rules,
    ISheetWorkflowMailStore store,
    IAccessDecisionService access,
    IDocumentStore documents,
    IUiStringCatalog catalog,
    INotificationOutbox outbox,
    IUnitOfWork uow,
    ILogger<SheetWorkflowNotifier>? logger = null)
{
    /// <summary>Код події черги для «подано».</summary>
    public const string SubmittedEventCode = "sheet.submitted";

    /// <summary>Код події черги для «повернуто».</summary>
    public const string ReturnedEventCode = "sheet.returned";

    /// <summary>Ключі шаблону «подано» в каталозі рядків.</summary>
    public const string SubmittedSubjectKey = "notifications.sheetSubmitted.subject";

    /// <inheritdoc cref="SubmittedSubjectKey"/>
    public const string SubmittedBodyKey = "notifications.sheetSubmitted.body";

    /// <summary>Ключі шаблону «повернуто» в каталозі рядків.</summary>
    public const string ReturnedSubjectKey = "notifications.sheetReturned.subject";

    /// <inheritdoc cref="ReturnedSubjectKey"/>
    public const string ReturnedBodyKey = "notifications.sheetReturned.body";

    /// <summary>
    /// Стеля кандидатів, для яких будується профіль і рішення про доступ (захист від ролі на сотні людей).
    /// </summary>
    /// <remarks>⚠ Обрізання — не мовчазне: попередження в журнал сервера з числом відкинутих.</remarks>
    public const int MaxCandidates = 200;

    /// <summary>Довжина теми в черзі (<c>itg.NotificationOutbox.Subject</c>).</summary>
    private const int SubjectMaxLength = 400;

    /// <summary>Довжина переліку адрес у черзі (<c>itg.NotificationOutbox.Recipients</c>).</summary>
    private const int RecipientsMaxLength = 4000;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "D-451: лист {EventCode} не поставлено (документ {DocumentId}, аркуш {SheetDefId}, період {PeriodKey}); дія лишається успішною.")]
    private static partial void LogFailed(
        ILogger logger, string eventCode, long documentId, int sheetDefId, int periodKey, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "D-451: лист {EventCode} — кандидатів {Count}, перевірено перших {Max}; решта без листа (документ {DocumentId}).")]
    private static partial void LogTruncated(ILogger logger, string eventCode, int count, int max, long documentId);

    /// <summary>Лист «Аркуш подано» погоджувачам поточного кроку; викликається ПІСЛЯ коміту подання.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="submitterId">Хто подав (листа не отримує).</param>
    /// <param name="at">Момент подання, UTC.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⛔ Не кидає ніколи: збій — у журнал сервера.</remarks>
    public Task SubmittedAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int submitterId, DateTime at, CancellationToken ct)
        => SafelyAsync(
            SubmittedEventCode, documentId, sheetDefId, periodKey,
            () => SubmittedCoreAsync(documentId, sheetDefId, periodKey, submitterId, at, ct));

    /// <summary>Лист «Аркуш повернуто» власнику й учасникам Write/Submit; викликається ПІСЛЯ коміту повернення.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="returnerId">Хто повернув (листа не отримує).</param>
    /// <param name="at">Момент повернення, UTC.</param>
    /// <param name="reason">Причина повернення.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⛔ Не кидає ніколи: збій — у журнал сервера.</remarks>
    public Task ReturnedAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int returnerId, DateTime at, string? reason,
        CancellationToken ct)
        => SafelyAsync(
            ReturnedEventCode, documentId, sheetDefId, periodKey,
            () => ReturnedCoreAsync(documentId, sheetDefId, periodKey, returnerId, at, reason, ct));

    private async Task SafelyAsync(
        string eventCode, long documentId, int sheetDefId, PeriodKey periodKey, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Лист — не умова дії: подання/повернення вже закомічене (D-451).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(Logger, eventCode, documentId, sheetDefId, periodKey.Value, ex);
        }
    }

    private ILogger Logger => logger ?? NullLogger<SheetWorkflowNotifier>.Instance;

    private async Task SubmittedCoreAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int submitterId, DateTime at, CancellationToken ct)
    {
        if (!await IsEnabledAsync(NotificationEventKind.SheetSubmitted, ct).ConfigureAwait(false))
        {
            return;
        }

        var facts = await store.GetFactsAsync(documentId, sheetDefId, periodKey, submitterId, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return;
        }

        // ⛔ Кандидати — носії ролі ПОТОЧНОГО кроку (маршрут той самий, що бачить `CanApproveAsync`); маршруту
        // немає — носії ролей із грантом Approve. Плюс учасники з наданням Approve (D-444): роль кроку їм однаково
        // потрібна, і перевіряє її саме рішення нижче, а не цей перелік.
        var step = await access.CurrentApprovalStepAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
        var candidates = new HashSet<int>(
            step is null
                ? await store.ApproverRoleHoldersAsync(ct).ConfigureAwait(false)
                : await store.RoleHoldersAsync(step.RoleId, ct).ConfigureAwait(false));
        candidates.UnionWith(await store
            .ParticipantsAsync(documentId, facts.SheetCode, periodKey, [GrantLevel.Approve], ct)
            .ConfigureAwait(false));
        candidates.Remove(submitterId);

        var recipients = new List<int>();
        foreach (var userId in Capped(SubmittedEventCode, candidates, documentId))
        {
            var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
            if ((await access.CanApproveAsync(profile, documentId, sheetDefId, periodKey, ct).ConfigureAwait(false)).IsAllowed
                && await SeesSheetAsync(profile, documentId, sheetDefId, periodKey, ct).ConfigureAwait(false))
            {
                recipients.Add(userId);
            }
        }

        await EnqueueAsync(
            SubmittedEventCode, SubmittedSubjectKey, SubmittedBodyKey, documentId, periodKey, facts, at, reason: null,
            recipients, ct).ConfigureAwait(false);
    }

    private async Task ReturnedCoreAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int returnerId, DateTime at, string? reason,
        CancellationToken ct)
    {
        if (!await IsEnabledAsync(NotificationEventKind.SheetReturned, ct).ConfigureAwait(false))
        {
            return;
        }

        var facts = await store.GetFactsAsync(documentId, sheetDefId, periodKey, returnerId, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return;
        }

        // ⛔ Власник (D-443: `CreatedByUserId`, змінюється передачею власності) + учасники, що заповнюють або
        // подають цей аркуш (D-443: Write — передача місяця, Submit — ще й подання). Approve-учасник — погоджувач,
        // не автор, і про повернення, яке, можливо, сам зробив, листа не потребує.
        var candidates = new HashSet<int> { facts.OwnerUserId };
        candidates.UnionWith(await store
            .ParticipantsAsync(documentId, facts.SheetCode, periodKey, [GrantLevel.Write, GrantLevel.Submit], ct)
            .ConfigureAwait(false));
        candidates.Remove(returnerId);

        var recipients = new List<int>();
        foreach (var userId in Capped(ReturnedEventCode, candidates, documentId))
        {
            var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
            if (await SeesSheetAsync(profile, documentId, sheetDefId, periodKey, ct).ConfigureAwait(false))
            {
                recipients.Add(userId);
            }
        }

        await EnqueueAsync(
            ReturnedEventCode, ReturnedSubjectKey, ReturnedBodyKey, documentId, periodKey, facts, at, reason,
            recipients, ct).ConfigureAwait(false);
    }

    /// <summary>Чи є ввімкнене правило «подія → увімкнений канал пошти», що пропускає <c>Info</c>.</summary>
    private async Task<bool> IsEnabledAsync(NotificationEventKind kind, CancellationToken ct)
    {
        var plan = await rules.GetPlanAsync(ct).ConfigureAwait(false);

        return plan.TargetsOf(kind, NotificationSeverity.Info).Any(c => c.Kind == NotificationChannelKind.Smtp);
    }

    /// <summary>
    /// Видимість документа й аркуша — ті самі перевірки, що й на маршрутах аркуша
    /// (<see cref="Documents.DocumentVisibility"/>): «не знайдено» для дії = «без листа» тут.
    /// </summary>
    private async Task<bool> SeesSheetAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        try
        {
            await Documents.DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);
            await Documents.DocumentVisibility
                .RequireSheetVisibleAsync(documents, access, profile, documentId, sheetDefId, periodKey, ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }

    private IEnumerable<int> Capped(string eventCode, HashSet<int> candidates, long documentId)
    {
        if (candidates.Count > MaxCandidates)
        {
            LogTruncated(Logger, eventCode, candidates.Count, MaxCandidates, documentId);
        }

        return candidates.Order().Take(MaxCandidates);
    }

    /// <summary>
    /// Лист кожному адресату ЙОГО мовою (як ролі каналу, <c>D-263</c>): групи за мовою, у кожній — шаблон каталогу
    /// з підстановками. Усі рядки черги — одним <c>SaveChanges</c>.
    /// </summary>
    private async Task EnqueueAsync(
        string eventCode, string subjectKey, string bodyKey, long documentId, PeriodKey periodKey, SheetMailFacts facts,
        DateTime at, string? reason, List<int> recipients, CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        var mailboxes = await store.MailboxesAsync(recipients, ct).ConfigureAwait(false);
        if (mailboxes.Count == 0)
        {
            return;
        }

        var link = store.SheetLink(documentId, periodKey, facts.SheetCode);

        foreach (var group in mailboxes.GroupBy(m => m.Language, StringComparer.OrdinalIgnoreCase))
        {
            var strings = (await catalog.GetAsync(group.Key, ct).ConfigureAwait(false)).Strings;
            var args = Args(facts, group.Key, at, reason, link);

            string Fill(string key)
                => args.Aggregate(
                    strings.GetValueOrDefault(key, key),
                    (acc, arg) => acc.Replace("{" + arg.Key + "}", arg.Value, StringComparison.Ordinal));

            var subject = OneLine(Fill(subjectKey));
            if (subject.Length > SubjectMaxLength)
            {
                subject = subject[..SubjectMaxLength];
            }

            var body = Fill(bodyKey);

            foreach (var chunk in Chunks(group.Select(m => m.Email)))
            {
                await outbox.EnqueueAsync(eventCode, subject, body, chunk, ct).ConfigureAwait(false);
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Підстановки шаблону мовою адресата.</summary>
    /// <remarks>
    /// ⚠ Лист — простий текст (<c>MailMessage.IsBodyHtml</c> не ставиться), тож значення не кодуються як HTML.
    /// Час — UTC з позначкою: мови часового поясу адресата система не знає.
    /// </remarks>
    internal static Dictionary<string, string> Args(
        SheetMailFacts facts, string language, DateTime at, string? reason, string link)
    {
        var name = facts.DocumentName?.Get(language);
        var document = string.IsNullOrWhiteSpace(name) ? facts.DocumentKey : $"{facts.DocumentKey} — {name}";
        var sheet = facts.SheetName?.Get(language) is { Length: > 0 } sheetName ? sheetName : facts.SheetCode;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document"] = document,
            ["sheet"] = sheet,
            ["period"] = facts.PeriodLabel,
            ["by"] = facts.ActorName,
            ["at"] = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
            ["reason"] = reason?.Trim() ?? string.Empty,
            ["link"] = link,
        };
    }

    /// <summary>Тема без переносів рядка: <c>MailMessage.Subject</c> їх не приймає.</summary>
    private static string OneLine(string text)
        => text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Адреси через кому порціями, що вміщаються в стовпець черги.</summary>
    internal static IEnumerable<string> Chunks(IEnumerable<string> emails)
    {
        var current = new List<string>();
        var length = 0;

        foreach (var email in emails)
        {
            var add = email.Length + (current.Count == 0 ? 0 : 1);
            if (current.Count > 0 && length + add > RecipientsMaxLength)
            {
                yield return string.Join(',', current);
                current.Clear();
                length = 0;
                add = email.Length;
            }

            current.Add(email);
            length += add;
        }

        if (current.Count > 0)
        {
            yield return string.Join(',', current);
        }
    }
}
