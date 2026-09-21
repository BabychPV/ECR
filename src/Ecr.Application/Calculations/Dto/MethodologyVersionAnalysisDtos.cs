// src/Ecr.Application/Calculations/Dto/MethodologyVersionAnalysisDtos.cs
namespace Ecr.Application.Calculations.Dto;

/// <summary>
/// Покриття версії: які виходи куди лягають і які колонки чекають на вихід (<c>BE-25</c>).
/// </summary>
/// <param name="MethodologyVersionId">Версія.</param>
/// <param name="Outputs">
/// Кожен оголошений вихід з АКТИВНИМИ прив'язками; порожній перелік — вихід рахується,
/// але не лягає нікуди.
/// </param>
/// <param name="WaitingBindings">
/// Активні прив'язки, чий <c>outputCode</c> ця версія не оголошує: колонка чекає на
/// вихід, якого немає, і лишиться порожньою.
/// </param>
public sealed record MethodologyCoverageDto(
    int MethodologyVersionId,
    IReadOnlyList<MethodologyOutputCoverageDto> Outputs,
    IReadOnlyList<CalculationBindingDto> WaitingBindings);

/// <summary>Один вихід версії і колонки, куди він лягає.</summary>
/// <param name="Code">Код виходу.</param>
/// <param name="Bindings">Активні прив'язки цього виходу.</param>
public sealed record MethodologyOutputCoverageDto(
    string Code,
    IReadOnlyList<CalculationBindingDto> Bindings);

/// <summary>Що саме порівнюється у двох версіях.</summary>
public enum MethodologyDiffItemKind
{
    /// <summary>Формула.</summary>
    Formula = 1,

    /// <summary>Константа (варіант за категорією, речовиною й датою початку).</summary>
    Constant = 2,

    /// <summary>Тест золотого набору.</summary>
    TestCase = 3,
}

/// <summary>Вид зміни.</summary>
public enum MethodologyDiffChange
{
    /// <summary>Є лише в новій версії.</summary>
    Added = 1,

    /// <summary>Є лише в базовій версії.</summary>
    Removed = 2,

    /// <summary>Є в обох, але відрізняється.</summary>
    Changed = 3,
}

/// <summary>Різниця двох версій методології — лише читання (<c>BE-25</c>, макет <c>mv-compare</c>).</summary>
/// <param name="BaseVersionId">Версія, з якою порівнюють («було»).</param>
/// <param name="MethodologyVersionId">Версія, яку порівнюють («стало»).</param>
/// <param name="Items">Відмінності; порожній перелік — версії однакові за цими наборами.</param>
public sealed record MethodologyVersionDiffDto(
    int BaseVersionId,
    int MethodologyVersionId,
    IReadOnlyList<MethodologyDiffItemDto> Items);

/// <summary>Одна відмінність.</summary>
/// <param name="Kind">Формула, константа чи тест.</param>
/// <param name="Code">Код запису.</param>
/// <param name="Category">Константа: категорія звуження; <c>null</c> — спільна або не константа.</param>
/// <param name="SubstanceEntryId">Константа: речовина звуження.</param>
/// <param name="ValidFrom">Константа: перший чинний день варіанта.</param>
/// <param name="Change">Додано, прибрано чи змінено.</param>
/// <param name="ChangedFields">Які поля змінено; порожньо для доданого й прибраного.</param>
/// <param name="Before">Головне значення в базовій версії (вираз, значення константи, очікування тесту).</param>
/// <param name="After">Головне значення в новій версії.</param>
public sealed record MethodologyDiffItemDto(
    MethodologyDiffItemKind Kind,
    string Code,
    string? Category,
    long? SubstanceEntryId,
    DateOnly? ValidFrom,
    MethodologyDiffChange Change,
    IReadOnlyList<string> ChangedFields,
    string? Before,
    string? After);
