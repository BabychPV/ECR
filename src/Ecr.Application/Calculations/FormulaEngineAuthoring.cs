// src/Ecr.Application/Calculations/FormulaEngineAuthoring.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Calculations;

/// <summary>
/// Чи дозволено ЗАВОДИТИ нові версії формульного рушія: порожня чернетка <c>Formulas</c>, клон формульної версії,
/// публікація формульної версії й імпорт пакета <c>ecr-methodology-package</c> (він заводить формульні чернетки).
/// </summary>
/// <remarks>
/// ✎ 24.11, рішення людини: «має бути тільки наш новий редакор» (P-1124-1). У продукті ключ вимкнено
/// (<c>appsettings.json</c>), і всі чотири входи відповідають <c>ECR-CALC-0422</c>
/// (<c>err.ECR-CALC-0422.formulaEngineRetired</c>). Вмикають його лише інструменти й випробування, яким потрібні
/// формульні версії як заготовка: хост тестів API (<c>EcrApiFactory</c>), стенд e2e (<c>tools/e2e-stand.ps1</c>) і
/// збирання Land з AF (<c>tools/land/Build-LandTemplate.ps1</c>). Наявні формульні версії перетворює на скрипт
/// P-1124-2; рантайм формульного рушія цим не вимикається.
/// <para>
/// ⚠ Обробники приймають політику необов'язковим параметром: <c>null</c> — дозволено (пряме конструювання в
/// модульних тестах). DI продукту реєструє її завжди (<c>Program.cs</c>), і тест API з вимкненим ключем це доводить.
/// </para>
/// </remarks>
/// <param name="Enabled">Чи дозволено заводити формульні версії.</param>
public sealed record FormulaEngineAuthoring(bool Enabled)
{
    /// <summary>Ключ конфігурації.</summary>
    public const string EnabledKey = "Calculations:FormulaEngineAuthoring:Enabled";

    /// <summary>Ключ повідомлення відмови.</summary>
    public const string RetiredMessageKey = "err.ECR-CALC-0422.formulaEngineRetired";

    /// <summary>Розбирає значення ключа; порожнє чи нерозбірне — вимкнено.</summary>
    /// <param name="value">Значення <see cref="EnabledKey"/>.</param>
    /// <returns>Політику.</returns>
    public static FormulaEngineAuthoring From(string? value)
        => new(bool.TryParse(value, out var enabled) && enabled);

    /// <summary>Чи дозволено: <c>null</c> (пряме конструювання обробника) — так.</summary>
    /// <param name="policy">Політика з DI.</param>
    /// <returns><c>true</c>, якщо формульну версію можна заводити.</returns>
    public static bool Allows(FormulaEngineAuthoring? policy) => policy?.Enabled ?? true;

    /// <summary>Відмова «формульний рушій виведено».</summary>
    /// <param name="action">Що просили: <c>create</c>, <c>clone</c>, <c>publish</c>, <c>import</c>.</param>
    /// <param name="version">Номер версії, якщо є.</param>
    /// <returns>Виняток для кидка.</returns>
    public static BusinessRuleException Retired(string action, string? version)
        => new(
            ErrorCodes.MethodologyInvalid,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Формульний рушій виведено з авторингу ({action}{(version is null ? string.Empty : " " + version)}): нові версії методологій — лише скрипт."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = RetiredMessageKey,
                ["action"] = action,
                ["version"] = version ?? string.Empty,
            });
}
