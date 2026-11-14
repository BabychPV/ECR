// tests/Ecr.Api.Tests/EmptyLayoutRegistry.cs
using Ecr.Application.Ports;

namespace Ecr.Api.Tests;

/// <summary>
/// Реєстр розкладок без жодної річної версії (D-474 «вимкнено»), незалежно від того, що лежить у спільній тестовій базі.
/// </summary>
/// <remarks>
/// ⛔ B121: справжній <c>TemplateLayoutRegistry</c> читає <c>cfg.TemplateVersion</c> СПІЛЬНОЇ бази колекції на старті
/// хоста й далі раз на 10 с. Будь-яка версія <c>YearWithMonthColumns</c>, яку лишив інший тест шарду, вмикає роутер
/// місячних колонок (<c>HasAny</c>), і читання, що йдуть через нього, коштують інакше (GET зрізу: +1
/// <c>doc.TableInstance</c>, CI 43594816074, api-3: 33 замість 32). Тоді число звернень храповика залежить від порядку
/// тестів, а не від коду. Тести з точною кількістю звернень, чиї документи не річні, пінять цей реєстр через
/// <see cref="EcrApiFactory.LayoutRegistry"/>.
/// </remarks>
public sealed class EmptyLayoutRegistry : ITemplateLayoutRegistry
{
    /// <summary>Єдиний екземпляр (стану немає).</summary>
    public static readonly EmptyLayoutRegistry Instance = new();

    private EmptyLayoutRegistry()
    {
    }

    /// <inheritdoc />
    public bool HasAny => false;

    /// <inheritdoc />
    public bool IsYearVersion(int templateVersionId) => false;

    /// <inheritdoc />
    public bool IsYearSheet(int sheetDefId) => false;

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
