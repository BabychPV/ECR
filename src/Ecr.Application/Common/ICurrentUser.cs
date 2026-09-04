namespace Ecr.Application.Common;

/// <summary>
/// Поточний користувач запиту. Реалізація в <c>Ecr.Api</c> дістає його з
/// cookie — і саме тому нижче рівня входу **не видно**, як він увійшов
/// (ФВ-6.2).
/// </summary>
public interface ICurrentUser
{
    /// <summary>Ідентифікатор; <c>null</c> для анонімного запиту.</summary>
    int? UserId { get; }

    /// <summary>Ім'я для аудиту і повідомлень.</summary>
    string? UserName { get; }

    /// <summary>Наскрізний ідентифікатор запиту.</summary>
    string CorrelationId { get; }

    /// <summary>Мова інтерфейсу для локалізації повідомлень.</summary>
    string Language { get; }
}
