using System.Globalization;
using Ecr.Application.Localization;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// T2-10: ТАБЛИЦЯ правил читання числа, набраного людиною, за мовою (en → en-US, ru → ru-RU, kz → kk-KZ).
/// Це поведінка ДО T2-10 — T2-10 її не змінив (змінилось лише `.5`/`-.5`/`,5` і `1 2`, див. CultureNumberReaderTests).
/// <list type="bullet">
/// <item>`1 234,5` — пробіл-розряди + кома-дріб: усюди 1234.5.</item>
/// <item>`1,234.5` — кома-розряди + крапка-дріб: лише en (1234.5); ru/kz — відмова (кома там — десятковий).</item>
/// <item>`1.234,5` — крапка-розряди: ніде (відмова; рішення 2026-09-29, у ru/kz крапка — не розряди).</item>
/// <item>`1.234` — крапка не є розрядами в жодній з трьох мов → 1.234.</item>
/// <item>`1,234` — en: НЕОДНОЗНАЧНО (кома — розряди) → відмова; ru/kz: кома — десятковий → 1.234.</item>
/// </list>
/// </summary>
public sealed class CultureNumberTableTests
{
    public static TheoryData<string, string, NumberTextKind, string?> Table() => new()
    {
        { "en", "1 234,5", NumberTextKind.Number, "1234.5" },
        { "ru", "1 234,5", NumberTextKind.Number, "1234.5" },
        { "kz", "1 234,5", NumberTextKind.Number, "1234.5" },

        { "en", "1,234.5", NumberTextKind.Number, "1234.5" },
        { "ru", "1,234.5", NumberTextKind.NotNumber, null },
        { "kz", "1,234.5", NumberTextKind.NotNumber, null },

        { "en", "1.234,5", NumberTextKind.NotNumber, null },
        { "ru", "1.234,5", NumberTextKind.NotNumber, null },
        { "kz", "1.234,5", NumberTextKind.NotNumber, null },

        { "en", "1.234", NumberTextKind.Number, "1.234" },
        { "ru", "1.234", NumberTextKind.Number, "1.234" },
        { "kz", "1.234", NumberTextKind.Number, "1.234" },

        { "en", "1,234", NumberTextKind.Ambiguous, null },
        { "ru", "1,234", NumberTextKind.Number, "1.234" },
        { "kz", "1,234", NumberTextKind.Number, "1.234" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [MemberData(nameof(Table))]
    public void Таблиця_читання_за_мовою(string language, string text, NumberTextKind kind, string? expected)
    {
        var reading = CultureNumberReader.Read(text, NumberCulture.ForLanguage(language));

        Assert.Equal(kind, reading.Kind);
        if (expected is not null)
        {
            Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), reading.Value);
        }
    }
}
