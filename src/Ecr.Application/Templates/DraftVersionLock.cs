// src/Ecr.Application/Templates/DraftVersionLock.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Templates;

/// <summary>
/// Блок рядка версії шаблону для структурної правки чернетки (C5).
/// </summary>
/// <remarks>
/// ⛔ Перевірка <see cref="TemplateVersion.EnsureStructurallyMutable"/> на
/// сутності, прочитаній ДО транзакції, — це перевірка застарілого стану:
/// публікація могла закомітитися між читанням і записом, і нова колонка чи
/// формула лягала в уже опубліковану версію, минаючи всі перевірки
/// публікації. Тому кожен структурний обробник першою дією своєї транзакції
/// кличе <see cref="EnsureDraftUnderLockAsync"/>: блок рядка версії
/// (той самий, що бере публікація) і повторна перевірка стану — вже
/// закоміченого, прочитаного під блоком.
///
/// ⚠ Порядок блокувань — рядок <c>cfg.TemplateVersion</c> ЗАВЖДИ першим,
/// до будь-якого запису в дочірні таблиці структури. Публікація дотримується
/// того самого порядку, тож циклу очікування між ними немає.
/// </remarks>
public static class DraftVersionLock
{
    /// <summary>
    /// Блокує рядок версії до кінця транзакції й відмовляє, якщо версія вже
    /// не чернетка.
    /// </summary>
    /// <param name="store">Сховище версій.</param>
    /// <param name="version">Сутність версії, прочитана обробником раніше.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c>, ключ <c>structurallyFrozen</c> — той самий, що
    /// дає <see cref="TemplateVersion.EnsureStructurallyMutable"/>.
    /// </exception>
    public static async Task EnsureDraftUnderLockAsync(
        ITemplateVersionStore store, TemplateVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(version);

        var status = await store.LockVersionForUpdateAsync(version.Id, ct).ConfigureAwait(false);

        if (status is TemplateVersionStatus.Published or TemplateVersionStatus.Deprecated)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Версія {version.Version} у стані {status} структурно заморожена. " +
                "Її опублікували, поки ця правка готувалася; структурні зміни вносяться " +
                "клонуванням у нову версію (ФВ-7.1).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.structurallyFrozen",
                    ["version"] = version.Version,
                    ["status"] = status.ToString(),
                });
        }
    }
}
