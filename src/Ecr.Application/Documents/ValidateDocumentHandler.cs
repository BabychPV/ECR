using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Повна валідація документа перед поданням (ФВ-5.1).</summary>
public sealed class ValidateDocumentHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IMetadataCache metadata,
    IValidationResultStore results,
    ValidationEngine engine,
    IDocumentHeaderStore headers,
    Domain.Abstractions.IClock clock,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,

    // ⛔ D16-04: знімок полів довідника для `REGFIELD` у правилах.
    IRegistryStore registries,

    // D-230: знахідки зв'язків виду Check (`RelationCheckRunner`).
    ITemplateVersionStore templateVersions)
{
    /// <summary>Виконує валідацію всіх аркушів документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Повідомлення трьох рівнів; наявність <c>Error</c> блокує <c>Submit</c>.</returns>
    public async Task<IReadOnlyList<ValidationMessage>> HandleAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). Валідація читає ВЕСЬ документ
        // і повертає повідомлення з підписами рядків і колонок — тобто його
        // зміст. До цього її міг запустити будь-хто, хто увійшов.
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        // ФВ-6.14: і право — у проєкті документа.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.View", ct).ConfigureAwait(false);

        // ⚠ Екземпляри таблиць беруться ОДНИМ запитом, а не по аркушах:
        // бюджет — 3 с p95 на весь документ, і похід у базу на кожну з
        // сотні таблиць у нього не вкладається.
        var instances = await rowStore
            .GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        // ⛔ Q-165 (аудит фази 2, продуктивність): які таблиці мають правила
        // з'ясовується з кешу метаданих (без походу в базу) ДО читання
        // комірок і рядків — так у пакетні запити нижче йдуть лише таблиці,
        // які реально валідуються, а не всі ~90 таблиць документа.
        var toValidate = new List<(TableInstanceRef Instance, TableDef Table, TemplateVersionSnapshot Snapshot)>();
        foreach (var instance in instances)
        {
            var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            var table = snapshot.Sheets
                .SelectMany(s => s.Tables)
                .FirstOrDefault(t => t.Id == instance.TableDefId);

            // ⛔ L6-09: і таблиця без правил, але з обов'язковою колонкою — подання
            // блокує саме її незаповнені комірки, тож «Перевірити» мусить їх показати.
            if (table is null
                || (table.ValidationRules.Count == 0 && !table.Columns.Any(c => !c.IsDeleted && c.IsRequired)))
            {
                continue;
            }

            toValidate.Add((instance, table, snapshot));
        }

        var instanceIds = toValidate.Select(t => t.Instance.TableInstanceId).ToList();

        // ⚠ Обидва зрізи беруться ОДНИМ запитом на весь документ, а не по
        // аркушах: бюджет — 3 с p95 на весь документ, і похід у базу на
        // кожну таблицю у нього не вкладається (той самий принцип, що вже
        // застосований вище до `GetTableInstancesAsync`).
        var cellsByInstance = await cellStore.ReadSlicesAsync(instanceIds, periodKey, ct).ConfigureAwait(false);

        // ⛔ Рядки екземпляра читаються ЯВНО: правило рівня рядка має назвати
        // `RowKey`, а зі самих комірок його не взяти — рядок без жодного
        // значення в зрізі не з'являється взагалі (`ФВ-3.8`), і саме він
        // найчастіше і порушує «поле обов'язкове».
        var rowIdsByInstance = await rowStore
            .GetRowIdsBatchAsync(instanceIds, periodKey, ct).ConfigureAwait(false);

        // ⛔ Шапка документа читається РЕАЛЬНО (раніше HDR.X у правилах
        // валідації завжди давав Null). Один запит на весь документ: шапка
        // та сама для кожної з до ~90 таблиць, повторювати запит у циклі
        // нижче означало б піти в базу настільки ж зайвий раз.
        var headerValues = await headers.GetExpressionValuesAsync(documentId, ct).ConfigureAwait(false);

        // ⚠ B-11: `Message` тут — мовою того, хто ЗАПУСТИВ перевірку, і це лише
        // запасний текст. T2-07 / T3-03 / T4-06: повідомлення двигуна (Check,
        // структурні, зламане правило) несуть `MessageKey` + `Params`, а
        // `GetValidationResultHandler` збирає текст мовою КОЖНОГО читача; тексти
        // правил він бере з `MessageL10n`. Старі збережені результати без ключа
        // лишаються текстом автора запуску.
        var messages = new List<ValidationMessage>();

        foreach (var (instance, table, snapshot) in toValidate)
        {
            var cells = cellsByInstance.TryGetValue(instance.TableInstanceId, out var found)
                ? found
                : [];
            var rowIds = rowIdsByInstance.TryGetValue(instance.TableInstanceId, out var foundRows)
                ? foundRows
                : new Dictionary<string, long>(StringComparer.Ordinal);

            if (table.ValidationRules.Count > 0)
            {
                messages.AddRange(await TableValidation
                    .RunAsync(engine, registries, snapshot, table, cells, rowIds, headerValues, currentUser.Language, ct)
                    .ConfigureAwait(false));
            }

            messages.AddRange(TableValidation.MissingRequiredColumnMessages(
                table, [.. table.Columns.Where(c => !c.IsDeleted && c.IsRequired)], cells, rowIds, currentUser.Language));
        }

        // ⛔ D-PS / R-B3: «Перевірити» рахує те саме, що подання, — порожнє обов'язкове поле шапки.
        // Версія — з екземплярів таблиць (документ без жодного екземпляра ще не відкривали). Значення
        // читаються лише коли в шаблоні є обов'язкове поле. Лише коди полів, без значень.
        if (instances.Count > 0)
        {
            var headerSnapshot = await metadata.GetAsync(instances[0].TemplateVersionId, ct).ConfigureAwait(false);
            var requiredHeader = RequiredHeaderCheck.RequiredFields(headerSnapshot.HeaderFields);
            if (requiredHeader.Count > 0)
            {
                var rawHeader = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
                var emptyHeader = RequiredHeaderCheck.EmptyCodes(requiredHeader, rawHeader);
                if (emptyHeader.Count > 0)
                {
                    messages.Add(RequiredHeaderCheck.ToMessage(emptyHeader));
                }
            }
        }

        // D-230: зв'язки Check (ПРИПУЩЕННЯ схеми, `RelationSpec.cs`). Читаються після таблиць із
        // правилами, бо мають власну вибірку таблиць (джерело й приймач зв'язку).
        messages.AddRange(await RelationCheckRunner
            .RunAsync(templateVersions, cellStore, rowStore, metadata, instances, periodKey, currentUser.Language, ct)
            .ConfigureAwait(false));

        var summary = new ValidationSummary(
            documentId,
            periodKey.Value,
            clock.UtcNow,
            messages.Count(m => m.Severity == ValidationSeverity.Error),
            messages.Count(m => m.Severity == ValidationSeverity.Warning),
            messages.Count(m => m.Severity == ValidationSeverity.Info),
            JsonSerializer.Serialize(messages));

        // ⚠ Підсумок ЗБЕРІГАЄТЬСЯ: подання питає в нього, чи є незакриті
        // помилки (ФВ-5.19). Якби воно щоразу перевалідовувало документ,
        // «подати» коштувало б стільки ж, скільки «перевірити», і на великому
        // документі це були б ті самі три секунди в найгірший момент.
        await results.SaveAsync(summary, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ S6 (ФВ-6.6): ЗБЕРІГАЄТЬСЯ підсумок цілком — подання питає його про
        // помилки на весь документ, а не на те, що бачить запускач. ВІДДАЮТЬСЯ ж
        // лише повідомлення про таблиці й колонки, які запускач бачить: адреса
        // (`RowKey`, `ColumnCode`) і текст правила — теж зміст прихованого.
        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        Security.DocumentReadScope? readable = null;

        // ⛔ Приховані помилки не зникають мовчки (`HiddenValidationIssues`):
        // інакше запускач, чиї зауваження всі під забороною, бачить «зауважень
        // немає», а «Подати» відмовляє.
        return HiddenValidationIssues.ForViewer(messages, m => HiddenValidationIssues.CanSee(readable ??= scope.InPeriod(periodKey), m));
    }

}
