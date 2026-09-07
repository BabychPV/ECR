using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Додає рядок у динамічну таблицю.</summary>
public sealed class CreateRowHandler(
    IRowStore rowStore,
    IDocumentStore documents,
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

        // ⛔ Належність екземпляра таблиці документові з МАРШРУТУ. Без цієї
        // звірки шлях у URL декоративний: клієнт указав би чужий
        // `TableInstanceId` і писав би в чужий документ, маючи право лише на
        // свій. `Patch` цю перевірку робить і пояснює навіщо
        // (`CellsController.Patch`); сюди вона не доїхала, хоча `documentId`
        // сюди приходив — і не читався взагалі.
        //
        // ⚠ Стоїть в ОБРОБНИКУ, а не в контролері, як у `Patch`: обробник —
        // єдина точка, повз яку не пройде другий виклик.
        if (instance.DocumentId != documentId)
        {
            throw new Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Екземпляр таблиці {tableInstanceId} не належить документу {documentId}.");
        }

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);

        var table = snapshot.Sheets
            .SelectMany(sh => sh.Tables)
            .FirstOrDefault(t => t.Id == instance.TableDefId)
            ?? throw new Errors.NotFoundException(
                "ECR-TMPL-0404", $"Таблиці {instance.TableDefId} немає в структурі версії {instance.TemplateVersionId}.");

        // 1. Рядок можна додати лише туди, де це дозволяє режим. У Fixed склад
        //    рядків заданий шаблоном, і поява «зайвого» зламала б і формули з
        //    діапазонами, і звірку з еталоном.
        //
        // ⛔ Питаємо ДОМЕН (`AllowsDynamicRows`), а не режим напряму. Тут
        // стояло `RowMode != Dynamic` — власне, вужче визначення того самого
        // поняття, і через нього режим `Mixed` не приймав жодного рядка:
        // «фіксовані плюс свої» на практиці дорівнювало `Fixed`. Домен же
        // вважає `Mixed` динамічним, і на це спирається прив'язка виразів —
        // тобто два місця системи розуміли один режим по-різному.
        if (!table.AllowsDynamicRows)
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

        // 4. Права. Питаються ДО вставки і з уже відомим ключем.
        //
        // ⛔ До цього місця обробник не звертався до `IAccessDecisionService`
        // ЖОДНОГО разу: залежність була вприснута й не читана, і компілятор
        // казав це прямо і безкоштовно (`CS9113`). Наслідок вимірювався живим
        // прогоном: рядок додавався в ЗАКРИТИЙ період. `EditRules` про ту саму
        // перевірку каже: «Закритий період блокує ВСІХ, включно з `Manage`. Це
        // головна перевірка моделі доступу: якщо вона пропускає, зламана вся
        // модель, і жоден інший тест цього не покаже».
        //
        // ⚠ Питається рішення на РЯДОК, не на комірки: колонок у цей момент
        // ніхто не назвав, і вимагати дозволу на кожну означало б відмовляти
        // там, де одна колонка обчислювана. Комірки перевіряє той, хто їх
        // пише, — `PatchCellsHandler`.
        //
        // ⚠ Ключ уже відомий: від нього залежить `RowIsReadOnly` — опис рядка
        // в шаблоні шукається саме за ключем.
        var decisions = await access
            .CanCreateRowsAsync(profile, tableInstanceId, [key.Value], ct)
            .ConfigureAwait(false);

        // ⛔ Немає рішення — ВІДМОВА. «Рішення немає, отже можна» — це той
        // самий дефект, лише переписаний акуратніше.
        var decision = decisions.TryGetValue(key.Value, out var found)
            ? found.Row
            : EditDecision.Deny(EditDenyReason.NoGrant, $"Рішення про доступ на рядок {key.Value} не отримано.");

        if (!decision.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-ACCS-0403",
                $"Рядок у цю таблицю додати не можна: {decision.Reason}.",
                new Dictionary<string, object?>
                {
                    ["reason"] = decision.Reason.ToString(),
                    ["detail"] = decision.Detail
                });
        }

        // 5. Id береться з SEQUENCE ДО вставки — саме це дозволяє вантажити
        //    рядок і його комірки одним проходом SqlBulkCopy.
        await rowStore.CreateRowAsync(tableInstanceId, PeriodKeyOf(instance), key,
                                      ordinal: existing.Count + 1, ct).ConfigureAwait(false);

        // ⛔ Новий рядок — це зміна документа (`H-23d`). Без цього «додав рядок»
        // не змінювало ані `ModifiedAt`, ані автора: у переліку документ
        // виглядав недоторканим із дня створення.
        //
        // ⚠ Адресат береться з `instance.DocumentId`, а не з параметра
        // `documentId`: перший прийшов зі сховища разом із самим екземпляром
        // таблиці, другий — із запиту, і збігаються вони лише доти, доки
        // клієнт не помилиться.
        await documents
            .TouchAsync(instance.DocumentId, profile.UserId, clock.UtcNow, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return key;
    }

    // Період екземпляра таблиці зберігається разом із ним; на Етапі 1 його
    // несе адреса рядків, тому беремо його з першого відомого джерела.
    private static Domain.ValueObjects.PeriodKey PeriodKeyOf(Ports.TableInstanceRef instance)
        => new(instance.PeriodKey);
}
