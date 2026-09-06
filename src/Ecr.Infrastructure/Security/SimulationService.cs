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
/// </remarks>
public sealed class SimulationService(
    EcrDbContext db, IAccessDecisionService access) : ISimulationService
{
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
            IsSimulation = true,
            SimulatedForUserId = subjectUserId,

            // Автором дій лишається той, хто симулює (D-86): у аудиті має
            // стояти людина, а не роль, яку вона приміряла.
            SimulationActorUserId = actorUserId,
        };
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
                "ECR-SIM-0422", $"Активного сеансу симуляції {sessionId} не існує.");
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
