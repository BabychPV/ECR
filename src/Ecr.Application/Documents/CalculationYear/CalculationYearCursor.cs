// src/Ecr.Application/Documents/CalculationYear/CalculationYearCursor.cs
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>
/// Курсор сторінки виходів огляду року (<c>GET …/calculation-results/year/rows?cursor=</c>): позиція наступного рядка
/// плюс відбиток УСІХ уже виданих рядків (ідентифікатори виходів у порядку показу).
/// </summary>
/// <remarks>
/// ⚠ Рядки огляду впорядковуються в пам'яті після зведення (аркуш → копія → таблиця → рядок документа → методологія →
/// вихід), і ключ порядку — порядковий номер рядка документа, якого в ідентифікаторі виходу немає: keyset за
/// ідентифікатором тут неможливий. Тому курсор несе позицію, а відбиток доводить, що перед нею лежать РІВНО ті рядки,
/// які клієнт уже отримав. Перерахунок, новий рядок чи інший фільтр перед позицією змінюють відбиток — курсор
/// відхиляється (<c>ECR-REQ-0422</c>, <c>reason = cursorStale</c>), і клієнт читає таблицю з першої сторінки: без
/// пропусків і повторів, яких дав би голий зсув на змінному наборі. Зміни ПІСЛЯ позиції курсор не ламають — вони
/// просто потрапляють на наступні сторінки.
/// </remarks>
/// <param name="Position">Скільки рядків уже видано (≥ 1).</param>
/// <param name="Fingerprint">Відбиток перших <paramref name="Position"/> рядків.</param>
public sealed record CalculationYearCursor(int Position, string Fingerprint)
{
    private const string Prefix = "y1";

    /// <summary>Найдовший курсор, що розбирається (довший — зіпсований).</summary>
    public const int MaxLength = 80;

    /// <summary>Курсор після перших <paramref name="position"/> рядків <paramref name="matched"/>.</summary>
    public static string Encode(IReadOnlyList<CalculationYearRowDto> matched, int position)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        var text = string.Join(
            '.', Prefix, position.ToString(CultureInfo.InvariantCulture), FingerprintOf(matched, position));
        return Base64Url.EncodeToString(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>
    /// Розбирає курсор. Порожній — <see langword="true"/> і <paramref name="cursor"/> = <see langword="null"/> (перша
    /// сторінка); зіпсований — <see langword="false"/>.
    /// </summary>
    public static bool TryDecode(string? text, out CalculationYearCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (text.Length > MaxLength)
        {
            return false;
        }

        // ⚠ Не TryDecodeFromChars: на недопустимому символі той кидає FormatException (тобто 500), а не false.
        Span<byte> buffer = stackalloc byte[MaxLength];
        if (Base64Url.DecodeFromChars(text, buffer, out _, out var written) != OperationStatus.Done)
        {
            return false;
        }

        var parts = Encoding.ASCII.GetString(buffer[..written]).Split('.');
        if (parts.Length != 3
            || parts[0] != Prefix
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            || position < 1
            || parts[2].Length != 24
            || !parts[2].All(char.IsAsciiHexDigitLower))
        {
            return false;
        }

        cursor = new CalculationYearCursor(position, parts[2]);
        return true;
    }

    /// <summary>Чи перед позицією курсора в <paramref name="matched"/> лежать рівно ті рядки, після яких його видано.</summary>
    public bool Fits(IReadOnlyList<CalculationYearRowDto> matched)
    {
        ArgumentNullException.ThrowIfNull(matched);
        return Position <= matched.Count
               && string.Equals(FingerprintOf(matched, Position), Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>SHA-256 ідентифікаторів перших <paramref name="count"/> рядків (з довжиною кожного), перші 12 байтів.</summary>
    private static string FingerprintOf(IReadOnlyList<CalculationYearRowDto> rows, int count)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        for (var i = 0; i < count; i++)
        {
            var id = Encoding.UTF8.GetBytes(rows[i].Id);
            BinaryPrimitives.WriteInt32LittleEndian(length, id.Length);
            hash.AppendData(length);
            hash.AppendData(id);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 12));
    }
}
