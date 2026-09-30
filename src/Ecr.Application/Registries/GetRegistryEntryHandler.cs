// src/Ecr.Application/Registries/GetRegistryEntryHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Один запис довідника цілком — для форми правки (X-03, R-04, четвертий
/// раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Перелік записів (<see cref="GetRegistryEntriesHandler"/>) віддає назву
/// ОДНІЄЮ мовою і без значень полів — він для списків вибору, де більше не
/// треба. Форма правки будувалася саме з нього: підставляла назву під
/// <c>en</c>, поля лишала порожніми, і збереження перезаписувало переклади
/// назви однією мовою. Тут — усі мови назви й усі значення полів запису.
///
/// ⚠ Значення — РЯДКАМИ в інваріантному форматі, тим самим, який приймає
/// <see cref="UpsertRegistryEntryHandler"/> (форма редагує поля текстом):
/// число — без роздільників розрядів і без хвоста нулів, дата — ISO
/// <c>yyyy-MM-dd</c>, логічне — <c>true</c>/<c>false</c>, посилання й
/// одиниця — ідентифікатор.
/// </remarks>
public sealed class GetRegistryEntryHandler(
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає запис.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="entryId">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">
    /// Довідника немає, запису немає, запис видалено або він належить іншому
    /// довіднику — <c>ECR-REG-0404</c>.
    /// </exception>
    public async Task<RegistryEntryDetailDto> HandleAsync(
        string registryCode, long entryId, CancellationToken ct)
    {
        await RegistryAccess
            .RequireAsync(
                access, currentUser, Permission, GrantLevel.Read,
                async token => (await registries.FindDefinitionAsync(registryCode, token).ConfigureAwait(false))?.Id,
                ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{registryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);

        // ⚠ Запис чужого довідника — «не знайдено», а не відповідь: адреса
        // несе код довідника, і збіг самого Id нічого не означає.
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != definition.Id)
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

        var fields = definition.Fields.ToDictionary(f => f.Id);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var value in await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false))
        {
            // ⚠ Значення поля, якого в описі вже немає, не віддається: форма
            // однаково не має для нього поля, а сервер відхилив би його як
            // невідоме при збереженні.
            if (fields.TryGetValue(value.RegistryFieldDefId, out var field))
            {
                values[field.Code] = Text(value, field.DataType);
            }
        }

        return new RegistryEntryDetailDto(
            entry.Id,
            entry.Code,
            entry.DisplayL10n,
            entry.ParentEntryId,
            entry.ValidFrom,
            entry.ValidTo,
            values);
    }

    /// <summary>Значення поля текстом у форматі, який приймає запис.</summary>
    internal static string? Text(RegistryValue value, CellDataType dataType) => dataType switch
    {
        CellDataType.String => value.ValueString,

        // ⚠ `0.#…#`, а не `G29`: той переходить на експоненту для малих чисел
        // (`1E-06`), а стовпець у базі зберігає хвіст нулів масштабу
        // (`12.5000000000`) — обидва форми людина в полі читала б як інше число.
        CellDataType.Int or CellDataType.Decimal =>
            value.ValueNumeric?.ToString("0.############################", CultureInfo.InvariantCulture),
        CellDataType.Bool => value.ValueBool switch
        {
            true => "true",
            false => "false",
            null => null,
        },
        CellDataType.Date => value.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        CellDataType.Lookup => value.ValueRefEntryId?.ToString(CultureInfo.InvariantCulture),
        CellDataType.Unit => value.ValueUnitId?.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}
