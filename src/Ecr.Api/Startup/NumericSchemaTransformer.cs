using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ecr.Api.Startup;

/// <summary>
/// Приводить числові схеми до того, що сервер насправді пише: цілі — числом,
/// <c>decimal</c> — рядком.
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
        ArgumentNullException.ThrowIfNull(context);

        // ⛔ `decimal` їде РЯДКОМ (`DecimalAsStringJsonConverter`), і схема
        // мусить це казати. Без цієї гілки опис лишається без `type` взагалі:
        // генератор схем не знає, що робить власний конвертер, і віддає
        // «будь-що» — у клієнті це `unknown`, тобто типізований контракт
        // мовчки зникає рівно на вимірюваних величинах.
        if (IsDecimal(context.JsonTypeInfo.Type))
        {
            schema.Type = context.JsonTypeInfo.Type == typeof(decimal?)
                ? JsonSchemaType.String | JsonSchemaType.Null
                : JsonSchemaType.String;

            // `format` описового призначення: клієнт бачить, що рядок несе
            // число, а не текст. `double` тут був би прямою неправдою.
            schema.Format = "decimal";
            schema.Pattern = null;

            return Task.CompletedTask;
        }

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

    /// <summary>Чи описує схема <c>decimal</c> або <c>decimal?</c>.</summary>
    /// <param name="type">Тип, з якого зроблено схему.</param>
    private static bool IsDecimal(Type type)
        => type == typeof(decimal) || type == typeof(decimal?);
}
