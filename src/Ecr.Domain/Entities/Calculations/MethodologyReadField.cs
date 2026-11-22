using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// ✎ D-568: поле ВЛАСНОЇ таблиці, яке читає скрипт версії (<c>row.X</c>, <c>row.X[m]</c>), і чи читає він місячне поле за
/// ІНШИЙ місяць, ніж свій. Матеріалізується зі скомпільованого скрипта разом із таблицями читання.
/// </summary>
/// <remarks>
/// ⚠ Навіщо рядок у базі: прохід читача (місяць-власник колонки <c>Formula</c>/<c>Calculated</c> річної розкладки переписав
/// число, яке читає скрипт іншого місяця) мусить знайти саме тих читачів, не компілюючи кожен скрипт.
/// <para>
/// Службові рядки: <see cref="RecordedMarker"/> (порожній код) — «перелік записано» (версія, що не читає жодного поля, від
/// версії без запису не відрізнилась би); <see cref="AllFields"/> — «читає будь-що» (скрипт не скомпілювався при
/// дозаписі). Версія без жодного рядка — запис відсутній: споживач вважає її читачем усього (консервативно).
/// </para>
/// </remarks>
public sealed class MethodologyReadField : Entity<int>
{
    /// <summary>Код службового рядка «перелік записано».</summary>
    public const string RecordedMarker = "";

    /// <summary>Код службового рядка «читає будь-яке поле за будь-який місяць».</summary>
    public const string AllFields = "*";

    /// <summary>Довжина коду поля (як код колонки).</summary>
    public const int MaxFieldCodeLength = 128;

    private MethodologyReadField() { }

    /// <summary>Створює запис.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="fieldCode">Ім'я поля рядка (або службовий код).</param>
    /// <param name="otherMonths">Місячне поле читається за інший місяць, ніж свій (чи індекс невідомий).</param>
    public MethodologyReadField(int methodologyVersionId, string fieldCode, bool otherMonths)
    {
        ArgumentNullException.ThrowIfNull(fieldCode);
        if (fieldCode.Length > MaxFieldCodeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldCode));
        }

        MethodologyVersionId = methodologyVersionId;
        FieldCode = fieldCode;
        OtherMonths = otherMonths;
    }

    /// <summary>Версія, чий скрипт читає поле.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>Ім'я поля рядка; унікальне в межах версії.</summary>
    public string FieldCode { get; private set; } = null!;

    /// <summary>Місячне поле читається за інший місяць (<c>row.X[m]</c>, m — не свій місяць).</summary>
    public bool OtherMonths { get; private set; }
}
