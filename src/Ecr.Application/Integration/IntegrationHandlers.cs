// src/Ecr.Application/Integration/IntegrationHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>
/// Запуск збору для сутності джерела. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Парного «опублікувати в AF» не існує: PI AF — виключно джерело (D-44).
/// <para>
/// Збір ідемпотентний за природним ключем: повторний запуск того самого
/// діапазону не дублює даних (ФВ-11.3). Відмова джерела — не збій операції:
/// діапазон іде в наздоганяння, а прогін завершується.
/// </para>
/// </remarks>
public sealed class CollectFromSourceHandler(
    ICollectionStore sources,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Ставить збір у чергу.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="fromUtc">Початок діапазону; <c>null</c> — за розкладом.</param>
    /// <param name="toUtc">Кінець діапазону; <c>null</c> — «зараз».</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    /// <exception cref="NotFoundException">Сутності джерела немає або вона вимкнена.</exception>
    public async Task<string> HandleAsync(
        int sourceEntityId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Існування сутності перевіряється ТУТ. Задача, поставлена на
        // неіснуючу сутність, завершилася б помилкою через хвилину, і
        // користувач шукав би причину в джерелі, а не в номері, який щойно
        // ввів.
        _ = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.");

        return await jobs
            .EnqueueAsync<ICollectionJob>(new CollectionTask(sourceEntityId, fromUtc, toUtc), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Завдання на збір.</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="FromUtc">Початок; <c>null</c> — за <c>LookbackDays</c> розкладу.</param>
/// <param name="ToUtc">Кінець; <c>null</c> — «зараз».</param>
public sealed record CollectionTask(int SourceEntityId, DateTime? FromUtc, DateTime? ToUtc);

/// <summary>
/// Перелік сутностей збору. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Ендпоінт додано після аудиту (`A7-03`): екран конфігуратора викликав
/// <c>GET /api/v1/sources</c>, якого не існувало, і завжди показував помилку.
/// Таблиця ендпоінтів контракту оголошувала лише запуск збору — але вимога
/// ФВ-13.13 («імена обираються зі списку, а не вводяться руками») без
/// переліку невиконувана.
/// </remarks>
public sealed class ListSourceEntitiesHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Віддає сутності разом зі станом останнього прогону і прогалиною.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<SourceEntityStatus>> HandleAsync(CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await sources.ListSourceEntitiesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Стан фонової задачі. Право <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// Усе, що довше за ~5 с, іде у фон і повертає <c>jobId</c>; саме через цей
/// шлях інтерфейс показує прогрес.
/// </remarks>
public sealed class GetJobStatusHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд стану системи (`02-contracts.md` §9).</summary>
    public const string Permission = "System.ViewHealth";

    /// <summary>Стан задачі; <c>null</c> — такої задачі немає.</summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Невідома задача — це <c>null</c>, який контролер перетворює на 404, а
    /// не порожній стан: інакше клієнт нескінченно опитував би ідентифікатор,
    /// якого не існує, і показував би вічний прогрес.
    /// </remarks>
    public async Task<JobStatus?> HandleAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var status = await jobs.GetStatusAsync(jobId, ct).ConfigureAwait(false);

        return string.Equals(status.State, "Unknown", StringComparison.Ordinal) ? null : status;
    }
}
