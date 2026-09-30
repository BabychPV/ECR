// src/Ecr.Application/Registries/Rules/RegistryRuleTemplates.cs
using System.Globalization;
using System.Text.Json;

namespace Ecr.Application.Registries.Rules;

/// <summary>
/// Параметри правил довідника і шаблони конструктора (FEATURE-REGISTRY-TABLES §6).
/// </summary>
/// <remarks>
/// ⚠ Параметри — JSON-об'єкт <c>cfg.RegistryRuleDef.ParametersJson</c>; домен перевіряє лише, що
/// це об'єкт, а схема залежить від виду правила (§6):
/// <list type="bullet">
/// <item><c>RequiredWhen</c> — <c>{"field":"X"}</c>;</item>
/// <item><c>CrossRegistry</c> — <c>{"field":"X","registry":"R","key":"PK"}</c>;</item>
/// <item><c>Expression</c> із шаблоном «Сума дочірніх» —
/// <c>{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":0.5}</c>.</item>
/// </list>
///
/// ⛔ Вираз шаблону ГЕНЕРУЄТЬСЯ з параметрів під час збереження опису, а не пишеться руками: сітка
/// читає ті самі параметри для живого індикатора Σ (§8.4), і вираз, що розійшовся б із ними, давав
/// би на екрані одну суму, а на сервері — іншу.
/// </remarks>
public static class RegistryRuleTemplates
{
    /// <summary>Шаблон «Сума дочірніх» (§6).</summary>
    public const string ChildSum = "childSum";

    /// <summary>Параметр «поле» (<c>RequiredWhen</c>, <c>CrossRegistry</c>, <c>childSum</c>).</summary>
    public const string FieldParameter = "field";

    /// <summary>Параметр «цільовий довідник» (<c>CrossRegistry</c>).</summary>
    public const string RegistryParameter = "registry";

    /// <summary>Параметр «ключ цільового довідника» (<c>CrossRegistry</c>).</summary>
    public const string KeyParameter = "key";

    /// <summary>Параметр «шаблон».</summary>
    public const string TemplateParameter = "template";

    /// <summary>Параметр «дочірній довідник» (<c>childSum</c>).</summary>
    public const string ChildParameter = "child";

    /// <summary>Параметр «ціль» (<c>childSum</c>).</summary>
    public const string TargetParameter = "target";

    /// <summary>Параметр «допуск» (<c>childSum</c>).</summary>
    public const string ToleranceParameter = "tolerance";

    /// <summary>Рядковий параметр; <c>null</c> — немає, порожній або не рядок.</summary>
    /// <param name="parametersJson">JSON-об'єкт параметрів.</param>
    /// <param name="name">Ім'я параметра.</param>
    public static string? Text(string? parametersJson, string name)
    {
        using var parsed = Parse(parametersJson);
        if (parsed is null
            || !parsed.RootElement.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Числовий параметр (число або рядок з інваріантним числом); <c>null</c> — немає.</summary>
    /// <param name="parametersJson">JSON-об'єкт параметрів.</param>
    /// <param name="name">Ім'я параметра.</param>
    public static decimal? Number(string? parametersJson, string name)
    {
        using var parsed = Parse(parametersJson);
        if (parsed is null || !parsed.RootElement.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNumber) => parsedNumber,
            _ => null,
        };
    }

    /// <summary>Шаблон конструктора, з якого зроблено правило; <c>null</c> — вираз написано руками.</summary>
    /// <param name="parametersJson">JSON-об'єкт параметрів.</param>
    public static string? TemplateOf(string? parametersJson) => Text(parametersJson, TemplateParameter);

    /// <summary>
    /// Вираз шаблону «Сума дочірніх»:
    /// <c>ABS(REGSUM(child, ROW.&lt;поле композиції&gt; = THIS, ROW.field) - target) &lt;= tolerance</c> (§6)
    /// <c>OR REGCOUNT(child, ROW.&lt;поле композиції&gt; = THIS) = 0</c>.
    /// </summary>
    /// <param name="child">Код дочірнього довідника.</param>
    /// <param name="compositionField">Поле композиції дитини, що вказує на довідник правила.</param>
    /// <param name="field">Поле дитини, яке сумується.</param>
    /// <param name="target">Ціль суми.</param>
    /// <param name="tolerance">Допуск (≥ 0).</param>
    /// <remarks>
    /// ⚠ Фільтр <c>ROW.&lt;поле композиції&gt; = THIS</c> — індексний шлях агрегату
    /// (<c>RegistryForms.Rows</c>): перебираються лише діти запису, а не весь довідник складу.
    /// <para>
    /// ⛔ Хвіст <c>OR REGCOUNT(…) = 0</c> — рішення RT-17a, доповнення до формули §6: склад, якого
    /// ЩЕ немає, — не порушення. Пакет пише один довідник, тож батько завжди зберігається раніше за
    /// своїх дітей, і без хвоста правило рівня <c>Error</c> не дало б створити жодного кейсу (Σ
    /// порожнього складу = 0). Щойно в кейса є хоч один рядок складу — Σ перевіряється повністю.
    /// </para>
    /// <para>
    /// ⚠ <c>REGSUM</c> стоїть ПЕРШИМ: значення першого агрегату виразу рушій кладе в <c>value</c>
    /// порушення — це Σ, яку показує індикатор сітки.
    /// </para>
    /// </remarks>
    public static string ChildSumExpression(string child, string compositionField, string field, decimal target, decimal tolerance)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"ABS(REGSUM('{child}', ROW.{compositionField} = THIS, ROW.{field}) - {target}) <= {tolerance} OR REGCOUNT('{child}', ROW.{compositionField} = THIS) = 0");

    private static JsonDocument? Parse(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return null;
        }

        try
        {
            var parsed = JsonDocument.Parse(parametersJson);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                return parsed;
            }

            parsed.Dispose();
            return null;
        }
        catch (JsonException)
        {
            // Домен не пускає в базу не-об'єкт (RegistryRuleDef.SetParameters); якщо він там таки
            // є — параметра немає, і правило відмовить як «параметр відсутній», а не падінням.
            return null;
        }
    }
}
