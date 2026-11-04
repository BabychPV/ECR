// src/Ecr.Domain/Entities/Configuration/LookupFilterSpec.cs
using System.Text.RegularExpressions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Формат <see cref="ColumnDef.LookupFilter"/> (HSE401): <c>level=&lt;LEVELCODE&gt;</c> - звуження списку ієрархічного
/// довідника до записів одного рівня. Інших ключів поки немає: невідомий ключ відхиляється при збереженні колонки,
/// щоб фільтр не лежав у шаблоні мертвим текстом.
/// </summary>
public static partial class LookupFilterSpec
{
    /// <summary>Єдиний підтримуваний ключ.</summary>
    public const string LevelKey = "level";

    [GeneratedRegex(@"\Alevel=([A-Za-z0-9_.\-]{1,64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();

    /// <summary>Розбирає фільтр.</summary>
    /// <param name="filter">Рядок фільтра; <c>null</c>/порожній - без звуження.</param>
    /// <param name="levelCode">Код рівня, якщо фільтр задає <c>level=</c>; інакше <c>null</c>.</param>
    /// <returns><c>false</c> - формат не підтримується (непорожній, але не <c>level=CODE</c>).</returns>
    public static bool TryParse(string? filter, out string? levelCode)
    {
        levelCode = null;
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var match = LevelPattern().Match(filter.Trim());
        if (!match.Success)
        {
            return false;
        }

        levelCode = match.Groups[1].Value;
        return true;
    }

    /// <summary>Код рівня з фільтра або <c>null</c> (немає фільтра чи формат не розпізнано).</summary>
    /// <param name="filter">Рядок фільтра.</param>
    public static string? LevelOf(string? filter) => TryParse(filter, out var level) ? level : null;
}
