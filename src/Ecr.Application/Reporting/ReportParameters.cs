// src/Ecr.Application/Reporting/ReportParameters.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Reporting;

/// <summary>Оголошення параметра звіту в описі версії (<c>RulesJson</c>, схема 2).</summary>
/// <param name="Code">Ім'я параметра без <c>@</c>; у виразі — <c>@Code</c>.</param>
/// <param name="Type"><c>Number</c>, <c>Text</c>, <c>Boolean</c> або <c>Date</c>; регістр не важить.</param>
/// <param name="Required">
/// Чи побудова без значення неможлива. <c>Required</c> разом із <c>Default</c> —
/// законна пара: замовчування і є тим значенням, без якого не будують.
/// </param>
/// <param name="Default">
/// Значення, яке бере побудова, коли параметр не переданий; JSON-значення
/// оголошеного типу (дата — рядком).
/// </param>
public sealed record ReportParameterCommand(
    string Code,
    string Type,
    bool Required = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Default = null);

/// <summary>Перевірене оголошення параметра: те, на що вже можна спиратися.</summary>
/// <param name="Code">Ім'я параметра.</param>
/// <param name="Type">Тип значення в термінах мови виразів.</param>
/// <param name="Required">Чи побудова без значення неможлива.</param>
/// <param name="Default">Замовчування, уже приведене до типу; <c>null</c> — немає.</param>
public sealed record ReportParameterSpec(string Code, ExpressionValueType Type, bool Required, object? Default);

/// <summary>Значення параметрів, з якими йде побудова.</summary>
/// <param name="Values">Ім'я → значення (замовчування вже підставлені).</param>
/// <param name="Json">
/// Ті самі значення для запису у <c>rpt.ReportSnapshot.ParametersJson</c>;
/// <c>null</c> — версія параметрів не оголошує, і писати нема чого.
/// </param>
public sealed record BoundReportParameters(IReadOnlyDictionary<string, object?> Values, string? Json);

/// <summary>
/// Параметри звіту (<c>@Name</c>, <c>02b</c> §8a): оголошення у версії опису і
/// значення, з якими будується зріз, — ОДНИМ кодом, щоб вони не розійшлися.
/// </summary>
/// <remarks>
/// ⛔ Оголошення перевіряються при СТВОРЕННІ версії, значення — при ПОБУДОВІ, і
/// обидві перевірки ведуть сюди. Другий перелік типів (окремо для опису, окремо
/// для запиту) розійшовся б із першим тихо: опис прийняв би параметр, якого
/// побудова не вміє прочитати, і дізналися б про це з невдалої нічної задачі.
///
/// ⚠ Імена параметрів порівнюються БЕЗ регістру — як усі <c>@Name</c> мови
/// (<c>02b</c> §3.4) і як <c>ReportExpressionScope</c>. Коди КОЛОНОК, навпаки,
/// точні; різниця не косметична, і саме тому вона названа в обох місцях.
/// </remarks>
public static partial class ReportParameters
{
    /// <summary>
    /// Стеля кількості параметрів версії.
    /// </summary>
    /// <remarks>
    /// Параметри не обчислюються на кожному рядку (на відміну від правил), тож
    /// межа тут не про швидкість: опис із сотнями <c>@Name</c> означає, що звіт
    /// намагаються зробити застосунком, а не формою.
    /// </remarks>
    public const int MaxParameters = 50;

    /// <summary>Порожній набір значень — для правил, які параметрів не мають.</summary>
    public static readonly IReadOnlyDictionary<string, object?> None =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Перевіряє оголошення параметрів версії.</summary>
    /// <param name="declared">Оголошення в порядку опису; <c>null</c> — параметрів немає.</param>
    /// <exception cref="BusinessRuleException">
    /// Ім'я порожнє чи не є <c>@Name</c>, повторюється, тип невідомий або
    /// <c>default</c> не того типу — <c>ECR-RPT-0422</c>.
    /// </exception>
    public static IReadOnlyList<ReportParameterSpec> Compile(IReadOnlyList<ReportParameterCommand>? declared)
    {
        declared ??= [];

        if (declared.Count > MaxParameters)
        {
            throw Invalid("*", $"оголошено {declared.Count} параметрів, а стеля — {MaxParameters}");
        }

        var specs = new List<ReportParameterSpec>(declared.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in declared)
        {
            var code = parameter?.Code ?? string.Empty;

            // ⛔ Ім'я мусить бути тим, що вміє прочитати лексер (`@Name`, §3.4):
            // оголошений параметр, на який неможливо послатися, — це опис, який
            // мовчки нічого не дає.
            if (parameter is null || !Name().IsMatch(code))
            {
                throw Invalid(code, "ім'я параметра — літера або «_», далі літери, цифри, «_» і крапки");
            }

            if (!seen.Add(code))
            {
                throw Invalid(code, "параметр оголошено двічі (імена @Name не розрізняють регістр)");
            }

            var type = TypeOf(parameter.Type)
                       ?? throw Invalid(code, $"тип «{parameter.Type}» невідомий: є {KnownTypes}");

            specs.Add(new ReportParameterSpec(
                code, type, parameter.Required, Value(code, type, parameter.Default, "default")));
        }

        return specs;
    }

