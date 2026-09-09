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
    Domain.Abstractions.IClock clock,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
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
            .RequireAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
        }

        // ⚠ Екземпляри таблиць беруться ОДНИМ запитом, а не по аркушах:
        // бюджет — 3 с p95 на весь документ, і похід у базу на кожну з
        // сотні таблиць у нього не вкладається.
        var instances = await rowStore
            .GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        // ⛔ Q-165 (аудит фази 2, продуктивність): які таблиці мають правила
        // з'ясовується з кешу метаданих (без походу в базу) ДО читання
        // комірок і рядків — так у пакетні запити нижче йдуть лише таблиці,
        // які реально валідуються, а не всі ~90 таблиць документа.
        var toValidate = new List<(TableInstanceRef Instance, TableDef Table)>();
        foreach (var instance in instances)
        {
            var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            var table = snapshot.Sheets
                .SelectMany(s => s.Tables)
                .FirstOrDefault(t => t.Id == instance.TableDefId);

            if (table is null || table.ValidationRules.Count == 0)
            {
                continue;
            }

            toValidate.Add((instance, table));
        }

        var instanceIds = toValidate.Select(t => t.Instance.TableInstanceId).ToList();

        // ⚠ Обидва зрізи беруться ОДНИМ запитом на весь документ, а не по
        // аркушах: бюджет — 3 с p95 на весь документ, і похід у базу на
        // кожну таблицю у нього не вкладається (той самий принцип, що вже
        // застосований вище до `GetTableInstancesAsync`).
        var cellsByInstance = await cellStore.ReadSlicesAsync(instanceIds, ct).ConfigureAwait(false);

        // ⛔ Рядки екземпляра читаються ЯВНО: правило рівня рядка має назвати
        // `RowKey`, а зі самих комірок його не взяти — рядок без жодного
        // значення в зрізі не з'являється взагалі (`ФВ-3.8`), і саме він
        // найчастіше і порушує «поле обов'язкове».
        var rowIdsByInstance = await rowStore
            .GetRowIdsBatchAsync(instanceIds, periodKey, ct).ConfigureAwait(false);

        var messages = new List<ValidationMessage>();

        foreach (var (instance, table) in toValidate)
        {
            var cells = cellsByInstance.TryGetValue(instance.TableInstanceId, out var found)
                ? found
                : [];
            var rowIds = rowIdsByInstance.TryGetValue(instance.TableInstanceId, out var foundRows)
                ? foundRows
                : new Dictionary<string, long>(StringComparer.Ordinal);

            messages.AddRange(TableValidation.Run(engine, table, cells, rowIds));
        }

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

        return messages;
    }

}
