namespace Ecr.MethodologyScriptMigration.Conversion;

/// <summary>
/// Імена глобальної області скрипта: без урахування регістру (два імені, що різняться лише регістром, — помилка
/// <c>script.duplicateName</c>) і без ключових слів.
/// </summary>
internal sealed class NameTable
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Чи ім'я недоступне: зайняте або ключове слово.</summary>
    /// <param name="name">Ім'я.</param>
    public bool IsTaken(string name) => ScriptText.IsKeyword(name) || _used.Contains(name);

    /// <summary>Резервує ім'я.</summary>
    /// <param name="name">Ім'я.</param>
    /// <returns><c>false</c> — ім'я вже зайняте чи є ключовим словом.</returns>
    public bool TryReserve(string name) => !ScriptText.IsKeyword(name) && _used.Add(name);

    /// <summary>
    /// Резервує <paramref name="baseName"/>, а якщо воно зайняте — <c>baseName + suffix</c>, далі з номером.
    /// </summary>
    /// <param name="baseName">Бажане ім'я.</param>
    /// <param name="suffix">Суфікс колізії.</param>
    /// <param name="renamed"><c>true</c>, якщо ім'я змінено.</param>
    public string Allocate(string baseName, string suffix, out bool renamed)
    {
        if (TryReserve(baseName))
        {
            renamed = false;
            return baseName;
        }

        renamed = true;
        var candidate = baseName + suffix;
        var n = 2;
        while (!TryReserve(candidate))
        {
            candidate = baseName + suffix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            n++;
        }

        return candidate;
    }
}
