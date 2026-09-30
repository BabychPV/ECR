using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Сеанси симуляції над <c>aud.SimulationSession</c> (ФВ-6.16a, D-96).
/// </summary>
/// <remarks>
/// ⚠ Таблиця журналу лежить поза моделлю EF, як і решта <c>aud.*</c>: журнал
/// append-only, і обліковий запис застосунку має на ньому лише <c>INSERT</c> і
/// <c>SELECT</c>. Виняток — <c>EndedAt</c>, який дозволено оновити (це єдине
/// поле, що змінюється після вставки).
///
/// ⚠ Три останні параметри необов'язкові свідомо: реєстрація в <c>DependencyInjection.cs</c>
/// (фабрика з двома аргументами) лишається як є. <c>UserStore</c> і <c>AuditWriter</c> не
/// мають стану поза <see cref="EcrDbContext"/>, тож над тим самим scoped-контекстом вони —
/// рівно те, що дав би контейнер.
/// </remarks>
/// <param name="db">Контекст бази.</param>
/// <param name="access">САМА служба рішень (не обгортка симуляції — інакше коло залежностей).</param>
/// <param name="users">Сховище для стелі D-210; <c>null</c> — над тим самим <paramref name="db"/>.</param>
/// <param name="audit">Журнал для події-спроби; <c>null</c> — над тим самим <paramref name="db"/>.</param>
/// <param name="clock">Годинник; <c>null</c> — системний.</param>
public sealed class SimulationService(
    EcrDbContext db,
    IAccessDecisionService access,
    IUserStore? users = null,
    IAuditWriter? audit = null,
    Domain.Abstractions.IClock? clock = null) : ISimulationService
{
    /// <summary>Суфікс причини: стелю D-210 порушено вже ПІСЛЯ старту сеансу.</summary>
    private const string AfterStartSuffix = "AfterStart";

    private readonly IUserStore _users = users ?? new UserStore(db);
    private readonly IAuditWriter _audit = audit ?? new AuditWriter(db);
    private readonly Domain.Abstractions.IClock _clock = clock ?? new SystemClock();

    /// <inheritdoc />
    public async Task<long> StartAsync(
        int actorUserId, int subjectUserId, string reason, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // OUTPUT inserted.Id: ідентифікатор потрібен викликачеві негайно, а
        // SCOPE_IDENTITY() зайвий раунд-трип і зайва залежність від контексту.
        command.CommandText = """
            INSERT aud.SimulationSession (ActorUserId, SubjectUserId, Reason, StartedAt)
            OUTPUT inserted.Id
            VALUES (@actor, @subject, @reason, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@actor", actorUserId);
        command.Parameters.AddWithValue("@subject", subjectUserId);
        command.Parameters.AddWithValue("@reason", reason);

        var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(raw, provider: null);
    }

    /// <inheritdoc />
    public async Task<int?> GetActorAsync(long sessionId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // Закритий сеанс не повертається: завершувати вже нема чого, і
        // повторне закриття не має ані вдаватися, ані виглядати як чужий сеанс.
        command.CommandText =
            "SELECT ActorUserId FROM aud.SimulationSession WHERE Id = @id AND EndedAt IS NULL;";
        command.Parameters.AddWithValue("@id", sessionId);

        var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return raw is null or DBNull ? null : Convert.ToInt32(raw, provider: null);
    }

    /// <inheritdoc />
    public async Task EndAsync(long sessionId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // Запис НЕ видаляється (D-25): журнал симуляцій потрібен саме тоді,
        // коли хтось питає, хто і навіщо дивився чужі дані.
        command.CommandText = """
            UPDATE aud.SimulationSession
               SET EndedAt = SYSUTCDATETIME()
             WHERE Id = @id AND EndedAt IS NULL;
            """;
        command.Parameters.AddWithValue("@id", sessionId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccessProfile> BuildProfileAsync(long sessionId, CancellationToken ct)
    {
        var (actorUserId, subjectUserId) = await ReadPrincipalsAsync(sessionId, ct).ConfigureAwait(false);

        await EnsureCeilingStillHoldsAsync(sessionId, actorUserId, subjectUserId, ct).ConfigureAwait(false);

        // ⚠ Профіль будується ЗАНОВО і НЕ кладеться в кеш (ФВ-6.16a п. 4).
        // AccessProfileCache відмовляється кешувати профілі з IsSimulation, але
        // покладатися лише на це не можна: побудова тут іде повз кеш узагалі.
        var subject = await access.BuildProfileAsync(subjectUserId, ct).ConfigureAwait(false);

        return new AccessProfile
        {
            // Ключ навмисно інший: якби він збігся з ключем суб'єкта, профіль
            // симуляції дістався б справжньому користувачеві.
            CacheKey = $"sim:{sessionId}",
            UserId = subjectUserId,
            SecurityStamp = subject.SecurityStamp,
            Permissions = subject.Permissions,
            Grants = subject.Grants,
            Denies = subject.Denies,

            // ⚠ Ролі беруться від СУБ'ЄКТА, а не від того, хто симулює:
            // інакше правило, обмежене роллю, показувало б не ті комірки —
            // тобто симуляція показувала б не те, що бачить користувач, і
            // сенс режиму зникав би (`D-96`).
            RoleIds = subject.RoleIds,

            // ⛔ Області дії (ФВ-6.14) — теж від суб'єкта: без них симуляція
            // не бачила б прав, які роль з областю дає в своїх проєктах.
            UnscopedRoleIds = subject.UnscopedRoleIds,
            Scoped = subject.Scoped,
            IsSimulation = true,
            SimulatedForUserId = subjectUserId,

            // Автором дій лишається той, хто симулює (D-86): у аудиті має
            // стояти людина, а не роль, яку вона приміряла.
            SimulationActorUserId = actorUserId,
        };
    }

    /// <summary>
    /// Стеля «View as» (D-210) на КОЖЕН запит під сеансом, а не лише на старті.
    /// </summary>
    /// <remarks>
    /// ⛔ Ціль, що посеред сеансу стала bootstrap чи отримала небезпечне право, інакше
    /// показувала б ці права актору до кінця сеансу: профіль сеансу будується заново на
    /// кожен запит (V-06), і нова роль у нього потрапляє.
    ///
    /// ⚠ Реакція — ЗАВЕРШИТИ сеанс (<c>EndedAt</c>) і відповісти як про закритий
    /// (<see cref="Application.Errors.NotFoundException"/>): обгортка
    /// <c>SimulationAwareAccessDecisionService</c> тоді віддає профіль самого актора — той
    /// самий шлях, що й сеанс, закритий в іншій вкладці. Не <c>403</c> на кожен запит: cookie
    /// з номером сеансу живе й далі, і актор застряг би у відмовах до явного виходу, а сеанс
    /// лишався б відкритим. Закритий сеанс не відкривається знову.
    ///
    /// ⚠ Ціна — один запит до бази (призначення × ролі × небезпечні права; прапорець
    /// bootstrap — ще один) на запит під сеансом. Кеш профілів тут не допоміг би: глобальні
    /// права ролей з областю він відкидає, а пряма зміна призначень штампа цілі не крутить.
    /// </remarks>
    private async Task EnsureCeilingStillHoldsAsync(
        long sessionId, int actorUserId, int subjectUserId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var target = await _users.GetSimulationTargetPrivilegesAsync(subjectUserId, now, ct).ConfigureAwait(false);
        if (target.DenyReason is not { } reason)
        {
            return;
        }

        await EndAsync(sessionId, ct).ConfigureAwait(false);

        await _audit.WriteIndependentSecurityEventAsync(
            new SecurityEventRecord(
                now,
                StartSimulationHandler.DeniedEvent,
                TargetUserId: subjectUserId,
                TargetRoleId: null,
                DetailsJson: $$"""{"reason":"{{reason}}{{AfterStartSuffix}}"}""",
                ChangedByUserId: actorUserId,
                CorrelationId: null),
            ct).ConfigureAwait(false);

        throw new Application.Errors.NotFoundException(
            "ECR-SIM-0422", $"Сеанс симуляції {sessionId} завершено: ціль порушила стелю D-210.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SIM-0422.noSession" });
    }

    private async Task<(int Actor, int Subject)> ReadPrincipalsAsync(long sessionId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ActorUserId, SubjectUserId FROM aud.SimulationSession WHERE Id = @id AND EndedAt IS NULL;";
        command.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new Application.Errors.NotFoundException(
                "ECR-SIM-0422", $"Активного сеансу симуляції {sessionId} не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SIM-0422.noSession" });
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
