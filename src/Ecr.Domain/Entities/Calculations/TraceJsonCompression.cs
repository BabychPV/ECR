// src/Ecr.Domain/Entities/Calculations/TraceJsonCompression.cs
using System.IO.Compression;
using System.Text;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// ✎ D-564 (PERF-03): стиснений <c>TraceJson</c> кроку трейсу — колонка
/// <c>calc.CalculationStep.TraceJsonGz varbinary(max)</c>.
/// </summary>
/// <remarks>
/// ⛔ Формат — рівно той, що дає SQL Server <c>COMPRESS(N'…')</c>: GZIP над
/// байтами <b>UTF-16LE</b> рядка. Тому той самий стовпець читається і кодом
/// (<see cref="Decompress"/>), і руками DBA чи інструментом
/// (<c>CAST(DECOMPRESS(TraceJsonGz) AS nvarchar(max))</c>) без жодної
/// домовленості поза базою. UTF-8 стискався б трохи краще, але
/// <c>CAST(… AS varchar(max))</c> під колацією бази (<c>Latin1_General_100_CI_AS_SC</c>,
/// не UTF-8) зіпсував би кирилицю виразів, тобто SQL-читач побачив би інший текст,
/// ніж код.
/// <para>
/// ⚠ Старі рядки (до D-564) лежать у <c>TraceJson nvarchar(max)</c> нестиснені й
/// лишаються читабельними: <see cref="CalculationStep.TraceJson"/> бере текстову
/// колонку, коли вона є, і стиснену — інакше. Великої міграції даних немає:
/// старі кроки прибирає <c>D-560</c> разом зі своїм прогоном.
/// </para>
/// </remarks>
public static class TraceJsonCompression
{
    /// <summary>Стискає JSON кроку; <c>null</c> лишається <c>null</c>.</summary>
    /// <param name="json">Текст <c>TraceJson</c>.</param>
    /// <returns>GZIP над UTF-16LE — той самий формат, що <c>COMPRESS(N'…')</c>.</returns>
    public static byte[]? Compress(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var raw = Encoding.Unicode.GetBytes(json);
        using var buffer = new MemoryStream(raw.Length / 4 + 32);
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Розпаковує те, що дав <see cref="Compress"/> або SQL <c>COMPRESS(N'…')</c>.</summary>
    /// <param name="compressed">Байти колонки; <c>null</c> — кроку без деталей.</param>
    /// <returns>Текст <c>TraceJson</c> або <c>null</c>.</returns>
    public static string? Decompress(byte[]? compressed)
    {
        if (compressed is null)
        {
            return null;
        }

        using var source = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var result = new MemoryStream(compressed.Length * 4);
        gzip.CopyTo(result);

        return Encoding.Unicode.GetString(result.GetBuffer(), 0, (int)result.Length);
    }
}
