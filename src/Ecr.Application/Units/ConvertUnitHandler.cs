// src/Ecr.Application/Units/ConvertUnitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Units;

/// <summary>
/// Явна конверсія одиниць. **Неявних конверсій не буває** (ФВ-16.4, D-74):
/// рушій перетворює величину лише за викликом <c>CONVERT</c> у виразі або за
/// правилом мапінгу інтеграції.
/// </summary>
public sealed class ConvertUnitHandler(IUnitCatalog catalog)
{
    /// <summary>Конвертує значення між одиницями.</summary>
    /// <param name="value">Значення у вихідній одиниці.</param>
    /// <param name="fromUnit">Код вихідної одиниці.</param>
    /// <param name="toUnit">Код цільової одиниці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Одиниці немає в довіднику.</exception>
    /// <exception cref="BusinessRuleException">Різні розмірності — <c>ECR-UOM-0422</c>.</exception>
    public async Task<decimal> HandleAsync(
        decimal value, string fromUnit, string toUnit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromUnit);
        ArgumentException.ThrowIfNullOrWhiteSpace(toUnit);

        // Однакові одиниці — значення без арифметики. Множення на 1.0 у
        // decimal дає зайвий хвіст: 123.456 стає 123.4560, і звірка рядка
        // з рядком перестає сходитися.
        if (string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var catalogue = await catalog.GetAsync(ct).ConfigureAwait(false);

        var from = Resolve(catalogue, fromUnit);
        var to = Resolve(catalogue, toUnit);

        // ⛔ Z2-05: уся арифметика й усі відмови - ті самі, що на межі інтеграції й у `CONVERT` виразів
        // (`BoundaryUnitConversion.Convert` над доменним `UnitConverter`): різні розмірності - ВІДМОВА, а не пошук
        // шляху «через базу» (маса в об'єм не переводиться без щільності - це константа методології, ФВ-16.3,
        // ФВ-16.5); нульовий множник ЛЮБОЇ з одиниць - відмова, а не константа `(value × 0) + offset`; швидкість у
        // швидкість - через чисельник і знаменник, а не через заокруглений `FactorToBase` (`1 Sm3/s` →
        // `3599.99…712` замість `3600`). Власна копія формули тут розходилася б із ними тихо: кожна дає число.
        try
        {
            return BoundaryUnitConversion.Convert(value, from.Id, to.Id, catalogue);
        }
        catch (DomainException ex)
        {
            throw new BusinessRuleException(ex.ErrorCode, ex.Message, ex.Details);
        }
    }

    private static UnitRef Resolve(UnitCatalogSnapshot catalogue, string code)
        => catalogue.Units.TryGetValue(code, out var unit)
            ? unit
            // ⚠ Новий ключ, не `.unitId` (`Repository`/`Unit` заміри): той
            // шукає за числовим Id, цей — за КОДОМ одиниці з тіла запиту
            // конверсії; різний адресат помилки за різним ключем, той самий
            // прийом, що `.column`/`.columnCode` у шаблонах (2026-09-23).
            : throw new NotFoundException(
                "ECR-UOM-0404", $"Одиниці «{code}» немає в довіднику.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0404.code",
                    ["code"] = code,
                });
}

/// <summary>Перелік одиниць із розмірностями (ФВ-16.2).</summary>
/// <remarks>
/// Розмірність віддається разом із одиницею навмисно: без неї клієнт не може
/// перевірити нічого — ні того, що конверсія можлива, ні того, що величини
/// сумісні.
/// </remarks>
public sealed class ListUnitsHandler(
    IUnitCatalog catalog, IUnitStore units, Security.IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Читає довідник одиниць.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <c>UsedIn</c> заповнюється лише тому, хто має <c>Uom.EditCatalog</c> (те саме право, що й
    /// <c>GET /units/{id}/usage</c>); решті — <c>null</c>. Перелік одиниць відкритий усім, а число
    /// використання в шаблонах і довідниках — ні.
    /// </remarks>
    public async Task<IReadOnlyList<UnitRef>> HandleAsync(CancellationToken ct)
    {
        var catalogue = await catalog.GetAsync(ct).ConfigureAwait(false);
        var list = catalogue.Units.Values.OrderBy(u => u.Code, StringComparer.Ordinal).ToList();

        var userId = currentUser.UserId;
        if (userId is null)
        {
            return list;
        }

        var profile = await access.BuildProfileAsync(userId.Value, ct).ConfigureAwait(false);
        if (!Security.PermissionCheck.IsGranted(profile, CreateUnitHandler.Permission))
        {
            return list;
        }

        var counts = await units.CountUnitStructuralUsageAsync(ct).ConfigureAwait(false);

        return list.Select(u => u with { UsedIn = counts.GetValueOrDefault(u.Id) }).ToList();
    }
}
