namespace Ecr.Application.Common;

/// <summary>
/// Поточний користувач запиту. Реалізація в <c>Ecr.Api</c> дістає його з
/// cookie — і саме тому нижче рівня входу **не видно**, як він увійшов
/// (ФВ-6.2).
/// </summary>
public interface ICurrentUser
{
    /// <summary>Ідентифікатор; <c>null</c> для анонімного запиту.</summary>
    public int? UserId { get; }

    /// <summary>Ім'я для аудиту і повідомлень.</summary>
    public string? UserName { get; }

    /// <summary>Наскрізний ідентифікатор запиту.</summary>
    public string CorrelationId { get; }

    /// <summary>Мова інтерфейсу для локалізації повідомлень.</summary>
    public string Language { get; }
}
