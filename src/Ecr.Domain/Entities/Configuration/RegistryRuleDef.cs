// src/Ecr.Domain/Entities/Configuration/RegistryRuleDef.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Правило цілісності довідника (<c>ФВ-8.12</c>, директива №06 <c>H-10</c>).
/// </summary>
/// <remarks>
/// ⛔ Сутність з'явилася разом із конструктором, а не раніше: до кроку 8
/// правила довідників описувалися в <c>reference/design/07-data-model.md</c> і
/// не існували ні в схемі, ні в домені. Тобто <c>ФВ-8.12</c> вимагала
/// редактора правил, редагувати в якому було нічого.
///
/// ⚠ Правило прив'язане до <b>довідника</b>, а не до таблиці, і саме тому це
/// не <see cref="ValidationRule"/>: та живе під <c>TableDefId</c> і перевіряє
/// комірки документа. Довідник документом не обмежений — його записи
/// резолвляться в кожному періоді кожного проєкту.
/// </remarks>
public sealed class RegistryRuleDef : Entity<int>
{
    private RegistryRuleDef() { }

    /// <summary>Створює правило довідника.</summary>
    /// <param name="registryDefId">Довідник, якому належить правило.</param>
    /// <param name="code">Код правила; він же в тексті порушення.</param>
    /// <param name="ruleKind">Вид правила — один із чотирьох.</param>
    /// <param name="expression">
    /// Предикат або вираз параметра. Для <see cref="RegistryRuleKind.Expression"/>
    /// це і є правило; для решти — умова, за якої правило застосовується.
    /// </param>
    /// <param name="severity">Рівень: <c>Info</c>, <c>Warning</c>, <c>Error</c>.</param>
    /// <param name="message">Текст порушення мовами каталогу.</param>
    /// <param name="parametersJson">
    /// Параметри виду правила: поле, група унікальності, цільовий довідник.
    /// <c>null</c> — правило параметрів не потребує.
    /// </param>
    /// <exception cref="DomainException">
    /// Вид правила поза переліком або параметри не є JSON-об'єктом —
    /// <c>ECR-REG-0422</c>.
    /// </exception>
    public RegistryRuleDef(
        int registryDefId,
        EcrCode code,
        RegistryRuleKind ruleKind,
        string expression,
        ValidationSeverity severity,
        LocalizedText message,
        string? parametersJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(message);

        // ⛔ Вид перевіряється явно, а не покладається на приведення числа.
        // `(RegistryRuleKind)7` — легальний C#, і саме так у таблицю потрапляє
        // правило, якого рушій не знає: воно не спрацьовує ніколи і виглядає
        // при цьому налаштованим.
        if (!Enum.IsDefined(ruleKind))
        {
            throw new DomainException(
                "ECR-REG-0422",
                $"Вид правила довідника {(byte)ruleKind} не входить у перелік із чотирьох.");
        }

        RegistryDefId = registryDefId;
        Code = code.Value;
        RuleKind = ruleKind;
        Expression = expression;
        Severity = severity;
        MessageL10n = message;
        IsActive = true;
        SetParameters(parametersJson);
    }

    /// <summary>Довідник, якому належить правило.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>Код правила; входить у ключ унікальності разом із довідником.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Вид правила — один із чотирьох (<c>H-10</c>).</summary>
    public RegistryRuleKind RuleKind { get; private set; }

    /// <summary>Предикат правила діалектом виразів ECR.</summary>
    public string Expression { get; private set; } = null!;

    /// <summary>Рівень порушення.</summary>
    public ValidationSeverity Severity { get; private set; }

    /// <summary>Текст порушення мовами каталогу.</summary>
    public LocalizedText MessageL10n { get; private set; } = null!;

    /// <summary>Параметри виду правила у форматі JSON; <c>null</c> — немає.</summary>
    public string? ParametersJson { get; private set; }

    /// <summary>Чи застосовується правило.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Записує параметри виду правила.</summary>
    /// <param name="json">JSON-об'єкт або <c>null</c>.</param>
    /// <exception cref="DomainException">Рядок не є JSON-об'єктом — <c>ECR-REG-0422</c>.</exception>
    /// <remarks>
    /// Перевіряється лише синтаксис — так само, як в <c>RegistryEntryLink</c>:
    /// схема параметрів залежить від виду правила і живе в обробнику. Але
    /// зберегти зламаний JSON означало б, що помилка виявиться при читанні —
    /// у того, хто її не робив.
    /// </remarks>
    public void SetParameters(string? json)
    {
        if (json is null)
        {
            ParametersJson = null;
            return;
        }

        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new DomainException(
                    "ECR-REG-0422", "Параметри правила мають бути JSON-об'єктом.");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new DomainException(
                "ECR-REG-0422", $"Параметри правила не є валідним JSON: {ex.Message}");
        }

        ParametersJson = json;
    }

    /// <summary>Змінює тіло правила, не міняючи його виду.</summary>
    /// <param name="expression">Новий предикат.</param>
    /// <param name="severity">Новий рівень.</param>
    /// <param name="message">Новий текст порушення.</param>
    /// <param name="parametersJson">Нові параметри; <c>null</c> — прибрати.</param>
    /// <remarks>
    /// ⛔ Вид правила не змінюється ніколи. Параметри і предикат означають для
    /// кожного виду різне, і зміна виду при збереженні старих параметрів дала
    /// б правило, яке синтаксично ціле і перевіряє не те. Змінити вид = завести
    /// нове правило і прибрати старе.
    /// </remarks>
    public void Update(
        string expression, ValidationSeverity severity, LocalizedText message, string? parametersJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(message);

        Expression = expression;
        Severity = severity;
        MessageL10n = message;
        SetParameters(parametersJson);
    }

    /// <summary>Вмикає або вимикає правило.</summary>
    /// <param name="isActive">Нове значення.</param>
    /// <remarks>
    /// Вимкнення замість видалення: правило, яке колись діяло, пояснює, чому
    /// наявні записи виглядають саме так.
    /// </remarks>
    public void SetActive(bool isActive) => IsActive = isActive;
}
