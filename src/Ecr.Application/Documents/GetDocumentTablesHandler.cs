// src/Ecr.Application/Documents/GetDocumentTablesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
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
    IMetadataCache metadata,
    IAccessDecisionService access,
    ICurrentUser currentUser)
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
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

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

        var byTableDef = instances.ToDictionary(i => i.TableDefId);
        var result = new List<DocumentTableDto>(instances.Count);

        foreach (var sheet in snapshot.Sheets.Where(s => !s.IsDeleted).OrderBy(s => s.Ordinal))
        {
            foreach (var table in sheet.Tables.Where(t => !t.IsDeleted).OrderBy(t => t.Ordinal))
            {
                if (!byTableDef.TryGetValue(table.Id, out var instance))
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
                    table.Ordinal));
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
public sealed record DocumentTableDto(
    int SheetDefId,
    string SheetCode,
    Domain.ValueObjects.LocalizedText SheetNameL10n,
    int SheetOrdinal,
    long TableInstanceId,
    int TableDefId,
    string TableCode,
    Domain.ValueObjects.LocalizedText TableNameL10n,
    int TableOrdinal);
