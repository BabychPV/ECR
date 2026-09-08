namespace Ecr.TestKit;

/// <summary>
/// Константи трейтів. Дозволяють ганяти тести окремого етапу і відділяти
/// інтеграційні від решти.
/// </summary>
public static class TestCategories
{
    /// <summary>Назва трейта етапу.</summary>
    public const string Stage = "Stage";

    /// <summary>Назва трейта категорії.</summary>
    public const string Category = "Category";

    public const string Stage1 = "Stage1";
    public const string Stage2 = "Stage2";
    public const string Stage3 = "Stage3";
    public const string Stage4 = "Stage4";
    public const string Stage5 = "Stage5";
    public const string Stage6 = "Stage6";
    public const string Stage7 = "Stage7";

    /// <summary>Потребує реального SQL Server; не входить у прогін за замовчуванням.</summary>
    public const string Integration = "Integration";

    /// <summary>Архітектурні правила — блокуючі в CI.</summary>
    public const string Architecture = "Architecture";

    /// <summary>Назва трейта «чим саме є ця перевірка».</summary>
    public const string Check = "Check";

    /// <summary>
    /// Перевірка читає ТЕКСТ вихідних файлів і не виконує застосунок.
    /// </summary>
    /// <remarks>
    /// ⛔ Позначка існує, щоб зелений результат такої перевірки не рахували за
    /// підтвердження поведінки: статичний сторож лишається зеленим під будь-якою
    /// мутацією, яка зберігає текст. Поведінку доводять сценарні й інтеграційні
    /// тести (директива №09 §8.2).
    /// </remarks>
    public const string Static = "Static";
}
