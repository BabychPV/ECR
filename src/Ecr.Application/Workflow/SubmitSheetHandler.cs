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
public sealed class SubmitSheetHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IWorkflowStore workflow,
    IDocumentStore documents,
    IMetadataCache metadata,
    IAccessDecisionService access,

    // ⛔ `validation` тут ЧИТАЄТЬСЯ (директива №09 `W8` п.5, `S-28`). Доти
    // параметр був упорснутий і не читаний — навмисно, під точковим
    // `#pragma warning disable CS9113`, щоб незакрита вимога `ФВ-5.4` («рівні
    // рядка, таблиці й документа блокують `Submit`») лишалася видимою при
    // кожному складанні, а не зникла разом із прибраним аргументом (`Q-146`).
    // Подання перевіряло лише осиротілі рядки — при тому, що документація
    // методу вже обіцяла `BusinessRuleException` «валідація або осиротілі
    // рядки». Тепер обіцянка виконується, і придушення прибране.
    Validation.ValidationEngine validation,
    Reporting.ReportSnapshotSync reports,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
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

        var instances = await rowStore.GetTableInstancesAsync(documentId, key, ct).ConfigureAwait(false);

        // ⛔ ВАЛІДАЦІЯ АРКУША, який подають (`ФВ-5.4`, директива №09 `W8` п.5).
        // Це те, чого тут не було зовсім: подання перевіряло лише осиротілі
        // рядки, тобто аркуш із блокувальними помилками подавався кодом `204`
        // і йшов далі по маршруту погодження як придатний.
        //
        // ⚠ Прогін СВІЖИЙ, а не читання збереженого підсумку
        // (`IValidationResultStore.GetLatestAsync`). Підсумок відповідає на
        // питання «що показала перевірка тоді», а подання питає «що з даними
        // ЗАРАЗ»: між натисканням «Перевірити» і «Подати» дані міняються, і
        // саме ця пара кроків — найзвичніший спосіб обійти перевірку, не
        // маючи такого наміру.
        //
        // ⚠ Рахується лише АРКУШ, який подають, а не документ цілком:
        // гранулярність робочого процесу — `аркуш × період` (`D-38`), і
        // блокувати подання одного аркуша помилкою сусіднього означало б
        // зробити багатоаркушевий документ неподаваним по частинах.
        var templateVersionId = await TemplateVersionOfAsync(documentId, instances, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId);
        var tables = sheet?.Tables.Where(t => !t.IsDeleted).ToDictionary(t => t.Id)
                     ?? new Dictionary<int, Domain.Entities.Configuration.TableDef>();

        var blocking = new List<Validation.ValidationMessage>();

        foreach (var instance in instances)
        {
            if (!tables.TryGetValue(instance.TableDefId, out var table)
                || table.ValidationRules.Count == 0)
            {
                continue;
            }

            var cells = await cellStore.ReadSliceAsync(instance.TableInstanceId, ct).ConfigureAwait(false);
            var rowIds = await rowStore.GetRowIdsAsync(instance.TableInstanceId, key, ct).ConfigureAwait(false);

            blocking.AddRange(Validation.TableValidation
                .Run(validation, table, cells, rowIds)
                .Where(m => m.Severity == ValidationSeverity.Error));
        }

        if (blocking.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-SUB-4221",
                $"Подання неможливе: блокувальних помилок валідації — {blocking.Count}.",
                new Dictionary<string, object?>
                {
                    ["messages"] = blocking
                        .Select(m => new { m.RuleCode, m.Message, m.RowKey, m.ColumnCode })
                        .ToList(),
                });
        }

        var state = await workflow.GetOrCreateAsync(documentId, sheetDefId, key, ct).ConfigureAwait(false);

        // ⚠ Іммутабельний зріз створюється ДО зміни стану: якщо зріз не
        // збережеться, аркуш не має стати поданим. Поданий аркуш без зрізу —
        // звіт, який неможливо ні звірити, ні перерахувати «як тоді».
        var payload = await SnapshotPayloadAsync(instances, key, ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        // ⛔ `TemplateVersionId` — СПРАВЖНІЙ (директива №09 `W8` п.5).
        // Тут стояв нуль: зріз, створений заради того, щоб через рік можна
        // було сказати «за якою структурою це подавали», не ніс структури
        // взагалі. Нуль при цьому не помітний нізвідки — він виглядає як
        // значення.
        //
        // ⚠ `MethodologyVersionsJson`, `NumericMode` і `CalendarMode`
        // лишаються незаповненими СВІДОМО і винесені в `Q-153`: чинні версії
        // методологій цього документа сюди не проведені, а вигадати їх тут
        // означало б записати в зріз неправду замість порожнечі. Різниця
        // принципова: порожнє поле видно, а `Legacy`/`Actual` за
        // замовчуванням виглядають як зафіксований вибір.
        await workflow.SaveSnapshotAsync(
            new SubmissionSnapshotRecord(
                documentId, sheetDefId, periodKey,
                templateVersionId,
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

    /// <summary>Версія шаблону, за якою живе документ.</summary>
    /// <remarks>
    /// ⚠ Спершу з уже прочитаних екземплярів таблиць — там вона є
    /// (<see cref="TableInstanceRef.TemplateVersionId"/>), і зайвий запит
    /// нічого не додасть. Запит іде лише тоді, коли екземплярів немає взагалі:
    /// документ за цей період ще не відкривали, а зріз подання однаково має
    /// назвати структуру.
    /// </remarks>
    private async Task<int> TemplateVersionOfAsync(
        long documentId, IReadOnlyList<TableInstanceRef> instances, CancellationToken ct)
        => instances.Count > 0
            ? instances[0].TemplateVersionId
            : await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);

    /// <summary>Зліпок значень аркуша у стабільному порядку.</summary>
    /// <remarks>
    /// Порядок фіксований навмисно: контрольна сума має залежати від ДАНИХ, а
    /// не від того, як їх повернула база цього разу.
    ///
    /// ⛔ Читаються ЕКЗЕМПЛЯРИ ТАБЛИЦЬ, а не «зріз документа». Тут стояло
    /// <c>cellStore.ReadSliceAsync(documentId)</c> — при тому, що перший
    /// аргумент цього порту зветься <c>tableInstanceId</c>. Обидва
    /// <c>long</c>, тож компілятор мовчав, а зріз подання набирався з
    /// екземпляра таблиці, чий ідентифікатор ВИПАДКОВО збігся з номером
    /// документа: майже завжди — з порожнечі, іноді — з чужих даних. Той
    /// самий клас помилки, що вже коштував перевірки осиротілих рядків
    /// (<c>IRowStore.GetOrphanedRowIdsAsync</c>).
    /// </remarks>
    private async Task<string> SnapshotPayloadAsync(
        IReadOnlyList<TableInstanceRef> instances, PeriodKey periodKey, CancellationToken ct)
    {
        var cells = new List<CellRecord>();

        foreach (var instance in instances)
        {
            cells.AddRange(await cellStore
                .ReadSliceAsync(instance.TableInstanceId, ct).ConfigureAwait(false));
        }

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
