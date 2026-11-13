// src/Ecr.Application/Ports/IReportViewGenerator.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Генерує «широкі» вʼюхи сирих даних <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;_v&lt;Версія&gt;</c>
/// для SSRS — по одній на таблицю опублікованої версії шаблону
/// (<c>ФВ-10.2</c>, <c>ФВ-10.4</c>; рішення людини 2026-09-30: SSRS отримує
/// сирі дані й будує звіти сам).
/// </summary>
/// <remarks>
/// ⛔ Застосунок DDL не виконує (<c>D-14</c>, <c>D-66</c>): реалізація лише
/// ВИКЛИКАЄ процедуру <c>rpt.usp_GenerateTemplateViews</c>, а вʼюху створює
/// вона — від імені власника. Виклик ідемпотентний: незмінна версія дає
/// побайтно ту саму вʼюху, і повторний виклик її не чіпає.
/// </remarks>
public interface IReportViewGenerator
{
    /// <summary>Створює або оновлює вʼюхи таблиць опублікованих версій шаблонів.</summary>
    /// <param name="templateVersionId">Версія шаблону; <c>null</c> — усі опубліковані.</param>
    /// <param name="ct">Скасування.</param>
    public Task GenerateAsync(int? templateVersionId, CancellationToken ct);

    /// <summary>
    /// ✎ P-4b P2: лише вʼюхи <c>_vN</c> однієї версії (<c>rpt.usp_GenerateTemplateViews</c>), без стабільних вʼюх шаблону.
    /// </summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Стабільні вʼюхи (<c>rpt.usp_GenerateStableViews</c>) з версією перебудовують УСІ вʼюхи шаблону по всіх його
    /// версіях — задача шаблону кличе їх ОДИН раз (<see cref="GenerateStableViewsAsync"/>), а не на кожну версію
    /// (F13 на великому бекапі: 25 версій — 42 хв, ~4 хв на версію). Типова реалізація — повна генерація версії (обидві
    /// процедури): для реалізацій без поділу це надлишково, але не губить вʼюх.
    /// </remarks>
    public Task GenerateVersionViewsAsync(int templateVersionId, CancellationToken ct) => GenerateAsync(templateVersionId, ct);

    /// <summary>✎ P-4b P2: лише стабільні вʼюхи шаблону (без версії) — один виклик на прогін задачі шаблону.</summary>
    /// <param name="templateId">Шаблон (ключ збою на картці <c>reportviews</c>).</param>
    /// <param name="anchorVersionId">Будь-яка опублікована/застаріла версія цього шаблону — параметр процедури.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>Типова реалізація нічого не робить: типова <see cref="GenerateVersionViewsAsync"/> уже будує й стабільні.</remarks>
    public Task GenerateStableViewsAsync(int templateId, int anchorVersionId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// ✎ P-4b P3: усі шаблони (старт застосунку, задача з ціллю <see cref="ReportViewsTarget.All"/>).
    /// </summary>
    /// <param name="onlyIfChanged">Пропустити, якщо входи вʼюх не змінилися з останньої успішної повної генерації.</param>
    /// <param name="ct">Скасування.</param>
    public Task GenerateAllAsync(bool onlyIfChanged, CancellationToken ct) => GenerateAsync(null, ct);
}
