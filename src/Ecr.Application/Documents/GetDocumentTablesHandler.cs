// src/Ecr.Application/Documents/GetDocumentTablesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Екземпляри таблиць документа за період, згруповані за аркушами.
/// </summary>
/// <remarks>
/// ⛔ Ендпоінт додано після аудиту (`A7-05`). Екран документа не міг
/// працювати взагалі: <c>GET /api/v1/documents/{id}</c> віддає
/// <c>DocumentSummary</c> — бізнес-ключ, кількість аркушів і зведений стан, —
/// а grid потребує <c>TableInstanceId</c>, якого там немає. Структуру версії
/// шаблону віддає інший ендпоінт, але й у ній є лише <b>описи</b> таблиць, не
/// екземпляри.
/// <para>
/// ⚠ Екземпляр існує <b>на кожен період окремо</b> (R-A6), тому період тут —
/// не фільтр показу, а частина адреси даних: без нього питання «які таблиці в
/// документі» не має відповіді.
/// </para>
/// </remarks>
public sealed class GetDocumentTablesHandler(
    IRowStore rowStore,
    IPermitBindingStore permitBindings,
    IMetadataCache metadata,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    // RS: назви груп і копії аркушів; необов'язковий лише заради тестів, що конструюють обробник вручну (без нього копій
    // у відповіді немає — лише оригінали).
    IDocumentSheetCopyStore? sheetCopies = null)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Віддає таблиці документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період; екземпляри існують окремо на кожен.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<DocumentTableDto>> HandleAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ І ГРАНТ на проєкт (Q-172, аудит фази 2). Право саме по собі каже
        // «цей користувач узагалі працює з документами», а не «з ЦИМ».
        // Метод іще й МАТЕРІАЛІЗУЄ екземпляри таблиць нижче — без цієї
        // перевірки будь-хто із загальним `Document.View` міг перелічити
        // `tableInstanceId` чужого документа (і зробити це для документа, що
        // ще не відкривали, — власним записом).
        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var key = PeriodKey.Parse(periodKey);

        // ⛔ Екземпляри створюються ПЕРЕД читанням, при першому відкритті
        // документа за цей період (`A7-30`). Доти їх не створював ніхто: у
        // всій системі `doc.TableInstance` лише читався, і документ, створений
        // через API, лишався без жодної таблиці назавжди.
        //
        // ⚠ Це запис на шляху читання — свідомо, за тією самою схемою, що й
        // побудова календаря періодів: виклик ідемпотентний, а альтернатива
        // (заводити тисячу рядків наперед на кожен документ) коштує більше й
        // здебільшого дарма.
        await rowStore.EnsureTableInstancesAsync(documentId, key, ct).ConfigureAwait(false);

        var instances = await rowStore
            .GetTableInstancesAsync(documentId, key, ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            // ⚠ Порожній перелік, а не 404: документ існує, просто за цей
            // період його ще не відкривали. Помилка тут виглядала б як
            // «документа немає», і користувач шукав би його в іншому місці.
            return [];
        }

        var snapshot = await metadata
            .GetAsync(instances[0].TemplateVersionId, ct)
            .ConfigureAwait(false);

        // ⛔ S6 (ФВ-6.6): таблиця під забороною (своєю чи аркуша) не з'являється
        // в переліку — ні назвою, ні кодом, ні `tableInstanceId`. Зріз на неї
        // однаково дав би 404, а назва в навігації сама розповідала б про неї.
        var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(key);

        // PM-11b: ознака «таблиця з дозволу» - ОДИН запит на всю відповідь, за власною версією документа (не проєкту).
        // Лише код/режим/тип: дані дозволу (номери, ліміти, рядки) лишаються за Registry.View. Права окремо не питаємо -
        // це та сама структура версії, що й назви таблиць вище, а читання вже вимагало Document.View.
        //
        // PM-11c: у версії без чинних прив'язок (Land та решта) запиту немає взагалі - прапор бере знімок метаданих, який
        // уже прочитано вище. Прив'язки міняються лише в чернетці з скиданням знімка (див. HasActivePermitBindings).
        var bindings = snapshot.HasActivePermitBindings
            ? (await permitBindings
                .ListActiveSummariesAsync(instances[0].TemplateVersionId, ct)
                .ConfigureAwait(false))
                .ToDictionary(b => b.SheetTableDefId)
            : new Dictionary<int, PermitBindingSummary>();

        // ⛔ RS: екземпляр — за (таблиця, копія); `0` — оригінал. Копії стоять ПІСЛЯ оригінала свого аркуша за номером
        // (`sheetOrdinal`, потім `copyOrdinal`) і ділять з ним `sheetCode`/`sheetOrdinal`.
        var byTableCopy = instances.ToDictionary(i => (i.TableDefId, CopyKey: i.SheetCopyId ?? 0));
        var groupOf = sheetCopies is null
            ? new Dictionary<int, SheetCopyGroupInfo>()
            : (await sheetCopies.ListGroupsAsync(documentId, ct).ConfigureAwait(false)).ToDictionary(g => g.SheetDefId);
        // TC02: скільки живих повідомлень чату в кожній копії — для підтвердження видалення (ОДИН запит до зведень чату,
        // лише коли в документі є копії).
        var chatCounts = sheetCopies is not null && groupOf.Values.Any(g => g.Copies.Count > 0)
            ? await sheetCopies.ChatMessageCountsAsync(documentId, ct).ConfigureAwait(false)
            : new Dictionary<long, int>();
        var result = new List<DocumentTableDto>(instances.Count);

        foreach (var sheet in snapshot.Sheets.Where(s => !s.IsDeleted).OrderBy(s => s.Ordinal))
        {
            groupOf.TryGetValue(sheet.Id, out var group);
            var groupName = SheetCopyNaming.Effective(sheet.NameL10n, group?.NameOverride);
            var hasCopies = group is { Copies.Count: > 0 };

            var copyKeys = Enumerable.Repeat(0L, 1)
                .Concat(sheet.Tables
                    .Where(t => !t.IsDeleted)
                    .SelectMany(t => instances.Where(i => i.TableDefId == t.Id && i.SheetCopyId is not null))
                    .Select(i => i.SheetCopyId!.Value)
                    .Distinct()
                    .OrderBy(id => group?.Copies.FirstOrDefault(c => c.SheetCopyId == id)?.Ordinal ?? short.MaxValue)
                    .ThenBy(id => id));

            foreach (var copyKey in copyKeys)
            {
                var ordinal = copyKey == 0
                    ? DocumentSheet.OriginalOrdinal
                    : group?.Copies.FirstOrDefault(c => c.SheetCopyId == copyKey)?.Ordinal
                      ?? (short)(DocumentSheet.OriginalOrdinal + 1);
                var display = SheetCopyNaming.Display(groupName, ordinal, hasCopies);

                foreach (var table in sheet.Tables.Where(t => !t.IsDeleted && readable.CanReadTable(t.Id)).OrderBy(t => t.Ordinal))
                {
                    if (!byTableCopy.TryGetValue((table.Id, copyKey), out var instance))
                    {
                        // Таблиця описана в шаблоні, але екземпляра за цей період
                        // немає: аркуш до документа не входить (ФВ-3.2).
                        continue;
                    }

                    result.Add(new DocumentTableDto(
                        sheet.Id,
                        sheet.Code,
                        sheet.NameL10n,
                        sheet.Ordinal,
                        instance.TableInstanceId,
                        table.Id,
                        table.Code,
                        table.NameL10n,
                        table.Ordinal,

                    // ⛔ Віддається ВІДПОВІДЬ домену, а не режим таблиці.
                    // Клієнту потрібна кнопка «додати рядок», і питання, на
                    // яке він відповідає, — «чи можна тут додавати». Віддати
                    // `RowMode` означало б попросити клієнта скласти
                    // `Dynamic || Mixed` самому, тобто завести ДРУГЕ
                    // визначення того самого правила. Перше вже розійшлося
                    // саме з собою всередині сервера, і коштувало це режиму
                    // `Mixed` цілком.
                    table.AllowsDynamicRows,
                    table.MaxDynamicRows,

                    // Q5: рядки з дозволу — лише оригінал; копія — порожня форма без прив'язки.
                    copyKey == 0 && bindings.TryGetValue(table.Id, out var binding)
                        ? new DocumentTablePermitBindingDto(binding.Code, binding.Mode, binding.PermitTypeCode)
                        : null,
                    instance.SheetCopyId,
                    ordinal,
                    display,
                    sheet.IsCopyable,
                    copyKey == 0 ? null : chatCounts.GetValueOrDefault(copyKey)));
                }
            }
        }

        return result;
    }
}

