// src/Ecr.Application/JobActorRegistration.cs
using Ecr.Application.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Application;

/// <summary>
/// Спільне складання «хто діє» для хостів, що виконують фонові задачі: Api і
/// дочірній воркер <c>Ecr.Worker</c> (ФВ-9.8, D-206, крок I1).
/// </summary>
public static class JobActorRegistration
{
    /// <summary>
    /// Реєструє <see cref="JobActorScope"/> і <see cref="ICurrentUser"/> як
    /// <see cref="JobAwareCurrentUser"/> над <typeparamref name="TRequestUser"/>.
    /// </summary>
    /// <typeparam name="TRequestUser">
    /// Користувач «запиту» цього хоста: в Api — користувач cookie, у воркері —
    /// ніхто (HTTP-запиту там немає). Реєструє його сам викликач.
    /// </typeparam>
    /// <param name="services">Колекція сервісів.</param>
    /// <remarks>
    /// ⛔ Одне місце на обидва хости: <see cref="JobActorScope"/> і обгортка
    /// мусять бути ОДНИМ екземпляром тримача на scope. Дві копії цих рядків (в
    /// Api і у воркері) розійшлися б саме в тому, що видно лише в задачі, —
    /// від чийого імені вона пише (F-01).
    /// </remarks>
    public static IServiceCollection AddEcrJobActor<TRequestUser>(this IServiceCollection services)
        where TRequestUser : class, ICurrentUser
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            sp.GetRequiredService<TRequestUser>(),
            sp.GetRequiredService<JobActorScope>()));

        return services;
    }
}
