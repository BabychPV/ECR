using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Додає рядок у динамічну таблицю.</summary>
public sealed class CreateRowHandler(
    IRowStore rowStore,
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Створює рядок і повертає його ключ.</summary>
    /// <exception cref="Errors.BusinessRuleException">
    /// Таблиця не дозволяє динамічні рядки, перевищено <c>MaxDynamicRows</c>,
    /// або <c>RowKey</c> уже існує (<c>ECR-ROW-0409</c>).
    /// </exception>
    public async Task<RowKey> HandleAsync(long documentId, long tableInstanceId, RowKey? requestedKey,
                                          AccessProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var instance = await rowStore.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);

        var table = snapshot.Sheets
            .SelectMany(sh => sh.Tables)
            .FirstOrDefault(t => t.Id == instance.TableDefId)
            ?? throw new Errors.NotFoundException(
                "ECR-TMPL-0404", $"Таблиці {instance.TableDefId} немає в структурі версії {instance.TemplateVersionId}.");

        // 1. Рядок можна додати лише в динамічну таблицю. У Fixed склад рядків
        //    заданий шаблоном, і поява «зайвого» зламала б і формули з
        //    діапазонами, і звірку з еталоном.
        if (table.RowMode != Domain.Enums.TableRowMode.Dynamic)
        {
            throw new Errors.BusinessRuleException(
                "ECR-ROW-0409",
                $"Таблиця {table.Code} має RowMode = {table.RowMode}: рядки задані шаблоном і не додаються.");
        }

        var existing = await rowStore.GetRowIdsAsync(tableInstanceId, PeriodKeyOf(instance), ct).ConfigureAwait(false);

        // 2. Стеля кількості рядків — захист від того, щоб таблиця на 5000
        //    рядків не з'явилася випадково і не зруйнувала бюджет читання зрізу.
        if (table.MaxDynamicRows is { } max && existing.Count >= max)
        {
            throw new Errors.BusinessRuleException(
                "ECR-ROW-0409",
                $"Досягнуто межу динамічних рядків таблиці {table.Code}: {max}.");
        }

        // 3. Ключ: заданий користувачем або GUID у форматі "N" (ФВ-2.5).
        var key = requestedKey ?? RowKey.NewDynamic();

        if (existing.ContainsKey(key.Value))
        {
            throw new Errors.BusinessRuleException(
                "ECR-ROW-0409", $"Рядок із ключем {key.Value} у цій таблиці вже існує.");
        }

        // 4. Id береться з SEQUENCE ДО вставки — саме це дозволяє вантажити
        //    рядок і його комірки одним проходом SqlBulkCopy.
        await rowStore.CreateRowAsync(tableInstanceId, PeriodKeyOf(instance), key,
                                      ordinal: existing.Count + 1, ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return key;
    }

    // Період екземпляра таблиці зберігається разом із ним; на Етапі 1 його
    // несе адреса рядків, тому беремо його з першого відомого джерела.
    private static Domain.ValueObjects.PeriodKey PeriodKeyOf(Ports.TableInstanceRef instance)
        => new(instance.PeriodKey);
}
