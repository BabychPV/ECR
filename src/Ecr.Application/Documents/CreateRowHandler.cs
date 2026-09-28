using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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
    IClock clock,
    ISheetEditGate sheetGate)
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
                $"Екземпляр таблиці {tableInstanceId} не належить документу {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableInstanceNotInDocument",
                    ["tableInstanceId"] = tableInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Видимість документа — до будь-якої відмови про таблицю (V-02, той
        // самий клас, що й у `PatchCellsHandler`). Нижче відмови кажуть режим
        // таблиці, межу рядків і чи зайнятий ключ — і все це діставалося
        // користувачеві із забороною на проєкт, бо права питалися лише
        // наприкінці. Відповідь — як на читання: 404, не 403.
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);

        var table = snapshot.Sheets
            .SelectMany(sh => sh.Tables)
            .FirstOrDefault(t => t.Id == instance.TableDefId)
            // ⚠ Той самий факт, що й `ColumnDefHandlers.FindTable`/
            // `ValidationRuleHandlers` (2026-09-23): «таблиці з таким Id немає
            // в цій версії», незалежно від того, звідки до нього дійшли —
            // тому наявний ключ, а не новий.
            ?? throw new Errors.NotFoundException(
                "ECR-TMPL-0404", $"Таблиці {instance.TableDefId} немає в структурі версії {instance.TemplateVersionId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.table",
                    ["tableDefId"] = instance.TableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["versionId"] = instance.TemplateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

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
                $"Таблиця {table.Code} має RowMode = {table.RowMode}: рядки задані шаблоном і не додаються.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ROW-0409.rowsFromTemplate",
                    ["tableCode"] = table.Code,
                    ["rowMode"] = table.RowMode.ToString(),
                });
        }

        // 2. Ключ: заданий користувачем або GUID у форматі "N" (ФВ-2.5). Потрібен
        //    уже тут — від нього залежить рішення про права (`RowIsReadOnly`).
        var key = requestedKey ?? RowKey.NewDynamic();

        // 3. Права — ДО транзакції й ВИНЯТКОВОГО блокування аркуша (P3).
        await RequireCreateAllowedAsync(profile, tableInstanceId, key, ct).ConfigureAwait(false);

        // ⛔ C3. Решта — стан аркуша, стеля рядків, дубль ключа, `ordinal` і сама
        // вставка — ОДНІЄЮ транзакцією під блокуванням аркуша × періоду. Доти
        // все читалося поза транзакцією: поки подання тримало аркуш, POST row
        // бачив під RCSI `Draft` і вставляв рядок, якого немає в зрізі подання;
        // а два одночасні POST обидва бачили `existing.Count < max` і обидва
        // брали `ordinal = Count + 1` — стелю перевищено, порядковий номер
        // задвоєно (`CreateRowRaceTests`).
        await uow.ExecuteInTransactionAsync(
            innerCt => CreateUnderLockAsync(instance, table, tableInstanceId, key, profile, innerCt),
            ct).ConfigureAwait(false);

        return key;
    }

    /// <summary>Рішення служби доступу на створення рядка з цим ключем.</summary>
    /// <remarks>
    /// <para>
    /// ⛔ До цього місця обробник колись не звертався до
    /// <c>IAccessDecisionService</c> ЖОДНОГО разу: залежність була вприснута й
    /// не читана (<c>CS9113</c>). Наслідок вимірювався живим прогоном: рядок
    /// додавався в ЗАКРИТИЙ період. Питається ДО вставки, з уже відомим ключем.
    /// </para>
    /// <para>
    /// ⚠ Питається рішення на РЯДОК, не на комірки: колонок у цей момент ніхто
    /// не назвав, і вимагати дозволу на кожну означало б відмовляти там, де одна
    /// колонка обчислювана. Комірки перевіряє той, хто їх пише, —
    /// <c>PatchCellsHandler</c>.
    /// </para>
    /// <para>
    /// ✎ P3: винесено З-ПІД виняткового блокування аркуша. Рішення — кілька
    /// читань (екземпляр, знімок, документ + проєкт + період + стан аркуша,
    /// правила періоду), і під замком вони тримали правки, подання й інші
    /// створення рядків цього аркуша. Чому це не відкриває TOCTOU: з усього,
    /// що рішення читає, блокування аркуша × періоду серіалізує ЛИШЕ стан
    /// аркуша (його змінює подання, яке теж бере це блокування), — і стан під
    /// замком перевіряється окремо (<c>EnterEditAsync</c> у
    /// <see cref="CreateUnderLockAsync"/>). Стан проєкту й періоду (закриття,
    /// архівація, перевідкриття), гранти й правила доступу до періоду цього
    /// блокування не беруть узагалі, тож і під замком рішення бачило їх лише
    /// як знімок під RCSI — захисту, який тут знято, у них не було. Опис рядка
    /// в шаблоні (<c>RowIsReadOnly</c>) — з незмінної версії шаблону.
    /// </para>
    /// </remarks>
    private async Task RequireCreateAllowedAsync(
        AccessProfile profile, long tableInstanceId, RowKey key, CancellationToken ct)
    {
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
                    ["messageKey"] = "err.ECR-ACCS-0403.addRowDenied",
                    // ⛔ B-06: без `detail` — він дублював стандартний член
                    // `problem+json` українським реченням (див. `PatchCellsHandler`).
                    ["reason"] = decision.Reason.ToString(),
                });
        }
    }

    /// <summary>
    /// Перевірки й вставка рядка під блокуванням аркуша; кличеться лише
    /// всередині транзакції <see cref="HandleAsync"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Блокування ВИНЯТКОВЕ (<c>EnterSubmitAsync</c>), а не спільне, як у
    /// правки комірок: спільне сумісне саме з собою, тож два POST row його
    /// тримали б одночасно, і «порахував — вставив» лишилося б гонкою. Виняткове
    /// серіалізує створення рядків між собою, із правками й поданням ЦЬОГО
    /// аркуша за ЦЕЙ період; сусідні аркуші й періоди не чекають. Стан аркуша
    /// читає <c>EnterEditAsync</c> — той самий власник транзакції вже тримає
    /// виняткове, тож спільне видається одразу й лише читає стан після блокування.
    /// </remarks>
    private async Task CreateUnderLockAsync(
        Ports.TableInstanceRef instance, TableDef table, long tableInstanceId, RowKey key,
        AccessProfile profile, CancellationToken ct)
    {
        await sheetGate
            .EnterSubmitAsync(instance.DocumentId, table.SheetDefId, PeriodKeyOf(instance), ct)
            .ConfigureAwait(false);
        var status = await sheetGate
            .EnterEditAsync(instance.DocumentId, table.SheetDefId, PeriodKeyOf(instance), ct)
            .ConfigureAwait(false);

        var stateReason = status switch
        {
            DocumentStatus.Submitted => (EditDenyReason?)EditDenyReason.DocumentSubmitted,
            DocumentStatus.Approved => EditDenyReason.DocumentApproved,
            _ => null,
        };

        if (stateReason is { } denied)
        {
            // ⚠ Той самий код, ключ і форма, що й відмова служби доступу
            // (`RequireCreateAllowedAsync`, до блокування):
            // для людини це та сама відмова, на якому б кроці її не спіймали.
            throw new Errors.AccessDeniedException(
                "ECR-ACCS-0403",
                $"Рядок у цю таблицю додати не можна: {denied}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ACCS-0403.addRowDenied",
                    ["reason"] = denied.ToString(),
                });
        }

        var existing = await rowStore.GetRowIdsAsync(tableInstanceId, PeriodKeyOf(instance), ct).ConfigureAwait(false);

        // 4. Стеля кількості рядків — захист від того, щоб таблиця на 5000
        //    рядків не з'явилася випадково і не зруйнувала бюджет читання зрізу.
        if (table.MaxDynamicRows is { } max && existing.Count >= max)
        {
            throw new Errors.BusinessRuleException(
                "ECR-ROW-0409",
                $"Досягнуто межу динамічних рядків таблиці {table.Code}: {max}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ROW-0409.rowLimitReached",
                    ["tableCode"] = table.Code,
                    ["max"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // 5. Дубль ключа — під блокуванням: два одночасні POST з тим самим
        //    ключем серіалізуються тут.
        if (existing.ContainsKey(key.Value))
        {
            throw new Errors.BusinessRuleException(
                "ECR-ROW-0409", $"Рядок із ключем {key.Value} у цій таблиці вже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ROW-0409.rowKeyExists",
                    ["rowKey"] = key.Value,
                });
        }

        // 6. Id береться з SEQUENCE ДО вставки — саме це дозволяє вантажити
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
    }

    // Період екземпляра таблиці зберігається разом із ним; на Етапі 1 його
    // несе адреса рядків, тому беремо його з першого відомого джерела.
    private static Domain.ValueObjects.PeriodKey PeriodKeyOf(Ports.TableInstanceRef instance)
        => new(instance.PeriodKey);
}
