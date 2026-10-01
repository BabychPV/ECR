// src/Ecr.Application/Ports/ISmtpSettingsStore.cs
using Ecr.Domain.Entities.Notifications;

namespace Ecr.Application.Ports;

/// <summary>Сховище налаштувань SMTP, заданих адміністратором (<c>D-263</c>).</summary>
public interface ISmtpSettingsStore
{
    /// <summary>Єдиний рядок налаштувань (відстежуваний) або <c>null</c>, якщо його ще не заводили.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<SmtpSettings?> FindAsync(CancellationToken ct);

    /// <summary>Додає перший рядок налаштувань.</summary>
    /// <param name="settings">Налаштування.</param>
    public void Add(SmtpSettings settings);
}

/// <summary>Шифрує пароль SMTP перед записом у базу. Розшифровує лише транспорт.</summary>
public interface ISmtpPasswordProtector
{
    /// <summary>Зашифровує пароль.</summary>
    /// <param name="password">Пароль у відкритому вигляді.</param>
    public byte[] Protect(string password);
}

/// <summary>
/// Кеш ефективних налаштувань транспорту. Зміну налаштувань у БД транспорт підхоплює не пізніше ніж
/// за 30 с; <see cref="Invalidate"/> робить це негайно (виклик після запису).
/// </summary>
public interface ISmtpSettingsCache
{
    /// <summary>Скидає кеш: наступна відправка прочитає налаштування заново.</summary>
    public void Invalidate();
}