/// <summary>Екземпляр таблиці разом з аркушем, якому він належить.</summary>
/// <param name="SheetDefId">Аркуш; ним подають і затверджують.</param>
/// <param name="SheetCode">Код аркуша; він же ключ у <c>DocumentSummary.SheetStates</c>.</param>
/// <param name="SheetNameL10n">Назва аркуша мовами каталогу.</param>
/// <param name="SheetOrdinal">Порядок аркуша.</param>
/// <param name="TableInstanceId">Екземпляр таблиці — саме його читає й пише grid.</param>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="TableNameL10n">Назва таблиці мовами каталогу.</param>
/// <param name="TableOrdinal">Порядок таблиці в аркуші.</param>
/// <param name="AllowsDynamicRows">Чи додає рядки користувач (<c>ФВ-3.2</c>).</param>
/// <param name="MaxDynamicRows">Стеля кількості рядків; <c>null</c> — без стелі.</param>
/// <param name="PermitBinding">
/// PM-11b: таблиця прив'язана до дозволу у версії шаблону ДОКУМЕНТА; <c>null</c> - ні. Лише ознака (код, режим, тип) -
/// даних дозволу тут немає й бути не може (вони за <c>Registry.View</c>).
/// </param>
/// <param name="SheetCopyId">
/// RS: копія аркуша (<c>doc.DocumentSheet.Id</c>), якій належить екземпляр; <c>null</c> — оригінал. Саме нею подають,
/// затверджують і повертають копію (<c>sheetCopyId</c> у запитах), а в <c>SheetStates</c> вона має ключ <c>КОД#sheetCopyId</c>.
/// </param>
/// <param name="CopyOrdinal">RS: номер у групі — 1 оригінал, 2… копії; копії діляться <c>sheetCode</c>/<c>sheetOrdinal</c> з оригіналом.</param>
/// <param name="DisplayNameL10n">
/// RS: назва аркуша для показу — «&lt;Назва&gt; N» (оригінал «&lt;Назва&gt; 1», коли копії є; без копій — назва як є).
/// <paramref name="SheetNameL10n"/> лишається назвою з шаблону.
/// </param>
/// <param name="IsCopyable">RS: шаблон дозволяє копіювати аркуш («Додати копію»).</param>
/// <param name="ChatMessageCount">
/// TC02: живих повідомлень чату цієї копії в усіх її таблицях (видалення копії забирає їх разом з даними); <c>null</c> — оригінал,
/// 0 — чат копії порожній. Клієнт показує число ДО підтвердження видалення.
/// </param>
public sealed record DocumentTableDto(
    int SheetDefId,
    string SheetCode,
    Domain.ValueObjects.LocalizedText SheetNameL10n,
    int SheetOrdinal,
    long TableInstanceId,
    int TableDefId,
    string TableCode,
    Domain.ValueObjects.LocalizedText TableNameL10n,
    int TableOrdinal,
    bool AllowsDynamicRows,
    int? MaxDynamicRows,
    DocumentTablePermitBindingDto? PermitBinding = null,
    long? SheetCopyId = null,
    int CopyOrdinal = 1,
    Domain.ValueObjects.LocalizedText? DisplayNameL10n = null,
    bool IsCopyable = false,
    int? ChatMessageCount = null);

/// <summary>Ознака прив'язки таблиці документа до дозволу (PM-11b): без даних дозволу.</summary>
/// <param name="Code">Код прив'язки.</param>
/// <param name="Mode"><c>Rows</c> або <c>Formulas</c>.</param>
/// <param name="PermitTypeCode">Код типу дозволу.</param>
public sealed record DocumentTablePermitBindingDto(string Code, string Mode, string PermitTypeCode);
