// src/Ecr.Application/Permits/PermitDraftWriter.cs
using Ecr.Application.Localization;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Створює запис шапки дозволу ЧЕРНЕТКОЮ (Permits, PM-04): спільне для заведення й поновлення. Викликається всередині
/// транзакції обробника.
/// </summary>
internal static class PermitDraftWriter
{
    /// <summary>Заводить чернетку; повертає збережений запис.</summary>
    /// <param name="registries">Сховище реєстрів (код з послідовності).</param>
    /// <param name="writer">Писач записів.</param>
    /// <param name="view">Тип.</param>
    /// <param name="values">Значення шапки (<see cref="PermitInput.HeaderValues"/>).</param>
    /// <param name="number">Номер (назва запису).</param>
    /// <param name="validFrom">Початок чинності.</param>
    /// <param name="validTo">Кінець чинності (виключний).</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<RegistryEntry> CreateAsync(
        IRegistryStore registries,
        RegistryEntryWriter writer,
        PermitTypeView view,
        IReadOnlyDictionary<string, object?> values,
        string number,
        DateOnly validFrom,
        DateOnly validTo,
        int userId,
        CancellationToken ct)
    {
        // ⛔ D-211: гард External-довідника - першою перевіркою (обробники викликають його теж, але сторож вимагає від типу-писача).
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);

        var code = EcrCode.Create(RegistryEntryWriter.AutoCode(await registries.NextEntryCodesAsync(1, ct).ConfigureAwait(false)));
        var entry = writer.AddEntry(
            view.Header.Id, code, new LocalizedText(new Dictionary<string, string> { [UiStringResolver.DefaultLanguage] = number }), userId);

        // ⛔ Вимкнений ДО першого збереження: ключ чернетки не повинен ставати живим навіть на мить (R2).
        entry.Deactivate();
        entry.SetValidity(validFrom, validTo);

        var changes = await writer.ApplyValuesAsync(view.Header, entry, values, prefetch: null, ct).ConfigureAwait(false);
        await writer.SaveEntryAsync(view.Header, entry, changes, userId, ct).ConfigureAwait(false);
        return entry;
    }
}
