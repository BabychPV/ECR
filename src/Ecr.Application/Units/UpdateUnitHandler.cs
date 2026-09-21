// src/Ecr.Application/Units/UpdateUnitHandler.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Units;

/// <summary>Одиниця для форми редагування — з усім, що форма показує, і версією.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код; не редагується.</param>
/// <param name="SymbolL10n">Позначення мовами каталогу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="DimensionId">Розмірність; не редагується.</param>
/// <param name="IsBase">Базова одиниця розмірності; її коефіцієнти не редагуються.</param>
/// <param name="FactorToBase">Множник переходу до базової одиниці.</param>
/// <param name="OffsetToBase">Зсув переходу до базової одиниці.</param>
/// <param name="RowVersion">Версія вмісту; повертається заголовком <c>If-Match</c>.</param>
public sealed record UnitDetail(
    int Id,
    string Code,
    IReadOnlyDictionary<string, string> SymbolL10n,
    IReadOnlyDictionary<string, string> NameL10n,
    byte DimensionId,
    bool IsBase,
    decimal FactorToBase,
    decimal OffsetToBase,
    string RowVersion)
{
    internal static UnitDetail Of(Unit unit)
        => new(unit.Id, unit.Code, unit.SymbolL10n.Values, unit.NameL10n.Values, unit.DimensionId,
               unit.IsBase, unit.FactorToBase, unit.OffsetToBase, UnitVersion.Of(unit));
}

/// <summary>Версія одиниці — хеш її вмісту.</summary>
/// <remarks>
/// ⚠ У <c>uom.Unit</c> немає колонки <c>rowversion</c>, а міграція в цій задачі
/// заборонена. Хеш вмісту ловить те саме, що потрібно формі: одиницю змінили
/// між читанням і записом. Десяткові — без хвостових нулів: із бази множник
/// приходить у масштабі колонки (<c>2.500000000000000000</c>), з пам'яті —
/// як ввели (<c>2.5</c>), і версія мусить збігатися.
/// </remarks>
public static class UnitVersion
{
    /// <summary>Версія поточного вмісту одиниці.</summary>
    public static string Of(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var canonical = string.Join(
            '',
            unit.Id.ToString(CultureInfo.InvariantCulture), unit.Code,
            Text(unit.SymbolL10n), Text(unit.NameL10n),
            unit.DimensionId.ToString(CultureInfo.InvariantCulture), unit.IsBase ? "1" : "0",
            Number(unit.FactorToBase), Number(unit.OffsetToBase), unit.IsActive ? "1" : "0");

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..12]);
    }

    private static string Number(decimal value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Text(LocalizedText text)
        => string.Join(
            '',
            text.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key.ToUpperInvariant() + "=" + p.Value));
}

/// <summary>Одиниця для форми редагування (директива №15, BE-15).</summary>
public sealed class GetUnitHandler(
    IUnitStore units,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Одиниця з версією; право — <c>Uom.EditCatalog</c>, як і на зміну.</summary>
    /// <exception cref="NotFoundException">Одиниці немає — <c>ECR-UOM-0404</c>.</exception>
    public async Task<UnitDetail> HandleAsync(int unitId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, CreateUnitHandler.Permission, ct)
            .ConfigureAwait(false);

        return UnitDetail.Of(await UnitUsageHandler.FindAsync(units, unitId, ct).ConfigureAwait(false));
    }
}

/// <summary>Зміна одиниці довідника <c>uom.Unit</c> (директива №15, BE-15).</summary>
/// <remarks>
/// ⛔ Код, розмірність і ознака базової не редагуються. Множник і зсув —
/// лише доки на одиницю ніщо не посилається: інакше вже збережені значення й
/// методики мовчки перерахувалися б в інші числа (<c>409 ECR-UOM-0409</c>).
/// Позначення й назва редагуються завжди.
/// </remarks>
public sealed class UpdateUnitHandler(
    IUnitStore units,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Тип події в журналі безпеки.</summary>
    public const string EventType = "UnitUpdated";

    /// <summary>Найдовше значення позначення чи назви однією мовою.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Змінює одиницю, якщо <paramref name="ifMatch"/> — її чинна версія.</summary>
    /// <exception cref="NotFoundException">Одиниці немає — <c>ECR-UOM-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException">
    /// Версія застаріла або змінюється множник одиниці, на яку посилаються, — <c>ECR-UOM-0409</c>.
    /// </exception>
    /// <exception cref="BusinessRuleException">Немає <c>If-Match</c> чи тіло невалідне — 422.</exception>
    public async Task<UnitDetail> HandleAsync(
        int unitId,
        IReadOnlyDictionary<string, string>? symbol,
        IReadOnlyDictionary<string, string>? name,
        decimal factorToBase,
        decimal offsetToBase,
        string? ifMatch,
        CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, CreateUnitHandler.Permission, ct)
            .ConfigureAwait(false);

        var unit = await UnitUsageHandler.FindAsync(units, unitId, ct).ConfigureAwait(false);
        RequireCurrentVersion(unit, ifMatch);

        var symbolText = Normalize(symbol);
        var nameText = Normalize(name);
        if (symbolText is null || nameText is null)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Одиниця потребує позначення й назви хоча б однією мовою, до {MaxTextLength} символів.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.unitInvalid", ["code"] = unit.Code });
        }

        if (factorToBase <= 0m)
        {
            throw new BusinessRuleException(
                "ECR-UOM-0422",
                $"Множник переходу до базової одиниці мусить бути додатним, а не {factorToBase}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0422.factorMustBePositive",
                    ["code"] = unit.Code,
                    ["factorToBase"] = factorToBase.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (factorToBase != unit.FactorToBase || offsetToBase != unit.OffsetToBase)
        {
            await RequireUnusedAsync(unit, ct).ConfigureAwait(false);
        }

        unit.Update(symbolText, nameText, factorToBase, offsetToBase);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new { id = unit.Id, code = unit.Code, factorToBase, offsetToBase }),
                profile.UserId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return UnitDetail.Of(unit);
    }

    private static void RequireCurrentVersion(Unit unit, string? ifMatch)
    {
        var expected = ListCollectionSchedulesHandler.NormalizeETag(ifMatch)
                       ?? throw new BusinessRuleException(
                           ErrorCodes.RequestInvalid,
                           "Запит на зміну одиниці має нести заголовок If-Match зі значенням rowVersion.",
                           new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.unitIfMatch" });

        var actual = UnitVersion.Of(unit);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.UnitInUse,
                $"Одиницю «{unit.Code}» змінили після того, як її прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0409.unitChanged",
                    ["code"] = unit.Code,
                    ["rowVersion"] = actual,
                });
        }
    }

    private async Task RequireUnusedAsync(Unit unit, CancellationToken ct)
    {
        var usage = await units.FindUnitUsageAsync(unit.Id, UsageResponse.PageSize, ct).ConfigureAwait(false);
        if (usage.Total > 0 || unit.IsBase)
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.UnitInUse,
                $"Множник і зсув одиниці «{unit.Code}» не змінюються: на неї посилаються — {usage.Total}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0409.unitFactorInUse",
                    ["code"] = unit.Code,
                    ["total"] = usage.Total.ToString(CultureInfo.InvariantCulture),
                    ["references"] = usage.Items,
                });
        }
    }

    private static LocalizedText? Normalize(IReadOnlyDictionary<string, string>? values)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, text) in values ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(text))
            {
                kept[language.Trim()] = text.Trim();
            }
        }

        return kept.Count > 0 && kept.Values.All(v => v.Length <= MaxTextLength)
            ? new LocalizedText(kept)
            : null;
    }
}
