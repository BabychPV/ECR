// src/Ecr.Api/Observability/CompactJsonLogFormatter.cs

using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace Ecr.Api.Observability;

/// <summary>
/// Запис журналу одним рядком JSON у форматі CLEF (<c>U20</c>).
/// </summary>
/// <remarks>
/// Поля: <c>@t</c> — час UTC (ISO 8601), <c>@l</c> — рівень, <c>@mt</c> — шаблон,
/// <c>@m</c> — готове повідомлення, <c>@x</c> — виняток зі стеком; далі ВСІ
/// властивості запису верхнім рівнем: <c>CorrelationId</c>, <c>UserId</c>,
/// <c>MachineName</c>, <c>SourceContext</c>, <c>Code</c> (код помилки з
/// <c>ExceptionHandlingMiddleware</c>) тощо.
///
/// ⚠ Власний форматер, а не <c>CompactJsonFormatter</c>: той живе в окремому
/// пакеті <c>Serilog.Formatting.Compact</c>, а нової залежності заради ~40 рядків
/// не заводимо. Екранування рядків і значень — штатний <see cref="JsonValueFormatter"/>
/// самого Serilog, тобто те саме, що й у пакеті.
///
/// ⚠ <c>@l</c> пишеться ЗАВЖДИ, зокрема для <c>Information</c> (канонічний CLEF
/// його там опускає): фільтр SIEM «рівень = …» не мусить знати цієї угоди.
/// </remarks>
public sealed class CompactJsonLogFormatter : ITextFormatter
{
    private static readonly JsonValueFormatter Values = new(typeTagName: "$type");

    /// <inheritdoc />
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write("{\"@t\":\"");
        output.Write(logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        output.Write("\",\"@l\":\"");
        output.Write(logEvent.Level.ToString());
        output.Write("\",\"@mt\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);
        output.Write(",\"@m\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.RenderMessage(CultureInfo.InvariantCulture), output);

        if (logEvent.Exception is not null)
        {
            output.Write(",\"@x\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
        }

        foreach (var (name, value) in logEvent.Properties)
        {
            // ⚠ Префікс `@` зарезервований CLEF: властивість з таким іменем
            // подвоює його, щоб не підмінити службове поле.
            var key = name.StartsWith('@') ? "@" + name : name;

            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(key, output);
            output.Write(':');
            Values.Format(value, output);
        }

        output.Write('}');
        output.WriteLine();
    }
}
