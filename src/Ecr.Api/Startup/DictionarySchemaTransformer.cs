using System.Collections;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ecr.Api.Startup;

/// <summary>
/// Дописує в схему словників опис їхніх значень.
/// </summary>
/// <remarks>
/// ⚠ Генератор OpenAPI описує <c>IReadOnlyDictionary&lt;string, object?&gt;</c>
/// як <c>{"type":"object"}</c> — без <c>additionalProperties</c>. Формально це
/// не помилка, але <c>openapi-typescript</c> читає такий опис однозначно:
/// «об'єкт без жодної дозволеної властивості», тобто
/// <c>Record&lt;string, never&gt;</c>. У клієнті це означає, що в
/// <c>RowDto.cells</c> **не можна покласти жодного значення** — типізований
/// клієнт стає непридатним рівно там, де лежать дані документа.
/// <para>
/// ⛔ Дефект знайдено лише на Етапі 6, коли типи вперше згенерували з живого
/// OpenAPI (<c>Q-017</c>). Ані збірка, ані тести його не бачать: сервер
/// серіалізує словники правильно, і хиба є тільки в **описі**.
/// </para>
/// </remarks>
public sealed class DictionarySchemaTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (!IsDictionary(context.JsonTypeInfo.Type))
        {
            return Task.CompletedTask;
        }

        // ⚠ Порожня схема, а не `true`: у JSON Schema `{}` означає «будь-яке
        // значення», і саме так це читає генератор клієнта — `unknown`.
        // Значення комірки справді може бути числом, текстом, датою або
        // порожнечею, і звужувати його тут означало б описати не те, що
        // сервер віддає.
        schema.AdditionalProperties ??= new OpenApiSchema();

        return Task.CompletedTask;
    }

    /// <summary>Чи є тип словником із рядковим ключем.</summary>
    private static bool IsDictionary(Type type)
    {
        if (!typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }


        return type.GetInterfaces()
            .Concat([type])
            .Any(candidate =>
                candidate.IsGenericType
                && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                && candidate.GetGenericArguments()[0] == typeof(string));
    }
}
