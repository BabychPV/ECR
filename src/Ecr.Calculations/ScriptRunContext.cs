namespace Ecr.Calculations;

/// <summary>
/// Прогін, у межах якого працює модуль скрипта (MS, план §13.9). Scoped: оркестратор заповнює його у scope гілки
/// перед <c>PrepareAsync</c>, і колонка <c>Calculated</c>, прив'язана до виходу іншої методології, читає результати
/// САМЕ цього прогону.
/// </summary>
/// <remarks>
/// ⚠ <c>null</c> — поза прогоном (перегляд, тест, симуляція): тоді читаються результати актуального прогону
/// документа й періоду, тобто те, що користувач бачить у сітці.
/// </remarks>
public sealed class ScriptRunContext
{
    /// <summary>Прогін, що зараз рахується; <c>null</c> — поза прогоном.</summary>
    public long? CalculationRunId { get; set; }
}
