// src/Ecr.Application/Registries/GetRegistryEntryUsageHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Registries;

/// <summary>
/// «Де використовується» ОДИН запис довідника — документи, у комірках чи шапці яких він стоїть (ФВ-8.14).
/// </summary>
/// <remarks>
/// <para>
/// Право — те саме, що й для «де використано» довідника (<see cref="GetRegistryUsageHandler.Permission"/>): перелік
/// потрібен перед зміною чи виведенням запису з обігу. Заборона на довідник (S18) — <c>404</c>, як неіснуючий.
/// </para>
/// <para>
/// ⛔ Документи звужені проєктами, де читач має <c>Document.View</c> (як <c>GET permits/{id}/usages</c>): чужі
/// документи не рахуються й не називаються, інакше картка запису розкривала б їхнє існування.
/// </para>
/// </remarks>
public sealed class GetRegistryEntryUsageHandler(
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документів проєкту: без нього документ у відповідь не потрапляє.</summary>
    public const string DocumentPermission = "Document.View";

    /// <summary>Перші <see cref="UsageResponse.PageSize"/> документів і їх загальна кількість.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="entryId">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Немає довідника або запису в ньому — <c>ECR-REG-0404</c>.</exception>
    public async Task<UsageResponse> HandleAsync(string code, long entryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, GetRegistryUsageHandler.Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{code}» не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registry",
                    ["registryCode"] = code,
                });

        RegistryAccess.EnsureNotDenied(profile, definition.Id, code);

        // ⚠ Запис чужого довідника — 404, а не «нуль документів»: інакше маршрут відповідав би про будь-який Id.
        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.RegistryDefId != definition.Id)
        {
            throw new NotFoundException(
                "ECR-REG-0404",
                $"Запису довідника {entryId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = entryId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var projects = ListDocumentsHandler.ReadableProjects(profile, DocumentPermission);
        if (projects.Count == 0)
        {
            return new UsageResponse(0, []);
        }

        return await registries
            .FindEntryDocumentsAsync(entryId, projects, UsageResponse.PageSize, ct)
            .ConfigureAwait(false);
    }
}
