using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Api.Startup;

/// <summary>
/// Єдине місце, де налаштовується JSON API.
/// </summary>
/// <remarks>
/// ⚠ Налаштувань ДВА: контролери MVC (<c>AddJsonOptions</c>) і мінімальні API
/// разом із генератором OpenAPI (<c>ConfigureHttpJsonOptions</c>). Доки
/// конвертери додавалися в кожне окремо, розійтися вони могли непомітно —
/// сервер віддавав би одне, а схема описувала інше. Тому обидва виклики
/// звертаються сюди, і сторож
/// <c>ApiConventionTests.Числа_передаються_рядком_щоб_не_втратити_точність</c>
/// перевіряє саме цей метод.
/// </remarks>
public static class EcrJsonSerialization
{
    /// <summary>Додає конвертери, спільні для всіх шляхів серіалізації.</summary>
    /// <param name="options">Опції, що починаються з <c>JsonSerializerDefaults.Web</c>.</param>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // ⚠ Переліки йдуть ІМЕНАМИ, а не числами. За замовчуванням
        // System.Text.Json пише `1`, і клієнт отримує статус версії,
        // стан періоду й рівень методології як безіменні числа: показати їх
        // користувачеві не можна, порівняти зі значенням — теж (`A7-07`).
        //
        // ⛔ Числа ще й НЕСТАБІЛЬНІ як контракт: вставка нового члена в
        // середину переліку мовчки змінює значення всіх наступних, і клієнт
        // починає показувати «Approved» там, де сервер має на увазі
        // «Submitted». Ім'я такого не вміє.
        options.Converters.Add(new JsonStringEnumConverter());

        // ⛔ `decimal` — РЯДКОМ (`D-30`, `docs/build/02-contracts.md` §10).
        // Контракт вимагав цього від початку, а реалізації не було: число
        // їхало числом і втрачало знаки в `JSON.parse` клієнта.
        options.Converters.Add(new DecimalAsStringJsonConverter());
        options.Converters.Add(new NullableDecimalAsStringJsonConverter());
    }
}
