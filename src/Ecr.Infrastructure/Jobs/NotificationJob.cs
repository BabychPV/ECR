using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Розсилання сповіщень про події робочого процесу і збоїв.
/// </summary>
/// <remarks>
/// Сповіщення — не транзакційна частина операції: якщо пошта недоступна,
/// подання документа все одно відбулося. Тому задача читає чергу подій, а не
/// викликається зсередини use-case.
/// </remarks>
public sealed class NotificationJob(EcrDbContext db) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти невідправлені події; згрупувати за адресатом, щоб не слати десять " +
            "листів поспіль; відправити; позначити відправленими. " +
            "⚠ Невдала відправка — не втрата: подія лишається в черзі й повторюється " +
            "з обмеженням кількості спроб.");
}
