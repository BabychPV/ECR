// src/Ecr.Domain/Entities/Documents/EditAssignmentSlice.cs
using System.Globalization;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Зріз документа, який власник передає виконавцям (EA-1): перетин (AND) необов'язкових
/// селекторів; жодного селектора — «весь документ».
/// </summary>
/// <remarks>
/// ⚠ Селектори — КОДИ й номери, а не <c>ColumnDefId</c>/<c>SheetDefId</c>: вони переживають перенос
/// версії шаблону (R10). <see cref="Key"/> — канонічний ключ зрізу (<c>UQ_EditAssignment</c>
/// за <c>(DocumentId, SliceKey)</c>); його рахує лише домен, ніколи не клієнт чи сховище.
/// </remarks>
public sealed record EditAssignmentSlice
{
    /// <summary>Межа довжини коду аркуша/таблиці (як <c>cfg.SheetDef.Code</c>).</summary>
    public const int MaxCodeLength = 64;

    /// <summary>Межа довжини <see cref="Key"/> (колонка <c>SliceKey nvarchar(200)</c>).</summary>
    public const int MaxKeyLength = 200;

    private EditAssignmentSlice(string? sheetCode, string? tableCode, byte? monthNumber, int? periodKey)
    {
        SheetCode = sheetCode;
        TableCode = tableCode;
        MonthNumber = monthNumber;
        PeriodKey = periodKey;
    }

    /// <summary>Код аркуша; <c>null</c> — усі аркуші.</summary>
    public string? SheetCode { get; }

    /// <summary>Код таблиці в межах аркуша; <c>null</c> — усі таблиці. Потребує <see cref="SheetCode"/>.</summary>
    public string? TableCode { get; }

    /// <summary>Номер місяця 1..12 для місячних колонок річних таблиць; <c>null</c> — будь-який.</summary>
    public byte? MonthNumber { get; }

    /// <summary>Ключ періоду (<c>Рік*100+№</c>) екземпляра таблиці; <c>null</c> — будь-який.</summary>
    public int? PeriodKey { get; }

    /// <summary>Канонічний ключ зрізу, напр. <c>S=F1|T=*|M=1|P=*</c>.</summary>
    public string Key => string.Create(
        CultureInfo.InvariantCulture,
        $"S={SheetCode?.ToUpperInvariant() ?? "*"}|T={TableCode?.ToUpperInvariant() ?? "*"}|M={(MonthNumber is { } m ? m.ToString(CultureInfo.InvariantCulture) : "*")}"
        + $"|P={(PeriodKey is { } p ? p.ToString(CultureInfo.InvariantCulture) : "*")}");

    /// <summary>Створює зріз і перевіряє інваріанти (ті самі, що CHECK-обмеження таблиці).</summary>
    /// <param name="sheetCode">Код аркуша або <c>null</c>.</param>
    /// <param name="tableCode">Код таблиці або <c>null</c>; потребує <paramref name="sheetCode"/>.</param>
    /// <param name="monthNumber">1..12 або <c>null</c>.</param>
    /// <param name="periodKey">Коректний <see cref="Ecr.Domain.ValueObjects.PeriodKey"/> або <c>null</c>.</param>
    /// <exception cref="ArgumentException">Порушено інваріант; користувацька валідація з <c>messageKey</c> — в обробнику (EA-3).</exception>
    public static EditAssignmentSlice Create(string? sheetCode, string? tableCode, byte? monthNumber, int? periodKey)
    {
        var sheet = NormalizeCode(sheetCode, nameof(sheetCode));
        var table = NormalizeCode(tableCode, nameof(tableCode));

        if (table is not null && sheet is null)
        {
            throw new ArgumentException("Код таблиці задається лише разом із кодом аркуша.", nameof(tableCode));
        }

        if (monthNumber is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(monthNumber), monthNumber, "Номер місяця має бути в межах 1..12.");
        }

        if (periodKey is { } key && !new Ecr.Domain.ValueObjects.PeriodKey(key).IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(periodKey), periodKey, "Ключ періоду має вигляд Рік*100+№.");
        }

        return new EditAssignmentSlice(sheet, table, monthNumber, periodKey);
    }

    private static string? NormalizeCode(string? code, string parameter)
    {
        if (code is null)
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxCodeLength)
        {
            throw new ArgumentException($"Код має бути довжиною 1..{MaxCodeLength} символів.", parameter);
        }

        // «|» і «*» — службові символи канонічного ключа; керувальні символи не потрібні ніде.
        if (trimmed.Contains('|', StringComparison.Ordinal)
            || trimmed.Contains('*', StringComparison.Ordinal)
            || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Код містить службовий або керувальний символ.", parameter);
        }

        return trimmed;
    }
}
