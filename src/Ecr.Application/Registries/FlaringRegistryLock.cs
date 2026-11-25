// src/Ecr.Application/Registries/FlaringRegistryLock.cs
using Ecr.Application.Common;
using Ecr.Application.GasComposition;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>
/// Одна зміна запису довідника для запобіжника факелів: вікно чинності ДО і ПІСЛЯ зміни.
/// </summary>
/// <param name="OldWindow">Вікно наявного запису; <c>null</c> — запису ще немає (вставка НОВОГО коду).</param>
/// <param name="NewWindow">Вікно після зміни; <c>null</c> — запис зникає (видалення).</param>
public sealed record FlaringRegistryEdit(ValidityWindow? OldWindow, ValidityWindow? NewWindow)
{
    /// <summary>Вставка нового запису з вікном <paramref name="window"/> (порожнє — без меж).</summary>
    /// <param name="window">Вікно нового запису.</param>
    public static FlaringRegistryEdit Insert(ValidityWindow? window) => new(null, window ?? new ValidityWindow(null, null));

    /// <summary>Зміна значень наявного запису: вікно те саме.</summary>
    /// <param name="window">Вікно запису.</param>
    public static FlaringRegistryEdit Change(ValidityWindow window) => new(window, window);

    /// <summary>Видалення наявного запису.</summary>
    /// <param name="window">Вікно запису.</param>
    public static FlaringRegistryEdit Remove(ValidityWindow window) => new(window, null);
}

/// <summary>
/// ✎ DESIGN-sg-flaring §5.7 (C4, D4–D6): закриті місяці не змінюються і для довідників факелів (<c>LP_SOUR_SPLIT</c>,
/// <c>TOX_GAS_STREAM</c>, <c>FLARE_LOCATION</c>), які пишуть загальні редактори довідників.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>право — <c>GasComposition.Edit</c> (інакше 403 <c>err.ECR-AUTH-0403.permission</c>): <c>Registry.EditData</c>
/// загальних редакторів тут недостатньо;</item>
/// <item>замкнені періоди — <see cref="IGasCompositionStore.ListLockedPeriodsAsync(IReadOnlyCollection{int}, CancellationToken)"/>
/// з вікнами з <c>doc.Period</c> (D4); відмова — <see cref="GasCompositionRules.RequireUnlocked"/> (409
/// <c>err.ECR-REG-0409.gasCompositionLocked</c>), якщо СТАРЕ або НОВЕ вікно запису перетинає замкнений період;</item>
/// <item>✎ D5: нетемпоральний довідник (вікно запису — (−∞, +∞)): вставка НОВОГО коду дозволена (її не читав жоден
/// замкнений результат: такі рядки результату не мали), зміна/видалення/відновлення наявного запису — 409, щойно
/// довідник має замкнений період. Окремих записів, які читав прогін, система не зберігає (немає read set, §5.8), тож
/// «запис, який читали» = будь-який наявний запис довідника, що його читає замкнений результат;</item>
/// <item>обмеження (C4(d)): замкнений місяць, усі рядки якого відхилено, результату не має, тож не замикає.</item>
/// </list>
/// </remarks>
public sealed class FlaringRegistryLock(IGasCompositionStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Коди довідників під запобіжником.</summary>
    public static IReadOnlyList<string> RegistryCodes { get; } = ["LP_SOUR_SPLIT", "TOX_GAS_STREAM", "FLARE_LOCATION"];

    /// <summary>Чи довідник під запобіжником факелів.</summary>
    /// <param name="registryCode">Код довідника.</param>
    public static bool Applies(string registryCode)
        => RegistryCodes.Any(c => string.Equals(c, registryCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>Перевіряє право й замкнені періоди для змін записів.</summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="edits">Зміни записів (порожньо — лише право).</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="Errors.AccessDeniedException">403: немає <c>GasComposition.Edit</c>.</exception>
    /// <exception cref="Errors.BusinessRuleException">409: зміна зачіпає замкнений період.</exception>
    public async Task EnsureEditAllowedAsync(RegistryDef definition, IReadOnlyCollection<FlaringRegistryEdit> edits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(edits);

        if (!Applies(definition.Code))
        {
            return;
        }

        _ = await PermissionCheck.RequireAsync(access, currentUser, GasCompositionPermissions.Edit, ct).ConfigureAwait(false);

        var ranges = RangesOf(definition.IsTemporal, edits).ToList();
        if (ranges.Count == 0)
        {
            return;
        }

        var locked = await store.ListLockedPeriodsAsync([definition.Id], ct).ConfigureAwait(false);
        if (locked.Count == 0)
        {
            return;
        }

        foreach (var range in ranges)
        {
            GasCompositionRules.RequireUnlocked(range, locked);
        }
    }

    /// <summary>Дати, на яких зміни змінюють видимий склад довідника.</summary>
    /// <param name="isTemporal">Довідник темпоральний.</param>
    /// <param name="edits">Зміни.</param>
    public static IEnumerable<GasDateRange> RangesOf(bool isTemporal, IEnumerable<FlaringRegistryEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        foreach (var edit in edits)
        {
            if (!isTemporal)
            {
                // D5: новий код не читав жоден замкнений результат; наявний — читав увесь час, (−∞, +∞).
                if (edit.OldWindow is not null)
                {
                    yield return new GasDateRange(DateOnly.MinValue, null);
                }

                continue;
            }

            foreach (var window in (ValidityWindow?[])[edit.OldWindow, edit.NewWindow])
            {
                if (window is { } w)
                {
                    yield return new GasDateRange(w.FromInclusive ?? DateOnly.MinValue, w.ToExclusive);
                }
            }
        }
    }
}
