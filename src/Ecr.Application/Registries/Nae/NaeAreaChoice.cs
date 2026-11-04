// src/Ecr.Application/Registries/Nae/NaeAreaChoice.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;

namespace Ecr.Application.Registries.Nae;

/// <summary>
/// AN-158 (human 04.11, «чого тут не лист бокс?»): the Area code of an NAE REGION/AREA node is CHOSEN from the shared list
/// «Air permit areas» (<see cref="AirLimitsSettings.AreaRegistryCode"/>), never typed — it is the same code permits scope on, so a
/// source's inherited area and the permits matching agree.
/// </summary>
internal static class NaeAreaChoice
{
    /// <summary>The value is not an active entry of the Area list.</summary>
    internal const string NotInListKey = "err.ECR-REG-0422.naeAreaNotInList";

    /// <summary>The list entry for <paramref name="text"/> (matched by its normalised code or English name).</summary>
    /// <param name="registries">Registry store.</param>
    /// <param name="settings">Air-limits settings (the Area list code).</param>
    /// <param name="text">Typed or chosen value; empty — no area.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The normalised code stored on the node and the entry's English name; <c>("", null)</c> for an empty value.</returns>
    /// <exception cref="BusinessRuleException"><c>422 naeAreaNotInList</c>.</exception>
    internal static async Task<(string Code, string? NameEn)> ResolveAsync(
        IRegistryStore registries, AirLimitsSettings settings, string? text, CancellationToken ct)
    {
        var wanted = AirLimitsSchema.NormalizeScope(text, "");
        if (wanted.Length == 0)
        {
            return ("", null);
        }

        // A list that is not provisioned yet has nothing to choose from: the typed value is kept (normalised) until it exists.
        var definition = await registries.FindDefinitionAsync(settings.AreaRegistryCode, ct).ConfigureAwait(false);
        if (definition is null)
        {
            return (wanted, null);
        }

        var entries = await registries.ListEntriesAsync(definition.Id, ct).ConfigureAwait(false);
        var match = entries.FirstOrDefault(e => !e.IsDeleted && e.IsActive
            && (AirLimitsSchema.NormalizeScope(e.Code, "") == wanted || AirLimitsSchema.NormalizeScope(e.DisplayL10n.Get("en"), "") == wanted));
        if (match is null)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Area «{text}» is not an active entry of the list {settings.AreaRegistryCode}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = NotInListKey,
                    ["areaCode"] = text?.Trim(),
                    ["registryCode"] = settings.AreaRegistryCode,
                });
        }

        return (AirLimitsSchema.NormalizeScope(match.Code, ""), match.DisplayL10n.Get("en"));
    }
}