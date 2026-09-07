using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Подання аркуша за період — гранулярність `аркуш × період` (D-38).
/// Створює **іммутабельний зріз** вхідних даних (ФВ-5.7, ФВ-9.4).
/// </summary>
#pragma warning disable CS9113 // див. коментар нижче: параметр лишений навмисно
public sealed class SubmitSheetHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IWorkflowStore workflow,
    IDocumentStore documents,
    IAccessDecisionService access,

    // ⛔ `validation` СЮДИ ВПОРСНУТИЙ І НЕ ЧИТАЄТЬСЯ, і це не недогляд
    // оформлення, а незакрита вимога. `ФВ-5.4`: «Рівні рядка, таблиці й
    // документа блокують `Submit`». Тут не блокує нічого: подання перевіряє
    // лише осиротілі рядки, а повну валідацію не кличе жодного разу — при
    // тому, що документація методу вже обіцяє `BusinessRuleException` із
    // приводу «валідація або осиротілі рядки».
    //
    // ⚠ Параметр лишається САМЕ ТОМУ, що прибрати його — найдешевший спосіб
    // зробити прогалину невидимою. Доки він тут, `CS9113` показує її при
    // кожному складанні; знята заборона на рівні дерева (`Directory.Build.props`)
    // означає, що НАСТУПНИЙ такий параметр стане помилкою складання, а цей
    // лишається єдиним оголошеним винятком (`Q-146`).
    Validation.ValidationEngine validation,
    Reporting.ReportSnapshotSync reports,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
#pragma warning restore CS9113
{
    /// <summary>Подає аркуш на погодження.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Аркуша немає в складі документа.</exception>
    /// <exception cref="AccessDeniedException">Немає рівня <c>Submit</c>.</exception>
    /// <exception cref="BusinessRuleException">Валідація або осиротілі рядки.</exception>
    public async Task HandleAsync(long documentId, int sheetDefId, int periodKey, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не може подавати аркуші.");

        var key = new PeriodKey(periodKey);

        // ⛔ Аркуш мусить входити в СКЛАД документа. Без цієї перевірки
        // `POST …/submit` на довільний `sheetDefId` — навіть той, якого в
        // документі ніколи не було, — проходив кодом `204`:
        // `IWorkflowStore.GetOrCreateAsync` нижче створює новий рядок стану
        // для БУДЬ-ЯКОГО ідентифікатора, а `CanSubmitAsync` перевіряє права,
        // не існування (виміряно живим прогоном, директива №09 §6.4, `S-17`).
        if (!await documents.HasSheetAsync(documentId, sheetDefId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "ECR-DOC-0404", $"Аркуша {sheetDefId} немає в складі документа {documentId}.");
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var decision = await access.CanSubmitAsync(profile, documentId, sheetDefId, key, ct)
                                   .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Подання аркуша {sheetDefId} відхилено: {decision.Reason}.",
                new Dictionary<string, object?> { ["reason"] = decision.Reason.ToString() });
        }

        // ⚠ Рядки з IsOrphaned блокують подання (ФВ-8.13). До Етапу 4 прапорець
        // ніхто не ставить — перевірка коректна і завжди пропускає; це не
        // несправність, а порядок робіт.
        //
        // ⚠ Саме GetOrphanedRowIdsAsync, а не GetOrphanFlagsAsync: у другого
        // перший аргумент — TableInstanceId, і передача documentId туди
        // компілювалася, але не знаходила нічого ніколи.
        var orphaned = await rowStore.GetOrphanedRowIdsAsync(documentId, key, ct).ConfigureAwait(false);
        if (orphaned.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-SUB-4221",
                $"Подання неможливе: рядків із втраченим посиланням на реєстр — {orphaned.Count}.",
                new Dictionary<string, object?> { ["rowIds"] = orphaned });
        }

        var state = await workflow.GetOrCreateAsync(documentId, sheetDefId, key, ct).ConfigureAwait(false);

        // ⚠ Іммутабельний зріз створюється ДО зміни стану: якщо зріз не
        // збережеться, аркуш не має стати поданим. Поданий аркуш без зрізу —
        // звіт, який неможливо ні звірити, ні перерахувати «як тоді».
        var payload = await SnapshotPayloadAsync(documentId, key, ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        await workflow.SaveSnapshotAsync(
            new SubmissionSnapshotRecord(
                documentId, sheetDefId, periodKey,
                TemplateVersionId: 0,
                MethodologyVersionsJson: null,
                NumericMode: (byte)NumericMode.Legacy,
                CalendarMode: (byte)Domain.Enums.CalendarMode.Actual,
                PayloadJson: payload,
                ContentHash: Hash(payload),
                SubmittedAt: now,
                SubmittedByUserId: userId),
            ct).ConfigureAwait(false);

        // ⛔ Подання СТАВИТЬ аркуш на перший крок маршруту (`ФВ-5.17`).
        // Без цього багатоетапність існувала б лише в таблиці: маршрут
        // завели б, а документ ішов би повз нього.
        //
        // ⚠ Маршруту немає — крок `null`, і поведінка та сама, що була.
        var step = await access
            .CurrentApprovalStepAsync(documentId, sheetDefId, key, ct)
            .ConfigureAwait(false);

        state.Submit(userId, now, step?.StepId);

        // ⛔ Подання СПОВІЩЕННЯ НЕ ПОРОДЖУЄ (`D-119`). Тут раніше стояла
        // постановка події в чергу — прибрано за рішенням замовника: лист про
        // кожне подання це шум, а шум вимикають разом із корисними листами.
        //
        // ⚠ Події черги — лише ЗБОЇ: збір, архівація, стани періодів,
        // перевищення бюджету перерахунку. Їх зводить `NotificationJob`.

        // ⛔ Подання МОРОЗИТЬ зріз звітності, якщо в періоді не лишилося
        // неподаних аркушів (`H-23b`, ФВ-9.17). На цьому тримається `ER-C-11`:
        // «подане не перераховується». Доти позначку `Submitted` не ставив
        // ніхто, і гарантія існувала лише на письмі — зріз, за яким звіт уже
        // пішов регуляторові, спокійно перебудовувався з іншими числами.
        await reports
            .MarkSubmittedAsync(documentId, key, userId, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Зліпок значень аркуша у стабільному порядку.</summary>
    /// <remarks>
    /// Порядок фіксований навмисно: контрольна сума має залежати від ДАНИХ, а
    /// не від того, як їх повернула база цього разу.
    /// </remarks>
    private async Task<string> SnapshotPayloadAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var cells = await cellStore.ReadSliceAsync(documentId, ct).ConfigureAwait(false);

        var ordered = cells
            .Where(c => c.Address.PeriodKey.Value == periodKey.Value)
            .OrderBy(c => c.Address.TableRowId)
            .ThenBy(c => c.Address.ColumnDefId)
            .Select(c => new
            {
                row = c.Address.TableRowId,
                column = c.Address.ColumnDefId,
                value = c.Value.ValueNumeric?.ToString(CultureInfo.InvariantCulture) ?? c.Value.ValueString,
            });

        return JsonSerializer.Serialize(ordered);
    }

    private static string Hash(string payload)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
