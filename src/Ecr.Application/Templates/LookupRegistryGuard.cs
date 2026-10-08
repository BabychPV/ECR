// src/Ecr.Application/Templates/LookupRegistryGuard.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Templates;

/// <summary>
/// RC16-2: ціль <c>Lookup</c>-колонки й <c>Lookup</c>-поля шапки мусить бути живим активним довідником.
/// </summary>
/// <remarks>
/// ⛔ У <c>cfg.ColumnDef.LookupRegistryDefId</c> зовнішнього ключа немає, тож колонка на неіснуючий довідник
/// зберігалась і публікувалась мовчки (потім - порожній випадний список і відмова на кожному введенні); у
/// <c>cfg.HeaderFieldDef</c> ключ є, і описка давала голий <c>500</c> на 547. Тепер - <c>422</c> з ключем.
///
/// ⚠ Довідник, на який у автора є заборона (<c>RegistryAccess.IsDenied</c>), відповідає ТАК САМО, як неіснуючий:
/// інакше автор шаблону з забороною на X перебором <c>Id</c> дізнавався б, які довідники існують
/// (та сама політика, що в <c>RegistryDefinitionHandlers.RequireUsableTargetAsync</c>).
/// </remarks>
internal static class LookupRegistryGuard
{
    /// <summary>Відмовляє збереження колонки, якщо довідника немає, він неактивний або заборонений автору.</summary>
    /// <param name="registries">Сховище довідників; <c>null</c> - перевірка не виконується (збирання без довідників).</param>
    /// <param name="access">Рішення доступу.</param>
    /// <param name="userId">Автор правки.</param>
    /// <param name="registryDefId">Довідник із команди.</param>
    /// <param name="columnCode">Код колонки - для тексту відмови.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-TMPL-0422</c>, ключ <c>lookupRegistryUnknown</c>.</exception>
    internal static async Task RequireForColumnAsync(
        IRegistryStore? registries, IAccessDecisionService access, int userId, int? registryDefId,
        string columnCode, CancellationToken ct)
    {
        if (registryDefId is not { } id || await IsUsableAsync(registries, access, userId, id, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.TemplateInvalid,
            $"Колонка «{columnCode}»: довідника {id.ToString(CultureInfo.InvariantCulture)} немає або він неактивний.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0422.lookupRegistryUnknown",
                ["columnCode"] = columnCode,
                ["registryDefId"] = id.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Те саме для поля шапки.</summary>
    /// <param name="registries">Сховище довідників; <c>null</c> - перевірка не виконується.</param>
    /// <param name="access">Рішення доступу.</param>
    /// <param name="userId">Автор правки.</param>
    /// <param name="registryDefId">Довідник із команди.</param>
    /// <param name="fieldCode">Код поля - для тексту відмови.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-TMPL-0422</c>, ключ <c>headerFieldLookupRegistryUnknown</c>.</exception>
    internal static async Task RequireForHeaderFieldAsync(
        IRegistryStore? registries, IAccessDecisionService access, int userId, int? registryDefId,
        string fieldCode, CancellationToken ct)
    {
        if (registryDefId is not { } id || await IsUsableAsync(registries, access, userId, id, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.TemplateInvalid,
            $"Поле шапки «{fieldCode}»: довідника {id.ToString(CultureInfo.InvariantCulture)} немає або він неактивний.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown",
                ["headerFieldCode"] = fieldCode,
                ["registryDefId"] = id.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static async Task<bool> IsUsableAsync(
        IRegistryStore? registries, IAccessDecisionService access, int userId, int registryDefId, CancellationToken ct)
    {
        if (registries is null)
        {
            return true;
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (RegistryAccess.IsDenied(profile, registryDefId))
        {
            return false;
        }

        var definition = await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false);

        return definition is { IsActive: true };
    }
}
