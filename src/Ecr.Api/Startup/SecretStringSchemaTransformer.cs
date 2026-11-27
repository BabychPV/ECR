using Ecr.Application.Common;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ecr.Api.Startup;

/// <summary>
/// Описує <see cref="SecretString"/> як те, чим він є в JSON: рядок лише для запису (P-1126-SRCCRED, R-12).
/// </summary>
/// <remarks>
/// ⚠ Генератор схем не знає, що робить власний конвертер (<see cref="SecretStringJsonConverter"/>), і без цієї гілки
/// віддає «будь-що» — у клієнті це <c>unknown</c>. <c>format: password</c> і <c>writeOnly</c> кажуть клієнтові, що
/// значення лише відправляють і ніколи не отримують.
/// </remarks>
public sealed class SecretStringSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.JsonTypeInfo.Type == typeof(SecretString))
        {
            schema.Type = JsonSchemaType.String | JsonSchemaType.Null;
            schema.Format = "password";
            schema.WriteOnly = true;
        }

        return Task.CompletedTask;
    }
}
