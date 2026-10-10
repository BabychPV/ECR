using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Ports;

/// <summary>
/// Прочитані з БД дані довідників, з яких будується знімок на будь-яку бізнес-дату (L5-12).
/// </summary>
/// <remarks>
/// ⚠ Незмінне після створення, тож <see cref="Build"/> безпечно кликати багато разів і з кількох потоків.
/// Читання БД уже відбулося в <see cref="IRegistrySnapshotLoader.LoadSourceAsync"/>; тут його немає.
/// </remarks>
public interface IRegistrySnapshotSource
{
    /// <summary>Будує незмінний знімок із застосованою видимістю на дату — лише в пам'яті.</summary>
    /// <param name="businessDate">Бізнес-дата знімка.</param>
    /// <returns>Знімок; для порожнього джерела — порожній.</returns>
    public IRegistrySnapshot Build(DateOnly businessDate);
}
