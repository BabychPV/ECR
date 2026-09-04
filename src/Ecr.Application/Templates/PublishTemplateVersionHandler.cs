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
    IUnitCatalog unitCatalog,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Право на публікацію версії шаблону (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Publish";

    /// <summary>Виконує публікацію.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="userId">Хто публікує.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// Валідація не пройдена; у <c>Details</c> — перелік діагностик.
    /// </exception>
    public async Task PublishAsync(int templateVersionId, int userId, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        // Усі дванадцять перевірок із 02b §12 — синтаксис, резолвінг, типи,
        // ациклічність, розкриття діапазонів, сумісність одиниць.
        // ⚠ Обидва контексти передаються ЯВНО: без контексту типів перевірка №3
        // мовчки не виконувалася (Q-072), без контексту одиниць — перевірка
        // сумісності (ФВ-16.6). Значення за замовчуванням тут ховало б пропуск.
        var structure = PublishChecks.Snapshot(version);
        var catalogue = await unitCatalog.GetAsync(ct).ConfigureAwait(false);

        var diagnostics = PublishChecks.Run(
            version,
            formulaEngine,
            new SnapshotTypeContext(structure),
            new SnapshotUnitContext(structure, catalogue));

        if (diagnostics.Count > 0)
        {
            // Усі проблеми одразу, а не перша: інакше користувач публікував би
            // версію десятки разів, виправляючи по одній.
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Публікацію відхилено: знайдено проблем — {diagnostics.Count}.",
                new Dictionary<string, object?>
                {
                    ["diagnostics"] = diagnostics
                        .Select(d => new DiagnosticInfo(d.Code, d.Message, d.Position, d.Length))
                        .ToList(),
                });
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

}

/// <summary>Проблема публікації у відповіді API.</summary>
/// <remarks>
/// Окремий тип, а не <c>ExpressionDiagnostic</c>: у відповідь іде рівно те, що
/// потрібно конфігуратору для підсвічування — код, текст і межі фрагмента.
/// </remarks>
/// <param name="Code">Код помилки з каталогу.</param>
/// <param name="Message">Пояснення.</param>
/// <param name="Position">Зсув у тексті виразу.</param>
/// <param name="Length">Довжина проблемного фрагмента.</param>
public sealed record DiagnosticInfo(string Code, string Message, int Position, int Length);
