// src/Ecr.Application/Ports/ITemplateVersionStore.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Операції над версією шаблону, які неможливо виразити через
/// <see cref="IRepository{T,TId}"/>, бо вони мусять бути атомарними в базі.
/// </summary>
/// <remarks>
/// ⚠ Порт уведений за тією самою причиною, що й порти <c>Q-018</c>: обробник
/// не має права знати про SQL, але <c>R-B7</c> вимагає саме атомарної
/// операції.
///
/// <b>Чому не read-modify-write у застосунку.</b> Інстансів застосунку
/// щонайменше два (<c>D-32</c>). Якби ревізію читали, додавали одиницю і
/// записували, два одночасні патчі дали б однакове нове значення, і другий
/// мовчки затер би перший — при цьому ключ кешу <c>v{id}:r{rev}</c> у клієнтів
/// збігся б із застарілою структурою. Тому інкремент робиться одним
/// <c>UPDATE … SET PresentationRevision = PresentationRevision + 1 OUTPUT
/// inserted.PresentationRevision</c>, і застосунок дізнається результат, а не
/// призначає його.
/// </remarks>
public interface ITemplateVersionStore
{
    /// <summary>
    /// Інкрементує <c>PresentationRevision</c> одним statement і повертає
    /// <b>нове</b> значення з <c>OUTPUT</c>.
    /// </summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Нова ревізія.</returns>
    public Task<int> IncrementPresentationRevisionAsync(int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Чи існують документи, прив'язані до цієї версії.
    /// </summary>
    /// <remarks>
    /// Від відповіді залежить класифікація структурної зміни (ФВ-7.4):
    /// та сама зміна коду колонки без документів <c>Safe</c>, з документами —
    /// <c>Breaking</c> і відмова операції.
    /// </remarks>
    public Task<bool> HasDocumentsAsync(int templateVersionId, CancellationToken ct);
}
