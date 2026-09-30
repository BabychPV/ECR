// src/Ecr.Domain/Entities/Configuration/RegistryImportProfile.cs
using System.Text.Json;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Збережений профіль імпорту довідника: розкладка файлу й відображення
/// джерел на поля (<c>RegistryImportSpec</c>, FEATURE-REGISTRY-TABLES §4.6, §8.6;
/// міграція <c>RK05RegistryImportProfile</c>, рішення <c>D-170</c>).
/// </summary>
/// <remarks>
/// <para>
/// Профіль потрібен, щоб наступна ревізія HMB імпортувалась у два кліки: людина
/// один раз налаштовує аркуш, орієнтацію, рядки заголовка й цілі, а далі лише
/// обирає профіль. Сюди ж потрапляє вибір авторитетного стовпця-дубля
/// (<c>D-196</c>: <c>"duplicateSources": {"CO": "Carbon_Monoxide"}</c>) — він
/// частина <see cref="SpecJson"/>, окремого стовпця не має.
/// </para>
/// <para>
/// ⚠ Тут перевіряється лише синтаксис: <see cref="SpecJson"/> мусить бути
/// JSON-об'єктом — так само, як <see cref="RegistryRuleDef.SetParameters"/>.
/// Схему специфікації знає планувальник імпорту (RT-18a), і відмову людині з
/// власним <c>messageKey</c> дає обробник профілів (RT-18c) ДО побудови
/// сутності. Сюди зламаний JSON доходить лише через помилку в коді, тому
/// відмова — <see cref="ArgumentException"/>, як у <see cref="RegistryKeyDef"/>:
/// 500 чесніший за вигаданий текст. Для вставок повз домен (скрипти,
/// перенесення) те саме тримає база — <c>CK_RegImpProfile_Json</c>.
/// </para>
/// <para>
/// <see cref="RowVersion"/> — токен <c>If-Match</c> для зміни й видалення
/// профілю: дві людини, що правлять один профіль, не перезаписують одна одну
/// мовчки (<c>profileChanged</c>, RT-18c).
/// </para>
/// </remarks>
public sealed class RegistryImportProfile : Entity<int>
{
    private RegistryImportProfile() { }

    /// <summary>Створює профіль імпорту.</summary>
    /// <param name="registryDefId">Довідник, у який імпортує профіль.</param>
    /// <param name="code">Код профілю; унікальний у межах довідника (<c>UQ_RegistryImportProfile</c>).</param>
    /// <param name="name">Назва мовами каталогу — її людина бачить у списку профілів майстра.</param>
    /// <param name="specJson">Специфікація імпорту — JSON-об'єкт.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент створення в UTC.</param>
    /// <exception cref="ArgumentException">
    /// Довідник не заданий або специфікація порожня, не є JSON чи не є JSON-об'єктом.
    /// </exception>
    public RegistryImportProfile(
        int registryDefId, EcrCode code, LocalizedText name, string specJson, int userId, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryDefId);

        RegistryDefId = registryDefId;
        Code = code.Value;
        Update(name, specJson, userId, utcNow);
    }

    /// <summary>Довідник, у який імпортує профіль.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>Код профілю; не змінюється після створення.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Назва мовами каталогу.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Специфікація імпорту (<c>RegistryImportSpec</c>) — JSON-об'єкт.</summary>
    public string SpecJson { get; private set; } = null!;

    /// <summary>Момент останньої зміни в UTC.</summary>
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Автор останньої зміни.</summary>
    public int UpdatedByUserId { get; private set; }

    /// <summary>Токен оптимістичної конкуренції (<c>If-Match</c>); ставить база.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Замінює назву й специфікацію профілю.</summary>
    /// <param name="name">Нова назва.</param>
    /// <param name="specJson">Нова специфікація — JSON-об'єкт.</param>
    /// <param name="userId">Автор зміни.</param>
    /// <param name="utcNow">Момент зміни в UTC.</param>
    /// <exception cref="ArgumentException">Специфікація порожня, не є JSON чи не є JSON-об'єктом.</exception>
    /// <remarks>
    /// ⛔ Код і довідник не змінюються: код — адреса профілю в посиланнях і
    /// скриптах, а специфікація, написана під поля одного довідника, для іншого
    /// синтаксично ціла і відображає не те. Інший довідник = новий профіль.
    /// </remarks>
    public void Update(LocalizedText name, string specJson, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureJsonObject(specJson);

        NameL10n = name;
        SpecJson = specJson;
        UpdatedByUserId = userId;
        UpdatedAt = utcNow;
    }

    // Лише синтаксис і форма кореня: зберегти зламаний JSON означало б, що
    // помилка виявиться при наступному імпорті — у того, хто її не робив.
    private static void EnsureJsonObject(string specJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specJson);

        try
        {
            using var parsed = JsonDocument.Parse(specJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"Специфікація імпорту має бути JSON-об'єктом, а не {parsed.RootElement.ValueKind}.",
                    nameof(specJson));
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Специфікація імпорту не є валідним JSON: {ex.Message}", nameof(specJson), ex);
        }
    }
}