    /// <summary>Оголошення параметрів ЗБЕРЕЖЕНОЇ версії; схема 1 — порожньо.</summary>
    /// <param name="rulesJson">Вміст <c>rpt.ReportVersion.RulesJson</c>.</param>
    /// <remarks>
    /// ⚠ Не через <see cref="ReportRowRules.Parse"/>: той розбирає і перевіряє
    /// УСІ вирази правил версії, а тут потрібні лише імена й типи — і потрібні
    /// на кожен запит побудови.
    /// </remarks>
    public static IReadOnlyList<ReportParameterSpec> Of(string? rulesJson)
    {
        if (ReportRules.Parse(rulesJson).Schema != ReportRowRules.Schema)
        {
            return [];
        }

        ReportRulesCommand? stored = null;

        try
        {
            stored = JsonSerializer.Deserialize<ReportRulesCommand>(rulesJson!, Options);
        }
        catch (JsonException)
        {
            // Зламані правила = правила за замовчуванням, як їх читає `ReportRules.Parse`.
        }

        return Compile(stored?.Parameters);
    }

    /// <summary>Зводить значення запиту з оголошеннями версії.</summary>
    /// <param name="declared">Оголошення версії.</param>
    /// <param name="values">Значення з запиту; <c>null</c> — жодного не передано.</param>
    /// <exception cref="BusinessRuleException">
    /// Невідоме ім'я, обов'язковий параметр без значення і без <c>default</c>
    /// або значення не того типу — <c>ECR-RPT-0422</c>.
    /// </exception>
    public static BoundReportParameters Bind(
        IReadOnlyList<ReportParameterSpec> declared, IReadOnlyDictionary<string, object?>? values)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var given = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, raw) in values ?? None)
        {
            // ⛔ Зайве ім'я — відмова, а не тиша. Друкарська помилка в імені
            // інакше дала б зріз, побудований із замовчування, і він виглядав
            // би як побудований із переданим значенням.
            if (!declared.Any(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                throw Unknown(code);
            }

            given[code] = raw;
        }

        var effective = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in declared)
        {
            // Явний `null` = «не передано»: інакше обов'язковий параметр
            // обходився б словом `null`, і правило рахувало б на порожнечі.
            var raw = given.TryGetValue(spec.Code, out var passed) ? CellValueReader.Normalize(passed) : null;

            if (raw is null)
            {
                if (spec.Required && spec.Default is null)
                {
                    throw Required(spec.Code);
                }

                effective[spec.Code] = spec.Default;
                continue;
            }

            effective[spec.Code] = Value(spec.Code, spec.Type, raw, "value");
        }

        return new BoundReportParameters(
            effective, declared.Count == 0 ? null : JsonSerializer.Serialize(effective, Options));
    }

    /// <summary>Те саме, але значення приходять уже записаними в JSON (задача черги).</summary>
    /// <param name="declared">Оголошення версії.</param>
    /// <param name="parametersJson">Значення як JSON-об'єкт; <c>null</c> — жодного.</param>
    /// <exception cref="BusinessRuleException">JSON не об'єкт або значення не сходяться.</exception>
    public static BoundReportParameters Bind(IReadOnlyList<ReportParameterSpec> declared, string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return Bind(declared, values: null);
        }

        Dictionary<string, object?>? values;

        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson, Options);
        }
        catch (JsonException)
        {
            // ⛔ Гучно: зламаний JSON, прочитаний як «параметрів немає», дав би
            // зріз на замовчуваннях замість відмови.
            throw Invalid("*", "значення параметрів не є JSON-об'єктом");
        }

        return Bind(declared, values);
    }

    /// <summary>Оголошений тип: <c>Number</c>, <c>Text</c>, <c>Boolean</c> або <c>Date</c>.</summary>
    /// <remarks>
    /// ⚠ Той самий словник імен, що приймає <c>POST /expressions/validate</c>
    /// для оточення діалекту <c>Report</c> (<c>ReportSymbolDeclaration.Type</c>).
    /// Третє написання тих самих чотирьох типів зробило б редактор правил і
    /// опис звіту двома різними мовами.
    /// </remarks>
    private static ExpressionValueType? TypeOf(string? type)
        => type is not null
           && Enum.TryParse<ExpressionValueType>(type, ignoreCase: true, out var parsed)
           && !int.TryParse(type, out _)
           && parsed is not (ExpressionValueType.Null or ExpressionValueType.Error)
            ? parsed
            : null;

    private static string KnownTypes => string.Join(
        ", ",
        new[]
        {
            ExpressionValueType.Number, ExpressionValueType.Text,
            ExpressionValueType.Boolean, ExpressionValueType.Date,
        });

    /// <summary>Приводить значення до ОГОЛОШЕНОГО типу; <c>null</c> лишається <c>null</c>.</summary>
    /// <remarks>
    /// ⛔ Тип диктує ОГОЛОШЕННЯ, а не вигляд значення — рівно з тієї ж причини,
    /// що й у <see cref="CellValueReader"/>: через HTTP усе приходить
    /// <c>JsonElement</c>-ом, і розбір «за типом CLR» промахувався б завжди.
    /// Рядок у числовому параметрі — канонічний дротовий формат десяткового (як
    /// у <c>DecimalAsStringJsonConverter</c>): JS-число губить знаки після ~15.
    /// Граматика та сама інваріантна (<see cref="MethodologyConstant.TryParseNumeric"/>);
    /// «1,5», «5 т», порожній — відмова. Рядок понад 16 знаків дробу — теж відмова,
    /// не обріз (JSON-число поводиться як і раніше).
    /// </remarks>
    private static object? Value(string code, ExpressionValueType type, object? raw, string part)
    {
        var value = CellValueReader.Normalize(raw);

        return (type, value) switch
        {
            (_, null) => null,
            (ExpressionValueType.Number, decimal number) => number,
            (ExpressionValueType.Number, string text)
                when MethodologyConstant.TryParseNumeric(text, out var parsed) && WithinScale(parsed) => parsed,
            (ExpressionValueType.Text, string text) => text,
            (ExpressionValueType.Boolean, bool flag) => flag,
            (ExpressionValueType.Date, DateTime date) => date,
            (ExpressionValueType.Date, string text) when CellDateParser.TryParse(text, out var parsed) => parsed,
            _ => throw Mismatch(code, type, part),
        };
    }

    /// <summary>Не більше 16 знаків дробу — межа <c>NumericPolicy.DefaultOutputScale</c> і <c>decimal(34,16)</c>.</summary>
    private static bool WithinScale(decimal number) => decimal.Round(number, 16) == number;

    /// <summary>Ім'я параметра так, як його читає лексер (<c>02b</c> §3.4).</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*$")]
    private static partial Regex Name();

    private static BusinessRuleException Invalid(string code, string reason)
        => new(
            ErrorCodes.ReportInvalid,
            $"Параметр «{code}»: {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0422.parameter",
                ["code"] = code,
                ["reason"] = reason,
            });

    private static BusinessRuleException Unknown(string code)
        => new(
            ErrorCodes.ReportInvalid,
            $"Параметра «{code}» версія звіту не оголошує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0422.parameterUnknown",
                ["code"] = code,
            });

    private static BusinessRuleException Required(string code)
        => new(
            ErrorCodes.ReportInvalid,
            $"Параметр «{code}» обов'язковий, а значення й замовчування немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0422.parameterRequired",
                ["code"] = code,
            });

    private static BusinessRuleException Mismatch(string code, ExpressionValueType type, string part)
        => new(
            ErrorCodes.ReportInvalid,
            $"Параметр «{code}» ({part}) очікує значення типу {type}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0422.parameterType",
                ["code"] = code,
                ["expectedType"] = type.ToString(),
                ["part"] = part,
            });
}
