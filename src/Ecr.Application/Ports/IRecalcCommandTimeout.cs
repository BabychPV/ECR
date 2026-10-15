namespace Ecr.Application.Ports;

/// <summary>
/// Піднімає таймаут SQL-команд поточного scope (контексту БД) до стелі перерахунку
/// (<c>Database:RecalcCommandTimeoutSeconds</c>).
/// </summary>
/// <remarks>
/// ⛔ Чому порт, а не лише рядок у <c>RecalculationJob</c>: паралельні гілки оркестратора пишуть результати
/// у власних scope/<c>EcrDbContext</c> (Q-249), і таймаут задачі їх не торкається. Оркестратор (проєкт
/// <c>Ecr.Calculations</c> не бачить EF) викликає порт у кожному своєму scope. Глобальний
/// <c>Database:CommandTimeoutSeconds</c> (60) лишається для решти запитів застосунку.
/// </remarks>
public interface IRecalcCommandTimeout
{
    /// <summary>Виставляє таймаут команд контексту поточного scope.</summary>
    public void Apply();
}
