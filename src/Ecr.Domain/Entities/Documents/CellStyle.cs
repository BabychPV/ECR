using System.Text.RegularExpressions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Користувацьке оформлення комірки документа: заливка, колір тексту, жирність
/// (CELL-STYLE L3b, «змінювати колір комірки як в Excel»).
/// </summary>
/// <remarks>
/// ⚠ Це НЕ значення. Рядок живе в окремій розрідженій таблиці <c>doc.CellStyle</c>, а не в
/// <see cref="CellValue"/>: значень ~108 млн на рік, порожні комірки не матеріалізуються, а колір
/// потрібен і на порожніх. Рядок існує, лише поки є що показувати: коли всі поля порожні
/// («Скинути»), його видаляють.
///
/// ⛔ Зовнішніх ключів на <c>doc.TableRow</c> / <c>cfg.ColumnDef</c> НЕМАЄ навмисно — як і в
/// <c>ext.RowWindowValue</c>: <c>arc.usp_ArchiveYear</c> звільняє партиції <c>doc.TableRow</c>
/// через <c>TRUNCATE … WITH (PARTITIONS)</c>, а SQL Server відмовляє в <c>TRUNCATE</c> таблиці,
/// на яку посилається будь-який ключ (процедура знімає й повертає лише ті ключі, які знає).
/// Цілісність тримають явні дії: видалення документа (<c>DocumentDeletionStore</c>), перенос
/// версії (<c>DocumentVersionMigrationStore</c>) і те, що зріз читає лише стилі наявних рядків.
///
/// ⚠ Ключ — як у <see cref="CellValue"/> (PeriodKey, TableRowId, ColumnDefId), але без
/// <c>TableDefId</c>: складеного ключа на колонку немає, нічого перевіряти ним.
/// </remarks>
public sealed class CellStyle
{
    private static readonly Regex HexColor = new(
        "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private CellStyle() { }

    /// <summary>Створює порожнє оформлення комірки (далі — <see cref="Apply"/>).</summary>
    public CellStyle(CellAddress address, int userId, DateTime at)
    {
        PeriodKeyValue = address.PeriodKey.Value;
        TableRowId = address.TableRowId;
        ColumnDefId = address.ColumnDefId;
        UpdatedByUserId = userId;
        UpdatedAt = at;
    }

    /// <summary>Ключ партиції (перший у ключі).</summary>
    public int PeriodKeyValue { get; private set; }

    public long TableRowId { get; private set; }
    public int ColumnDefId { get; private set; }

    /// <summary>Заливка <c>#RRGGBB</c>; <c>null</c> — немає.</summary>
    public string? BackgroundHex { get; private set; }

    /// <summary>Колір тексту <c>#RRGGBB</c>; <c>null</c> — немає.</summary>
    public string? ForegroundHex { get; private set; }

    public bool IsBold { get; private set; }

    public DateTime UpdatedAt { get; private set; }
    public int UpdatedByUserId { get; private set; }

    /// <summary>Усе порожнє: рядок більше нічого не показує і має бути видалений.</summary>
    public bool IsBlank => BackgroundHex is null && ForegroundHex is null && !IsBold;

    public CellAddress Address => new(new PeriodKey(PeriodKeyValue), TableRowId, ColumnDefId);

    /// <summary>Чи рядок кольору — рівно <c>#rrggbb</c> (без прозорості).</summary>
    public static bool IsValidHex(string? value) => value is not null && HexColor.IsMatch(value);

    /// <summary>
    /// Застосовує зміну. Поле <c>null</c> — не чіпати; <c>""</c> — зняти; <c>#rrggbb</c> — задати.
    /// </summary>
    /// <returns><c>true</c>, якщо хоч одне поле справді змінилося (no-op не оновлює автора й час).</returns>
    /// <exception cref="ArgumentException">Колір не у формі <c>#rrggbb</c>.</exception>
    public bool Apply(string? background, string? foreground, bool? isBold, int userId, DateTime at)
    {
        var newBackground = Resolve(background, BackgroundHex, nameof(background));
        var newForeground = Resolve(foreground, ForegroundHex, nameof(foreground));
        var newBold = isBold ?? IsBold;

        if (newBackground == BackgroundHex && newForeground == ForegroundHex && newBold == IsBold)
        {
            return false;
        }

        BackgroundHex = newBackground;
        ForegroundHex = newForeground;
        IsBold = newBold;
        UpdatedByUserId = userId;
        UpdatedAt = at;
        return true;
    }

    private static string? Resolve(string? requested, string? current, string name)
    {
        if (requested is null)
        {
            return current;
        }

        if (requested.Length == 0)
        {
            return null;
        }

        if (!IsValidHex(requested))
        {
            throw new ArgumentException($"Колір має бути у формі #rrggbb: «{requested}».", name);
        }

        // Верхній регістр — одна форма в базі: порівняння «змінилося чи ні» не залежить від регістру.
        return requested.ToUpperInvariant();
    }
}
