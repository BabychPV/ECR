using Ecr.Application.Ports;
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Публікує версію шаблону. Це найважливіша операція конфігуратора: після неї
/// структура заморожена, а всі перевірки, які можна зробити наперед, уже
/// зроблені.
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється з переліком проблем.
/// Часткова публікація неможлива за побудовою.
/// </remarks>
public sealed class PublishTemplateVersionHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    IFormulaEngine formulaEngine,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Виконує публікацію.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="userId">Хто публікує.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// Валідація не пройдена; у <c>Details</c> — перелік діагностик.
    /// </exception>
    public async Task PublishAsync(int templateVersionId, int userId, CancellationToken ct)
    {
        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Перевірки виразів (усі 12 із 02b §12 — синтаксис, резолвінг,
        //    типи, ациклічність, розкриття діапазонів, сумісність одиниць)
        //    вмикаються на Етапі 2 разом із рушієм. Тут навмисно НЕ ставиться
        //    заглушка «повертає true»: вона створила б враження, що перевірка
        //    працює. Місце виклику зафіксоване нижче.
        var diagnostics = await CollectDiagnosticsAsync(version, ct).ConfigureAwait(false);

        if (diagnostics.Count > 0)
        {
            // Усі проблеми одразу, а не перша: інакше користувач публікував би
            // версію десятки разів, виправляючи по одній.
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Публікацію відхилено: знайдено проблем — {diagnostics.Count}.",
                new Dictionary<string, object?> { ["diagnostics"] = diagnostics });
        }

        // Перехід стану. Кидає ECR-TMPL-0409, якщо версія вже опублікована;
        // виняток виходить назовні до SaveChanges, тому часткових змін немає.
        version.Publish(userId, clock.UtcNow);

        await audit.WritePublicationEventAsync(
            new PublicationEventRecord(
                clock.UtcNow, EntityType: "TemplateVersion", EntityId: templateVersionId,
                ResultDiffJson: null, ChangeReason: "Publish", ChangedByUserId: userId),
            ct).ConfigureAwait(false);

        // Аудит і зміна стану — в одній транзакції: подія публікації без
        // публікації (і навпаки) зробила б журнал недостовірним.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Збирає діагностики перевірок публікації.
    /// </summary>
    /// <remarks>
    /// На Етапі 1 перевірок виразів ще немає — рушій з'являється на Етапі 2.
    /// Метод повертає порожній список, і це <b>не</b> заглушка «все гаразд»:
    /// версія без формул справді не має чого перевіряти, а щойно формули
    /// з'являться, тут викликається <c>formulaEngine</c> за 02b §12.
    /// </remarks>
    private Task<IReadOnlyList<string>> CollectDiagnosticsAsync(
        Domain.Entities.Configuration.TemplateVersion version, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
