using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Накопичує трейс обчислення згідно з <see cref="TraceLevel"/>.
/// </summary>
/// <remarks>
/// Політика зберігання — «нічого не затирається» (ЗБР-1), тому обсяг керується
/// **тим, що пишемо**, а не строком: записане живе назавжди, непотрібне просто
/// не пишеться (ЗБР-3). Повний трейс кожного кроку кожної методології кожного
/// періоду перевищив би обсяг самих даних.
/// <para>
/// ✎ HSE301 A3b (FEATURE-HSE301-VIEW §7.1): крок ВИДИМОЇ формули пишеться на
/// будь-якому рівні, крім <c>Off</c>, — інакше на типовому <c>ErrorsOnly</c> «Чому
/// це число?» не мало б що показати саме для тих чисел, які методолог виніс на
/// екран. Невидимі кроки — як і раніше, лише на <c>Full</c>.
/// </para>
/// </remarks>
public sealed class TraceRecorder(TraceLevel level)
{
    private readonly List<TraceStep> _steps = [];
    private long? _substance;

    /// <summary>Рівень деталізації.</summary>
    public TraceLevel Level { get; } = level;

    /// <summary>Речовина, до якої належать наступні кроки; <c>null</c> — рівень рядка.</summary>
    /// <param name="substanceEntryId">Речовина прогону.</param>
    /// <remarks>
    /// ⚠ Речовина — частина адреси кроку (FEATURE-HSE301-VIEW §7.1): за нею крок
    /// <c>tons[SO2]</c> знаходить свій результат, а не результат першої речовини рядка.
    /// </remarks>
    public void EnterSubstance(long? substanceEntryId) => _substance = substanceEntryId;

    /// <summary>Чи буде записано успішний крок формули з такою видимістю.</summary>
    /// <param name="visible">Формула видима (<c>IsVisible</c>).</param>
    /// <remarks>
    /// Модуль питає ДО того, як збирати входи кроку: обчислювати їх для кроку, який
    /// не запишеться, означало б платити рушієм за те, що ніхто не прочитає.
    /// </remarks>
    public bool Records(bool visible)
        => Level == TraceLevel.Full || (visible && Level != TraceLevel.Off);

    /// <summary>Чи буде записано крок, що завершився помилкою.</summary>
    public bool RecordsFailures => Level != TraceLevel.Off;

    /// <summary>Записує успішний крок, якщо рівень це передбачає.</summary>
    /// <param name="code">Код кроку — зазвичай код формули або виходу.</param>
    /// <param name="expression">Вираз як його бачив рушій.</param>
    /// <param name="value">Значення кроку.</param>
    /// <param name="visible">Формула видима — крок пишеться й на <c>ErrorsOnly</c>.</param>
    /// <param name="detail">Одиниця й входи кроку для <c>TraceJson</c> v1.</param>
    public void Step(
        string code, string? expression, decimal? value, bool visible = false, TraceDetail? detail = null)
    {
        // ⛔ Off не пише НІЧОГО — навіть у пам'ять. Накопичити «про всяк
        // випадок» і не зберегти означало б платити пам'яттю воркера за те,
        // що ніхто не прочитає; а на річному перерахунку це мільйони кроків.
        if (!Records(visible))
        {
            return;
        }

        _steps.Add(new TraceStep(_steps.Count + 1, code, expression, value, null, Detail: detail, SubstanceEntryId: _substance));
    }

    /// <summary>Записує крок, що завершився помилкою.</summary>
    /// <param name="code">Код кроку.</param>
    /// <param name="expression">Вираз.</param>
    /// <param name="error">Код помилки-значення (<c>#DIV/0</c>, <c>#UNIT</c>).</param>
    /// <param name="detail">Входи кроку — з якими значеннями він не порахувався.</param>
    /// <remarks>
    /// ⚠ Помилковий крок пишеться і на <c>ErrorsOnly</c>, і на <c>Full</c>:
    /// саме заради нього рівень <c>ErrorsOnly</c> і є типовим. Число, яке не
    /// порахувалося, без запису неможливо ні пояснити, ні відтворити.
    /// </remarks>
    public void Failed(string code, string? expression, string error, TraceDetail? detail = null)
    {
        if (!RecordsFailures)
        {
            return;
        }

        _steps.Add(new TraceStep(_steps.Count + 1, code, expression, null, error, Detail: detail, SubstanceEntryId: _substance));
    }

    /// <summary>Записує крок, значення якого замасковане в нуль.</summary>
    /// <param name="code">Код кроку — зазвичай код виходу.</param>
    /// <param name="expression">Вираз.</param>
    /// <param name="value">Значення, яке пішло в результат.</param>
    /// <param name="reason">Причина маскування.</param>
    /// <remarks>
    /// ⛔ Пишеться на БУДЬ-ЯКОМУ рівні, крім <c>Off</c> — навіть на
    /// <c>ErrorsOnly</c>, де кроки зі значеннями не пишуться. Це не виняток,
    /// а весь зміст <c>H-24d-1</c>: число не змінюється, змінюється **тиша**.
    /// Замаскований нуль, якого не видно на типовому рівні трейсу, — це
    /// рівно та тиша, яку ми прибираємо.
    /// </remarks>
    public void Masked(string code, string? expression, decimal? value, MaskedZeroReason reason)
    {
        if (Level == TraceLevel.Off)
        {
            return;
        }

        _steps.Add(new TraceStep(_steps.Count + 1, code, expression, value, null, reason, SubstanceEntryId: _substance));
    }

    /// <summary>Зібрані кроки.</summary>
    public IReadOnlyList<TraceStep> Steps => _steps;
}

