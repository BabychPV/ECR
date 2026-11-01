// src/Ecr.Application/Permits/AirLimits/AirImportProfileHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.RegistryImport;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>Stored import profiles of the AIR_SOURCES table (<c>cfg.RegistryImportProfile</c>).</summary>
public sealed class AirImportProfileHandler(
    AirLimitsSettingsAccessor accessor,
    IRegistryImportProfileStore profiles,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Permission to read profiles.</summary>
    public const string ViewPermission = "Registry.View";

    /// <summary>Permission to save profiles.</summary>
    public const string EditPermission = "Registry.EditDefinition";

    /// <summary>Profiles of the table, by code. Permission <c>Registry.View</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<ImportProfileSummaryDto>> ListAsync(CancellationToken ct)
    {
        await Security.PermissionCheck.RequireAsync(access, currentUser, ViewPermission, ct).ConfigureAwait(false);
        var tables = await accessor.Data.TablesAsync(ct).ConfigureAwait(false);
        return [.. (await profiles.ListAsync(tables.Sources.Id, ct).ConfigureAwait(false))
            .Select(p => new ImportProfileSummaryDto(p.Code, Names(p), IsBuiltIn(p), p.UpdatedAt))];
    }

    /// <summary>One profile with its specification. Permission <c>Registry.View</c>.</summary>
    /// <param name="code">Profile code.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ImportProfileDto> GetAsync(string code, CancellationToken ct)
    {
        await Security.PermissionCheck.RequireAsync(access, currentUser, ViewPermission, ct).ConfigureAwait(false);
        var tables = await accessor.Data.TablesAsync(ct).ConfigureAwait(false);
        var profile = await profiles.FindAsync(tables.Sources.Id, code?.Trim() ?? string.Empty, ct).ConfigureAwait(false)
                      ?? throw NotFound(code);
        return ToDto(profile);
    }

    /// <summary>Creates or replaces a profile. Permission <c>Registry.EditDefinition</c>.</summary>
    /// <param name="code">Profile code.</param>
    /// <param name="request">Name, specification, token.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ImportProfileDto> SaveAsync(string code, SaveImportProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Security.PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401",
                         "Anonymous requests do not change registries.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var tables = await accessor.Data.TablesAsync(ct).ConfigureAwait(false);
        var ecrCode = EcrCode.Create(code);
        var spec = (request.Spec ?? throw SpecInvalid("spec", "missing")).Validated();
        var name = new LocalizedText(new Dictionary<string, string>(
            request.NameL10n is { Count: > 0 } ? request.NameL10n : new Dictionary<string, string> { ["en"] = ecrCode.Value },
            StringComparer.OrdinalIgnoreCase));

        var existing = await profiles.FindAsync(tables.Sources.Id, ecrCode.Value, ct).ConfigureAwait(false);
        if (existing is null)
        {
            existing = new RegistryImportProfile(tables.Sources.Id, ecrCode, name, spec.ToJson(), userId, clock.UtcNow);
            profiles.Add(existing);
        }
        else
        {
            if (!string.Equals(request.RowVersion, Convert.ToBase64String(existing.RowVersion), StringComparison.Ordinal))
            {
                throw Changed(existing.Code);
            }

            existing.Update(name, spec.ToJson(), userId, clock.UtcNow);
        }

        try
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw Changed(existing.Code);
        }

        return ToDto(existing);
    }

    private static bool IsBuiltIn(RegistryImportProfile p) => p.Code == AirLimitsSchema.ProfileCode;

    private static Dictionary<string, string> Names(RegistryImportProfile p)
        => new Dictionary<string, string>(p.NameL10n.Values, StringComparer.OrdinalIgnoreCase);

    private static ImportProfileDto ToDto(RegistryImportProfile p)
        => new(p.Code, Names(p), IsBuiltIn(p), RegistryImportSpec.Parse(p.SpecJson), Convert.ToBase64String(p.RowVersion), p.UpdatedAt);

    private static NotFoundException NotFound(string? code)
        => new(
            "ECR-REG-0404",
            $"Import profile {code} does not exist.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.importProfile", ["code"] = code ?? string.Empty });

    private static ConcurrencyConflictException Changed(string code)
        => new(
            "ECR-REG-0409",
            $"Import profile {code} was changed after it was read.",
            new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.ProfileChanged, ["code"] = code });

    private static BusinessRuleException SpecInvalid(string where, string reason)
        => new(
            "ECR-IMP-0422",
            $"The import profile is not valid ({where}): {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = RegistryImportKeys.SpecInvalid,
                ["where"] = where,
                ["reason"] = reason,
            });
}