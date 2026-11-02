namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Порівняння версій шаблону «по-людськи» — дзеркало <c>compareVersions</c> клієнта
/// (<c>features/documents/documentVersion.ts</c>): числа порівнюються числами (2.10 після 2.9), текст — текстом, число
/// раніше за текст, коротша версія, що є початком довшої, менша (2.3 &lt; 2.3.1). Початкове «v» ігнорується.
/// </summary>
internal static class VersionOrder
{
    // Версія в `cfg.TemplateVersion` - до 20 символів, тож порівняння дешеве.
    private static readonly char[] Separators = ['.', '-', '+'];

    /// <summary>Від'ємне - <paramref name="a"/> раніше за <paramref name="b"/>; нуль - рівні.</summary>
    public static int Compare(string a, string b)
    {
        var left = Segments(a);
        var right = Segments(b);
        var length = Math.Max(left.Length, right.Length);

        for (var i = 0; i < length; i++)
        {
            if (i >= left.Length)
            {
                return -1;
            }

            if (i >= right.Length)
            {
                return 1;
            }

            var x = left[i];
            var y = right[i];
            var xNumber = IsNumber(x);
            var yNumber = IsNumber(y);
            if (xNumber && yNumber)
            {
                // Без переповнення: незначущі нулі зрізаються, далі довжина, далі посимвольно.
                var nx = x.TrimStart('0');
                var ny = y.TrimStart('0');
                var byLength = nx.Length.CompareTo(ny.Length);
                var byValue = byLength != 0 ? byLength : string.CompareOrdinal(nx, ny);
                if (byValue != 0)
                {
                    return byValue < 0 ? -1 : 1;
                }

                continue;
            }

            if (xNumber)
            {
                return -1;
            }

            if (yNumber)
            {
                return 1;
            }

            var byText = string.CompareOrdinal(x, y);
            if (byText != 0)
            {
                return byText < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    private static string[] Segments(string version)
    {
        var text = version.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        return [.. text.Split(Separators).Select(part => part.ToLowerInvariant())];
    }

    private static bool IsNumber(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);
}
