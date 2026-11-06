// src/Ecr.Application/Permits/Import/PermitImportMappingHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Errors;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Nae;
using Ecr.Application.RegistryImport;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Import;

/// <summary>
/// Saved column mapping of the paste/import dialog of a permit table, SHARED by all users: <c>cfg.RegistryImportProfile</c> of the
/// table registry with code <see cref="PermitImportTables.ProfileCode"/> (no migration: the table exists since N3).
/// </summary>
/// <remarks>
/// Read: <c>Registry.View</c> or grant <c>Read</c> on the table registry (whoever can open the dialog). Save: <c>Permit.EditType</c> —
/// the mapping is part of how the permit TYPE is filled, the same right that edits the type's tables and columns — AND the read
/// access of <see cref="GetAsync"/> (a table under Deny is 404 like a missing one, S18).
/// The specification is <c>{"kind":"permitPasteMapping","columns":{"&lt;header&gt;":"&lt;column&gt;"}}</c>.
/// </remarks>
public sealed class PermitImportMappingHandler(
    IRegistryStore registries,
    IRegistryImportProfileStore profiles,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    NaeTreeSettings? naeTree = null,
    AirLimitsSettings? air = null)
{
    /// <summary>Right to read the mapping (or a grant <c>Read</c> on the table).</summary>
    public const string ViewPermission = "Registry.View";

    /// <summary>Right to save the mapping.</summary>
    public const string EditPermission = "Permit.EditType";

    /// <summary>Most mapped headers.</summary>
    public const int MaxColumns = 500;

    /// <summary>Longest header kept.</summary>
    public const int MaxHeaderLength = 400;

    private const string Kind = "permitPasteMapping";

    /// <summary>The saved mapping (empty when none) with the table's NAE and derivable columns.</summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PermitImportMappingDto> GetAsync(string code, CancellationToken ct)
    {
        var lookup = new RegistryLookup(registries, code);
        await RegistryAccess.RequireAsync(access, currentUser, ViewPermission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var table = PermitImportTables.RequireTable(await lookup.RequireAsync(ct).ConfigureAwait(false), code);
        var profile = await profiles.FindAsync(table.Id, PermitImportTables.ProfileCode, ct).ConfigureAwait(false);
        return await ToDtoAsync(table, profile, ct).ConfigureAwait(false);
    }

    /// <summary>Creates or replaces the mapping. An existing one needs <c>If-Match</c> = its <c>rowVersion</c>.</summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="request">Header → column.</param>
    /// <param name="ifMatch">Token from the last read; ignored when nothing is saved yet.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ConcurrencyConflictException"><c>409 ECR-REG-0409</c>: changed by someone else after it was read.</exception>
    /// <exception cref="BusinessRuleException"><c>422 ECR-IMP-0422</c>: unknown column or too many headers.</exception>
    public async Task<PermitImportMappingDto> SaveAsync(
        string code, SavePermitImportMappingRequest request, string? ifMatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401",
                         "Anonymous requests do not change registries.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        // ⛔ S18/D-312 (AU-13 P2-A): та сама перевірка таблиці, що й у GetAsync, ДО будь-якої валідації: таблиця під Deny
        // (або без Read) для запису так само «невідома», як для читання - 404 ≡ неіснуючий, без оракула кодів колонок.
        var lookup = new RegistryLookup(registries, code);
        await RegistryAccess.RequireAsync(access, currentUser, ViewPermission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var table = PermitImportTables.RequireTable(await lookup.RequireAsync(ct).ConfigureAwait(false), code);
        var columns = Validate(table, request.Columns ?? new Dictionary<string, string>());
        var spec = JsonSerializer.Serialize(new { kind = Kind, columns });
        var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "Paste mapping" });

        var existing = await profiles.FindAsync(table.Id, PermitImportTables.ProfileCode, ct).ConfigureAwait(false);
        if (existing is null)
        {
            existing = new RegistryImportProfile(table.Id, EcrCode.Create(PermitImportTables.ProfileCode), name, spec, userId, clock.UtcNow);
            profiles.Add(existing);
        }
        else
        {
            var token = Token(ifMatch);
            if (token is null || !string.Equals(token, Convert.ToBase64String(existing.RowVersion), StringComparison.Ordinal))
            {
                throw Changed(table.Code);
            }

            profiles.Expect(existing, Convert.FromBase64String(token));
            existing.Update(name, spec, userId, clock.UtcNow);
        }

        try
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw Changed(table.Code);
        }
        catch (Exception ex) when (EditAssignmentAccess.IsUniqueViolation(ex))
        {
            // Two FIRST saves at once: both read «no mapping» and add one; the second is refused by UQ_RegistryImportProfile
            // (RegistryDefId, Code). The same answer as a stale If-Match - the mapping was saved by someone else meanwhile (not 500).
            throw Changed(table.Code);
        }

        return await ToDtoAsync(table, existing, ct).ConfigureAwait(false);
    }

    /// <summary>Header → column of a stored specification; a broken or foreign specification reads as empty.</summary>
    internal static Dictionary<string, string> Parse(string? specJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(specJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(specJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == Kind
                && doc.RootElement.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Object)
            {
                foreach (var column in columns.EnumerateObject())
                {
                    if (column.Value.ValueKind == JsonValueKind.String && column.Value.GetString() is { Length: > 0 } field)
                    {
                        result[column.Name] = field;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not ours (or hand-edited): nothing is applied, the dialog falls back to the header match.
        }

        return result;
    }

    private async Task<PermitImportMappingDto> ToDtoAsync(RegistryDef table, RegistryImportProfile? profile, CancellationToken ct)
    {
        var fields = table.Fields.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);
        var columns = Parse(profile?.SpecJson)
            .Where(c => fields.Contains(c.Value) && c.Value != PermitHeaderFields.PermitFieldCode)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        var described = await PermitImportTables.DescribeAsync(registries, table, naeTree, air, ct).ConfigureAwait(false);
        return new PermitImportMappingDto(
            table.Code,
            columns,
            profile is null ? null : Convert.ToBase64String(profile.RowVersion),
            profile?.UpdatedAt,
            [.. described.NaeColumns.Select(f => f.Code)],
            [.. described.RegionColumns.Concat(described.AreaColumns).Select(f => f.Code)]);
    }

    private static Dictionary<string, string> Validate(RegistryDef table, IReadOnlyDictionary<string, string> columns)
    {
        if (columns.Count > MaxColumns)
        {
            throw Invalid("columns", string.Create(CultureInfo.InvariantCulture, $"more than {MaxColumns} headers"));
        }

        var fields = table.Fields
            .Where(f => f.Code != PermitHeaderFields.PermitFieldCode)
            .ToDictionary(f => f.Code, f => f.Code, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (header, column) in columns)
        {
            var key = PermitImportTables.NormalizeHeader(header);
            if (key.Length == 0 || key.Length > MaxHeaderLength)
            {
                continue;
            }

            if (column is null || !fields.TryGetValue(column, out var canonical))
            {
                throw Invalid(header, $"unknown column {column}");
            }

            result[key] = canonical;
        }

        return result;
    }

    private static string? Token(string? ifMatch)
    {
        var token = ifMatch?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (token.StartsWith("W/", StringComparison.Ordinal))
        {
            token = token[2..];
        }

        token = token.Trim('"');
        try
        {
            _ = Convert.FromBase64String(token);
            return token;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ConcurrencyConflictException Changed(string code)
        => new(
            "ECR-REG-0409",
            $"The paste mapping of {code} was changed after it was read.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0409.importMappingChanged", ["registryCode"] = code });

    private static BusinessRuleException Invalid(string where, string reason)
        => new(
            "ECR-IMP-0422",
            $"The paste mapping is not valid ({where}): {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = RegistryImportKeys.SpecInvalid,
                ["where"] = where,
                ["reason"] = reason,
            });
}
