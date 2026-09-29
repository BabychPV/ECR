// src/Ecr.Application/Registries/Rows/RegistryRowDto.cs
using System.Buffers.Binary;

namespace Ecr.Application.Registries.Rows;

/// <summary>
/// Рядок довідника зі значеннями полів (RT-13, FEATURE-REGISTRY-TABLES §7.1).
/// </summary>
/// <param name="Id">Запис.</param>
/// <param name="Code">Код запису.</param>
/// <param name="Display">Назва мовою користувача; немає — код.</param>
/// <param name="ParentEntryId">Батько ієрархії (<c>ParentEntryId</c>), не композиції.</param>
/// <param name="ValidFrom">Перший чинний день; <c>null</c> — від початку.</param>
/// <param name="ValidTo">Перший НЕчинний день; <c>null</c> — без обмеження.</param>
/// <param name="Version">
/// Жетон конкуренції (<c>D-166</c>): найпізніший <c>PeriodStart</c> запису та його значень. Непрозорий
/// рядок — клієнт повертає його як <c>baseVersion</c> (RT-14), а не розбирає.
/// </param>
/// <param name="Values">Код поля → значення; поля без значення відсутні.</param>
public sealed record RegistryRowDto(
    long Id,
    string Code,
    string Display,
    long? ParentEntryId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string Version,
    IReadOnlyDictionary<string, RegistryRowValueDto> Values);

/// <summary>Значення поля рядка.</summary>
/// <param name="Value">
/// Значення рядком: число — інваріантно й без втрати знаків (D-30), дата — <c>yyyy-MM-dd</c>,
/// логічне — <c>true</c>/<c>false</c>, <c>Lookup</c> — ідентифікатор запису-цілі, <c>Unit</c> — ідентифікатор одиниці.
/// </param>
/// <param name="Display">Назва цілі <c>Lookup</c> або код одиниці поля <c>Unit</c>; інакше <c>null</c>.</param>
/// <param name="Unit">Код одиниці числового значення; <c>null</c> — безрозмірне.</param>
public sealed record RegistryRowValueDto(string? Value, string? Display, string? Unit);

/// <summary>Кодування версії рядка (<see cref="RegistryRowDto.Version"/>).</summary>
/// <remarks>
/// ⚠ Base64 восьми байтів тиків UTC (big-endian) — не захист, а сигнал непрозорості, як курсор.
/// Точність <c>datetime2(3)</c> тики зберігають без втрат, тож порівняння з <c>PeriodStart</c> у
/// RT-14 точне.
/// </remarks>
public static class RegistryRowVersion
{
    /// <summary>Кодує момент.</summary>
    /// <param name="periodStartUtc">Початок системного періоду (UTC).</param>
    public static string Encode(DateTime periodStartUtc)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, periodStartUtc.Ticks);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Розбирає версію, яку віддав сервер.</summary>
    /// <param name="version">Рядок від клієнта.</param>
    /// <param name="periodStartUtc">Момент (UTC).</param>
    /// <returns><c>false</c> — рядок не є версією цього сервера.</returns>
    public static bool TryDecode(string? version, out DateTime periodStartUtc)
    {
        periodStartUtc = default;
        Span<byte> bytes = stackalloc byte[8];

        if (string.IsNullOrWhiteSpace(version)
            || !Convert.TryFromBase64String(version, bytes, out var written)
            || written != 8)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        periodStartUtc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}
