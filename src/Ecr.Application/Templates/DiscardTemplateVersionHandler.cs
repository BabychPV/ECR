// src/Ecr.Application/Templates/DiscardTemplateVersionHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Templates;

/// <summary>
/// Відкидає незавершену чернетку версії шаблону разом з її структурою
/// (<c>DELETE /template-versions/{id}</c>).
/// </summary>
/// <remarks>
/// ⛔ Лише чернетка (<see cref="Domain.Entities.Configuration.TemplateVersion.EnsureDiscardable"/>) і лише
/// та, на яку ніщо поза її конфігурацією не посилається: проєкт, документ, маршрут погодження, зріз,
/// мапінг інтеграції, вхід методики, клон від цієї чернетки. Перелік використань іде у відмову —
/// адміністратор бачить, що саме тримає версію. Каскад, блокування, перевірка й запис у журнал безпеки —
/// одна транзакція: видалення не комітиться без сліду (той самий вибір, що
/// <c>DeleteMethodologyVersionHandler</c>).
/// </remarks>
public sealed class DiscardTemplateVersionHandler(
    ITemplateVersionDiscardStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    IMetadataCache metadataCache,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// Право — те саме, що на публікацію й виведення з обігу: незворотна дія над версією
    /// має ту саму вагу, що й випуск.
    /// </summary>
    public const string Permission = "Template.Publish";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string DiscardedEventType = "TemplateVersionDiscarded";

    /// <summary>Відкидає чернетку.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — версії немає.</exception>
    /// <exception cref="DomainException"><c>ECR-TMPL-0409</c> — не чернетка або версію використовують.</exception>
    public async Task HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var version = await store.LockAsync(templateVersionId, innerCt).ConfigureAwait(false)
                          ?? throw VersionNotFound(templateVersionId);

            version.EnsureDiscardable();

            var usages = await store.FindUsagesAsync(templateVersionId, innerCt).ConfigureAwait(false);
            if (usages.Count > 0)
            {
                throw InUse(version.Version, usages);
            }

            var deleted = await store.DeleteAsync(templateVersionId, innerCt).ConfigureAwait(false);

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    DiscardedEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        templateVersionId,
                        templateId = version.TemplateId,
                        version = version.Version,
                        clonedFromVersionId = version.ClonedFromVersionId,
                        deleted,
                    }),
                    userId,
                    currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }

    /// <summary>409: версію тримає щось поза її конфігурацією.</summary>
    /// <param name="version">Номер версії.</param>
    /// <param name="usages">Вид використання → кількість.</param>
    /// <returns>Виняток для кидка.</returns>
    internal static DomainException InUse(string version, IReadOnlyDictionary<string, int> usages)
    {
        var list = string.Join(", ", usages.OrderBy(u => u.Key, StringComparer.Ordinal)
            .Select(u => string.Create(CultureInfo.InvariantCulture, $"{u.Key}: {u.Value}")));

        return new DomainException(
            ErrorCodes.TemplateFrozen,
            $"Чернетку {version} не можна відкинути: на неї посилаються {list}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0409.discardVersionInUse",
                ["version"] = version,
                ["usages"] = list,
            });
    }

    private static NotFoundException VersionNotFound(int templateVersionId)
        => new(
            ErrorCodes.TemplateNotFound,
            $"Версії шаблону {templateVersionId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
            });
}
