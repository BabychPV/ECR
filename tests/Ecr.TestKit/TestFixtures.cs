namespace Ecr.TestKit;

/// <summary>
/// Шлях до спільних зразків.
/// </summary>
/// <remarks>
/// ⚠ Зразки живуть в одному місці і читаються обома боками — серверними
/// тестами і клієнтськими. Копія на кожному боці розійшлася б при першій
/// правці, і обидва тести лишалися б зеленими: саме так виглядає межа, яку
/// ніхто не перевіряє (<c>D6-08</c>, <c>A7-04</c>).
/// </remarks>
public static class TestFixtures
{
    /// <summary>Повний шлях до зразка в каталозі виводу.</summary>
    /// <param name="name">Ім'я файла, наприклад <c>health-response.json</c>.</param>
    public static string Path(string name)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
