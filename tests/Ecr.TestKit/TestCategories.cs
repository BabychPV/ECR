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

    /// <summary>Потребує реального SQL Server; не входить у прогін за замовчуванням.</summary>
    public const string Integration = "Integration";

    /// <summary>Архітектурні правила — блокуючі в CI.</summary>
    public const string Architecture = "Architecture";
}
