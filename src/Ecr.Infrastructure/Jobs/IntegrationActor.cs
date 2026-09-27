// src/Ecr.Infrastructure/Jobs/IntegrationActor.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Технічний автор фонових задач інтеграції — запис <c>svc-integration</c>
/// (<c>09-seed.sql</c>, «Технічний обліковий запис інтеграції»).
/// </summary>
/// <remarks>
/// ⛔ Дефект, який це закриває: сід заводив <c>svc-integration</c>, коментар
/// <see cref="MaterializeCollectedDataJob"/> обіцяв запис «під технічним
/// записом», а <see cref="JobActorScope.Enter"/> не викликав ніхто, крім
/// <see cref="ExcelImportJob"/>. У задачі Quartz HTTP-запиту немає, тож
/// <c>PatchCellsHandler</c> бачив <c>UserId = null</c> і відмовляв
/// <c>ECR-AUTH-0401</c> — матеріалізація PI у проді не записувала нічого.
/// Тести цього не бачили: реальний <c>IntegrationCellPatcher</c> з реальним
/// обробником запису не ганяв жоден.
///
/// ⚠ Id береться з бази за ЛОГІНОМ, а не літералом: Id — IDENTITY, і в кожній
/// розгорнутій базі він свій (порядок сіду, ручні правки). Літерал збігався б
/// рівно до першої бази, де це не так, і тоді писав би від імені чужої людини.
///
/// ⛔ Немає запису або його вимкнено — явна відмова з кодом, а не мовчазний
/// запис «від нікого» і не підміна іншим користувачем: питання «хто змінив
/// цю комірку» мусить мати відповідь (<c>09-seed.sql</c>).
///
/// ⚠ Провайдер спільний навмисно: наступна задача інтеграції, якій потрібен
/// автор, бере його звідси, а не повторює пошук за логіном у себе.
/// </remarks>
public sealed class IntegrationActor(EcrDbContext db, JobActorScope scope)
{
    /// <summary>Логін технічного запису — той самий, що в <c>09-seed.sql</c>.</summary>
    public const string UserName = "svc-integration";

    /// <summary>Встановлює <c>svc-integration</c> автором задачі до звільнення результату.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звільнення повертає scope у стан «поза задачею».</returns>
    /// <exception cref="NotFoundException">
    /// <c>ECR-SEC-0404</c>: запису <c>svc-integration</c> немає або він вимкнений.
    /// </exception>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        var account = await db.Users
            .AsNoTracking()
            .Where(u => u.UserName == UserName)
            .Select(u => new { u.Id, u.IsActive })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (account is not { IsActive: true })
        {
            throw new NotFoundException(
                ErrorCodes.SecurityPrincipalNotFound,
                $"Технічного запису {UserName} немає або його вимкнено: задача інтеграції не має від чийого імені писати. "
                + "Запис заводить 09-seed.sql.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                    ["userId"] = UserName,
                });
        }

        // ⚠ Груп немає: токена входу в технічного запису не буває. Мова — `en`,
        // базова мова продукту: читачів у цього автора немає, а тексти відмов
        // ідуть у журнал задачі.
        // ⛔ `EnterIntegration`, а не `Enter`: саме це (контекст задачі, а не
        // логін) дає профілю `IsIntegrationWriter` — право писати значення
        // збору в будь-який проєкт без грантів, у межах усіх заборон
        // (`AccessDecisionService.BuildProfileAsync`, `EditRules.Effective`).
        return scope.EnterIntegration(new JobActor(
            account.Id, UserName, "en", [], Guid.NewGuid().ToString("N")));
    }
}
