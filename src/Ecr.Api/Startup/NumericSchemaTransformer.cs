using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ecr.Api.Startup;

/// <summary>
/// Прибирає з числових схем альтернативу «або рядок».
/// </summary>
/// <remarks>
/// ⚠ <c>JsonSerializerDefaults.Web</c> вмикає
/// <c>JsonNumberHandling.AllowReadingFromString</c>, і генератор OpenAPI чесно
/// описує це як <c>"type": ["integer", "string"]</c>. Але терпимість стосується
/// лише <b>читання</b>: у відповідь сервер завжди пише число.
/// <para>
/// ⛔ Наслідок в описі був важчим за причину: кожне ціле в згенерованому
/// клієнті ставало <c>string | number</c>. Тобто <c>tableInstanceId</c>,
/// <c>periodKey</c> і <c>sheetDefId</c> не можна було передати нікуди без
/// приведення типів — типізований клієнт переставав бути корисним рівно там,
/// де він потрібен (`A7-06`).
/// </para>
/// <para>
/// ⚠ Схема стає <b>суворішою</b> за фактичну терпимість сервера, і це
/// правильний бік помилки: клієнт, згенерований із неї, надсилатиме числа
/// числами, а рядок сервер усе одно прийме.
/// </para>
/// </remarks>
public sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (schema.Type is not { } type || !type.HasFlag(JsonSchemaType.String))
        {
            return Task.CompletedTask;
        }

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            // `Null` зберігається: nullable-число описується саме так, і
            // прибрати його означало б оголосити обов'язковим поле, яке
            // сервер віддає порожнім.
            schema.Type = JsonSchemaType.Integer | (type & JsonSchemaType.Null);
            schema.Pattern = null;
        }
        else if (type.HasFlag(JsonSchemaType.Number))
        {
            schema.Type = JsonSchemaType.Number | (type & JsonSchemaType.Null);
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}
