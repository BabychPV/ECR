using Ecr.Api.Health;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ecr.Api.Startup;

/// <summary>
/// Додає <c>/health/*</c> у документ OpenAPI (<c>D-137</c>).
/// </summary>
/// <remarks>
/// ⛔ Без цього трансформера здоров'я лишається поза межею, яку описує
/// контракт: <c>/health/*</c> — це middleware, а не контролер, і генератор
/// його не бачить. Саме тому клієнт мусив описувати відповідь руками — і
/// <c>A7-04</c>/<c>A7-36</c> жили там, де їх ніщо не могло спіймати.
///
/// ⚠ Схема береться з <see cref="HealthReportDto"/> — того самого типу, який
/// серіалізує <see cref="HealthResponse"/>. Двох джерел форми більше немає.
///
/// ⚠ Інсталятор (ЕТАП 8) після встановлення читатиме саме <c>/health/ready</c>
/// і вимагатиме зеленого (<c>D-139</c>). Опис у контракті потрібен і йому.
/// </remarks>
public sealed class HealthDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>Шляхи здоров'я та їхній опис.</summary>
    private static readonly (string Path, string Summary, bool Detailed)[] Endpoints =
    [
        ("/health/live", "Процес живий. Жодної перевірки — БД не торкається.", false),
        ("/health/ready", "Готовність: база, планувальник задач, джерела збору.", true),
        ("/health/db", "Подробиці бази: редакція, RCSI, файлові групи, запас партицій.", true),
    ];

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Paths ??= [];
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        document.Components.Schemas["HealthCheckDto"] = CheckSchema();
        document.Components.Schemas["HealthReportDto"] = ReportSchema();

        foreach (var (path, summary, detailed) in Endpoints)
        {
            document.Paths[path] = new OpenApiPathItem
            {
                Operations = new Dictionary<HttpMethod, OpenApiOperation>
                {
                    [HttpMethod.Get] = Operation(summary, detailed),
                },
            };
        }

        return Task.CompletedTask;
    }

    private static OpenApiOperation Operation(string summary, bool detailed)
    {
        var operation = new OpenApiOperation
        {
            Summary = summary,
            Tags = new HashSet<OpenApiTagReference> { new("Health") },
            Responses = new OpenApiResponses(),
        };

        // ⚠ `/health/live` віддає слово `Healthy` рядком, а не звіт: писар до
        // нього не підключений навмисно (жодної перевірки — нема чого писати).
        // Описати його як звіт означало б обіцяти клієнтові те, чого немає.
        operation.Responses["200"] = detailed
            ? new OpenApiResponse
            {
                Description = "Звіт перевірок.",
                Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchemaReference("HealthReportDto"),
                    },
                },
            }
            : new OpenApiResponse { Description = "Процес живий." };

        operation.Responses["503"] = new OpenApiResponse
        {
            Description = "Система не готова: принаймні одна перевірка червона.",
        };

        return operation;
    }

    private static OpenApiSchema ReportSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Звіт перевірок здоров'я.",
        Required = new HashSet<string>(StringComparer.Ordinal) { "status", "totalDurationMs", "checks" },
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["status"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Зведений стан: Healthy, Degraded, Unhealthy.",
            },
            ["totalDurationMs"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number,
                Format = "double",
                Description = "Скільки тривали всі перевірки.",
            },
            ["checks"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Description = "Перевірки; порядок задає контейнер — шукати за іменем.",
                Items = new OpenApiSchemaReference("HealthCheckDto"),
            },
        },
    };

    private static OpenApiSchema CheckSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Одна перевірка у звіті.",
        Required = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "status", "description", "durationMs", "data",
        },
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["name"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Ім'я перевірки: db, jobs, sources.",
            },
            ["status"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Стан цієї перевірки.",
            },
            ["description"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
                Description = "Що саме вона з'ясувала.",
            },
            ["durationMs"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number,
                Format = "double",
                Description = "Тривалість цієї перевірки.",
            },
            ["data"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Подробиці перевірки; текст винятку сюди не потрапляє (ФВ-6.11).",

                // ⚠ Значення довільного типу: `rcsi` — булеве, `edition` —
                // рядок, `partitionsAhead` — число. Без цього рядка генератор
                // опише словник як об'єкт БЕЗ дозволених властивостей — рівно
                // дефект `A6-05`, через який у комірку не можна було покласти
                // жодного значення.
                AdditionalProperties = new OpenApiSchema(),
            },
        },
    };
}
