namespace Ecr.Infrastructure.Security;

/// <summary>
/// Перевіряє <c>SecurityStamp</c> на **кожен** запит: відкликання ролі має
/// діяти негайно, а не після закінчення cookie (ФВ-6.10, тест безпеки №2).
/// </summary>
public sealed class SecurityStampValidator(EcrDbContext db)
{
    /// <summary>Чи актуальний штамп із cookie.</summary>
    public Task<bool> IsCurrentAsync(int userId, string stampFromCookie, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: легкий запит SELECT SecurityStamp FROM sec.[User] WHERE Id = @id; " +
            "порівняти. Кешувати на кілька секунд можна, довше — ні: сенс саме в негайності.");
}
