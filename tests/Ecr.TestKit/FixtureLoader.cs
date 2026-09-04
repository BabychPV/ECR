using System.Text.Json;

namespace Ecr.TestKit;

/// <summary>
/// Завантажує фікстури з <c>water-demo.json</c>.
/// </summary>
/// <remarks>
/// Очікувані числа беруться **з файлу**, а не з коду тесту: інакше правка
/// очікування стає непомітною, а саме через це «зелені» тести перестають
/// щось означати.
/// </remarks>
public static class FixtureLoader
{
    /// <summary>Завантажений набір.</summary>
    public static WaterDemoFixture Load()
        => throw new NotImplementedException(
            "TODO: прочитати Fixtures/water-demo.json з каталогу виводу; " +
            "десеріалізувати в WaterDemoFixture; кешувати статично.");

    /// <summary>Очікуване значення за шляхом, напр. <c>\"Water_07.Main.Total\" → \"7001001\"</c>.</summary>
    public static decimal ExpectedDecimal(string path, string key)
        => throw new NotImplementedException(
            "TODO: дістати з розділу expected; парсити інваріантно як decimal. " +
            "Відсутній ключ — це помилка тесту, а не привід повернути 0.");
}

/// <summary>Модель фікстури.</summary>
public sealed class WaterDemoFixture
{
    /// <summary>Сирий JSON — для розділів, які тест читає динамічно.</summary>
    public required JsonDocument Raw { get; init; }
}
