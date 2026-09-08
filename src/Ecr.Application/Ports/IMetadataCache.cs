// src/Ecr.Application/Ports/IMetadataCache.cs

using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Кеш метаданих шаблону. Опублікована версія структурно незмінна, тому ключ
/// <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна правка
/// створює новий ключ, а не псує старий (D-16). Це прибирає когерентність кешу
/// між інстансами як клас проблеми.
/// </summary>
public interface IMetadataCache
{
    /// <summary>Повна структура версії шаблону.</summary>
    public Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Скидає запис. Потрібно після <c>Publish</c>, міграції — і після
    /// кожної структурної правки чернетки (<c>PUT/DELETE …/sheets/{code}</c>
    /// та наступні зрізи авторства структури).
    /// </summary>
    /// <remarks>
    /// ⚠ До появи авторства структури через API це справді було потрібно
    /// «лише після Publish»: єдиним, хто міняв <c>SheetDef</c> і сусідів поза
    /// тестами, був офлайновий <c>Ecr.DataGen</c>, який працює до першого
    /// прогріву кешу. Структурний <c>PUT</c> у працюючому інстансі зламав би
    /// це припущення мовчки: додавання аркуша не піднімає
    /// <c>PresentationRevision</c> (не презентаційна правка), тож без явного
    /// скидання прогрітий кеш віддавав би знімок ДО правки, доки версію не
    /// опублікують.
    /// </remarks>
    public Task InvalidateAsync(int templateVersionId, CancellationToken ct);
}