/// <summary>Крок трейсу.</summary>
/// <param name="Order">Порядок кроку.</param>
/// <param name="Code">Код кроку.</param>
/// <param name="Expression">Вираз.</param>
/// <param name="Value">Значення.</param>
/// <param name="Error">Код помилки-значення.</param>
/// <param name="Masked">Чому значення стало нулем (<c>H-24d-1</c>).</param>
/// <param name="Detail">Одиниця й входи кроку; <c>null</c> — крок без виразу (маскування виходу).</param>
/// <param name="SubstanceEntryId">Речовина кроку; <c>null</c> — рівень рядка.</param>
public sealed record TraceStep(
    int Order,
    string Code,
    string? Expression,
    decimal? Value,
    string? Error,
    MaskedZeroReason Masked = MaskedZeroReason.None,
    TraceDetail? Detail = null,
    long? SubstanceEntryId = null)
{
    /// <summary>Крок у схемі <c>TraceJson</c> v1 (FEATURE-HSE301-VIEW §7.2).</summary>
    public string ToJson() => TraceJson.Write(this);
}

/// <summary>Те, що крок знає понад значення: одиницю результату й входи.</summary>
/// <param name="UnitCode">Код одиниці результату; <c>null</c> — формула одиниці не оголошує.</param>
/// <param name="Inputs">Входи в порядку першого входження у вираз.</param>
public sealed record TraceDetail(string? UnitCode, IReadOnlyList<TraceInput> Inputs);

/// <summary>Один вхід кроку — значення з того самого контексту, що рахував формулу.</summary>
/// <param name="Kind">Вид входу.</param>
/// <param name="Code">Код; для періоду — властивість (<c>Hours</c>).</param>
/// <param name="Value">Значення текстом (інваріантна культура); <c>null</c> — порожньо.</param>
/// <param name="UnitCode">Код одиниці; <c>null</c> — невідома чи безрозмірна.</param>
/// <param name="Cell">Адреса комірки — лише для <c>@Arg</c>.</param>
/// <param name="SubstanceEntryId">Речовина, для якої резолвилася константа.</param>
/// <param name="PeriodOffset">Зсув періоду; ненульовий лише для <c>[Period-1].X</c>.</param>
public sealed record TraceInput(
    TraceInputKind Kind,
    string Code,
    string? Value,
    string? UnitCode,
    TraceCell? Cell = null,
    long? SubstanceEntryId = null,
    int PeriodOffset = 0);

/// <summary>Комірка, з якої прийшов аргумент.</summary>
/// <param name="TableInstanceId">Таблиця документа.</param>
/// <param name="RowKey">Ключ рядка; <c>null</c> — рівень таблиці.</param>
/// <param name="Column">Код колонки — він же ім'я аргументу.</param>
public sealed record TraceCell(long TableInstanceId, string? RowKey, string Column);

/// <summary>Запис кроку в схемі <c>TraceJson</c> v1 (FEATURE-HSE301-VIEW §7.2).</summary>
/// <remarks>
/// ⚠ Числа — РЯДКАМИ в інваріантній культурі, а не JSON-числами: <c>decimal(34,16)</c>
/// не вміщається в <c>double</c>, яким JSON-число читає більшість клієнтів, і пояснення
/// показало б інше число, ніж лежить у результаті.
/// </remarks>
public static class TraceJson
{
    /// <summary>Версія схеми.</summary>
    public const int Version = 1;

    private static readonly JsonWriterOptions Options = new()
    {
        // Вираз містить лапки й апострофи (`'kg'`): стандартний кодувальник перетворив би
        // їх на `'`, і вираз у БД перестав би читатися очима. JSON лишається
        // коректним — екрануються лише обов'язкові символи.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Серіалізує крок.</summary>
    /// <param name="step">Крок трейсу.</param>
    /// <returns>JSON схеми v1.</returns>
    public static string Write(TraceStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Options))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteString("expr", step.Expression);
            json.WriteString("result", Format(step.Value));
            json.WriteString("unit", step.Detail?.UnitCode);
            json.WriteString("masked", step.Masked == MaskedZeroReason.None ? null : step.Masked.ToString());
            json.WriteString("error", step.Error);

            json.WriteStartArray("inputs");
            foreach (var input in step.Detail?.Inputs ?? [])
            {
                WriteInput(json, input);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Число в інваріантній культурі.</summary>
    /// <param name="value">Число; <c>null</c> — немає.</param>
    public static string? Format(decimal? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static void WriteInput(Utf8JsonWriter json, TraceInput input)
    {
        json.WriteStartObject();
        json.WriteString("kind", input.Kind switch
        {
            TraceInputKind.Argument => "arg",
            TraceInputKind.Constant => "const",
            TraceInputKind.Formula => "formula",
            _ => "period",
        });

        // Для періоду схема називає поле `prop`: це властивість календаря, а не код.
        json.WriteString(input.Kind == TraceInputKind.Period ? "prop" : "code", input.Code);
        json.WriteString("value", input.Value);

        if (input.Kind != TraceInputKind.Period)
        {
            json.WriteString("unit", input.UnitCode);
        }

        if (input.PeriodOffset != 0)
        {
            json.WriteNumber("offset", input.PeriodOffset);
        }

        if (input.SubstanceEntryId is { } substance)
        {
            json.WriteNumber("substance", substance);
        }

        if (input.Cell is { } cell)
        {
            json.WriteStartObject("cell");
            json.WriteNumber("tableInstanceId", cell.TableInstanceId);
            json.WriteString("row", cell.RowKey);
            json.WriteString("column", cell.Column);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }
}
