using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;

namespace Ecr.Calculations;

/// <summary>
/// Кроки розрахунку, спільні для модулів: запис значення в результати, завершення кроку обчислення
/// (помилка, маскування <c>Legacy</c>, трейс), перетворення аргументу у значення, календарний контекст.
/// </summary>
/// <remarks>
/// ⛔ Це не нова поведінка, а винесена зі <c>GenericCalculationModule</c>: числа двох рушіїв
/// (формули й скрипт) збігаються лише доти, доки ці кроки існують в одному екземплярі коду. Усе, що
/// потрібне, передається параметрами — стану в класі немає, тож його можна кликати з будь-якого модуля
/// збірки.
/// </remarks>
internal static class CalculationSteps
{
    /// <summary>Пише одне значення — вихід або проміжне — у результати рядка.</summary>
    /// <param name="values">Куди пишеться.</param>
    /// <param name="version">Версія рядка — режим арифметики й ідентифікатор.</param>
    /// <param name="binding">Контекст прив'язки — масштаби колонок-приймачів.</param>
    /// <param name="numeric">Числова політика версії.</param>
    /// <param name="trace">Трейс рядка.</param>
    /// <param name="code">Код виходу або видимої формули.</param>
    /// <param name="value">Значення з контексту обчислення.</param>
    /// <param name="unitId">Одиниця результату.</param>
    /// <param name="substance">Речовина; <c>null</c> — значення рівня рядка.</param>
    /// <param name="kind">Вихід чи проміжне.</param>
    internal static void EmitValue(
        List<CalculationOutputValue> values,
        MethodologyDescriptor version,
        CalculationBindingContext binding,
        NumericPolicy numeric,
        TraceRecorder trace,
        string code,
        ExpressionValue value,
        int unitId,
        MethodologySubstance? substance,
        CalculationResultKind kind)
    {
        var masked = MaskedZero.Prepare(value, version.NumericMode);

        if (masked.Value is not { } number)
        {
            // ⚠ У `Strict` сюди потрапляє і замаскований випадок:
            // значення там `null`, а не нуль (`ФВ-9.14`). Причина
            // однаково має бути названа, тому запис іде окремим
            // кроком, а не загальним «#NULL».
            if (masked.Reason != MaskedZeroReason.None)
            {
                trace.Masked(code, null, null, masked.Reason);
                return;
            }

            // ⚠ Проміжне без числа — лише пропуск: крок формули з причиною вже
            // записав `Evaluate`, а другий такий самий запис нічого б не додав.
            if (kind == CalculationResultKind.Intermediate)
            {
                return;
            }

            // Вихід без числа не пишеться: нуль тут виглядав би як
            // порахований результат. Причина вже в трейсі.
            //
            // ⚠ Аудит A2: ЧИСЛО без `decimal`-подання — це `Legacy`
            // `double` за межею ≈7.9e28 (`Pow(10, 30)`). Колонка
            // результату його не вмістить, а «#NULL» збрехав би, що
            // значення не було; тому `#VALUE`, як у `Strict`.
            var error = value.ErrorCode
                ?? (value.Type == Expressions.Ast.ExpressionValueType.Number
                    ? Expressions.ExpressionErrors.BadValue
                    : "#NULL");

            // B66: вихід без результату через помилку — причина нуля/неповного результату (StepError рядка).
            trace.OutputFailed(code, error);
            trace.Failed(code, null, error);
            return;
        }

        // ⛔ Замаскований нуль пишеться в трейс ЗАВЖДИ, коли він
        // стався. Число при цьому те саме, що дала б чинна система, —
        // саме тому знайти ці випадки можна лише за записом, і саме
        // вони обіцяні як найцінніший побічний результат міграції.
        if (masked.Reason != MaskedZeroReason.None)
        {
            trace.Masked(code, null, number, masked.Reason);
        }

        // ⛔ Скільки знаків несе результат — КОНФІГУРАЦІЯ КОЛОНКИ, у
        // яку він потрапляє (рішення людини 2026-09-20), а не спільна
        // константа рушія. Колонка мовчить — беруться всі шістнадцять
        // (`NumericPolicy.DefaultOutputScale`), бо саме стільки несе
        // конвеєр чинної системи; обрізати до шести «на всяк випадок»
        // означало б змінити число у звіті там, де ніхто про це не
        // просив. Проміжне, прив'язане до колонки (`M_t` у сітці), бере
        // масштаб тієї самої колонки тим самим кодом.
        var scale = binding.OutputScales.TryGetValue(code, out var declared)
            ? declared
            : null;

        values.Add(new CalculationOutputValue(
            version.MethodologyVersionId,
            substance is null ? null : checked((int)substance.SubstanceEntryId),
            code,
            numeric.RoundOutput(number, scale),
            unitId,
            kind));
    }


    /// <summary>
    /// Завершує крок обчислення: помилка → <c>trace.Failed</c>; у <c>Legacy</c> NaN/±∞ → 0 з записом
    /// <c>trace.Masked</c>; далі <c>trace.Step</c>.
    /// </summary>
    /// <typeparam name="TState">Стан для ліниво обчислюваних деталей кроку (без замикання на гарячому шляху).</typeparam>
    /// <param name="code">Код кроку — формули, змінної або виходу.</param>
    /// <param name="expression">Текст виразу для трейсу.</param>
    /// <param name="result">Результат обчислення виразу.</param>
    /// <param name="numeric">Числова політика версії.</param>
    /// <param name="trace">Трейс рядка.</param>
    /// <param name="visible">Крок видимий — пишеться й на <c>ErrorsOnly</c>.</param>
    /// <param name="source">Бібліотека, чия це формула (HSE301 L); <c>null</c> — своя версія.</param>
    /// <param name="state">Стан для <paramref name="describe"/>.</param>
    /// <param name="describe">Деталі кроку (одиниця, входи); викликається лише коли крок запишеться.</param>
    /// <returns>Результат після маскування: помилка лишається помилкою, нечисло <c>Legacy</c> стає нулем.</returns>
    /// <remarks>
    /// ⚠ Винесено з <c>GenericCalculationModule.Evaluate</c> без зміни поведінки: другий модуль (скрипт) мусить
    /// маскувати й писати кроки ТАК САМО, а не «майже так».
    /// </remarks>
    internal static ExpressionValue FinishStep<TState>(
        string code,
        string? expression,
        ExpressionValue result,
        NumericPolicy numeric,
        TraceRecorder trace,
        bool visible,
        TraceSource? source,
        TState state,
        Func<TState, TraceDetail?> describe)
    {
        if (result.IsError)
        {
            trace.Failed(
                code,
                expression,
                result.ErrorCode!,
                trace.RecordsFailures ? describe(state) : null,
                source);
            return result;
        }

        // ✎ D-427 (Q-088): у `Legacy` результат КОЖНОЇ формули з NaN/±∞ стає 0 ДО того, як наступна формула
        // прочитає його через `!Name` — як чинна система (`Utilities.cs:58-71`: перевірка на кожній формулі).
        // Причина — крок трейсу `MaskedZeroReason`. `Strict` не зачеплено: там #DIV/0 — помилка-значення.
        if (numeric.Mode == NumericMode.Legacy && MaskedZero.ReasonFor(result) is var maskReason
            && maskReason != MaskedZeroReason.None)
        {
            trace.Masked(code, expression, 0m, maskReason);
            result = ExpressionValue.LegacyNumber(0d);
        }

        // ⚠ Входи збираються лише для кроку, який запишеться: на `ErrorsOnly` невидима
        // формула не платить за обхід і повторне читання посилань нічим.
        var detail = trace.Records(visible) ? describe(state) : null;

        if (result.AsNumber() is not { } number)
        {
            trace.Step(code, expression, null, visible, detail, source);
            return result;
        }

        // ⛔ Проміжний крок НЕ округлюється (директива №05 §6): у вихідних текстах чинної системи
        // немає жодного `Math.Round` між формулами, число йде рядком `G17`, який для `double` круговий.
        // Округлення — лише всередині `Round()` формули і при збереженні в колонку (`EmitValue`).
        trace.Step(code, expression, number, visible, detail, source);

        return result;
    }

    /// <summary>Календарний контекст періоду за режимом версії.</summary>
    /// <param name="periods">Межі періодів.</param>
    /// <param name="calendar">Календарний контекст.</param>
    /// <param name="version">Версія — режим календаря.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Ключ періоду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Межі беруться з <c>doc.Period</c>, а НЕ виводяться з
    /// <c>PeriodKey</c>. Вивести їх із ключа неможливо:
    /// <c>PeriodKey = Year*100 + Sequence</c> (R-A6), і для квартального
    /// проєкту <c>202602</c> — це другий КВАРТАЛ. Тлумачити <c>Sequence</c> як
    /// місяць означало б поділити на 28 днів замість 91: усі <c>г/с</c> у
    /// звіті стали б утричі більшими, і жодна перевірка цього не побачила б —
    /// число залишається правдоподібним (ФВ-16.11a, D-112).
    /// </remarks>
    internal static async Task<Expressions.PeriodContext> PeriodAsync(
        IPeriodStore periods,
        CalendarContext calendar,
        MethodologyDescriptor version,
        long documentId,
        PeriodKey periodKey,
        CancellationToken ct)
    {
        var bounds = await periods
            .FindPeriodBoundsAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false)
            ?? throw new Domain.Abstractions.DomainException(
                "ECR-PRD-0404",
                $"Періоду {periodKey.Value} для документа {documentId} не існує: "
                + "тривалість обчислити нема з чого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0404.periodForDocument",
                    ["periodKey"] = periodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Sequence — порядковий номер періоду в році, і саме він, а не місяць:
        // у квартальному проєкті їх чотири (R-A6, D-108).
        var sequence = (byte)(periodKey.Value % 100);

        return calendar.Build(
            bounds.PeriodStart, bounds.PeriodEnd, version.CalendarMode,
            periodKey.Value / 100, sequence);
    }


    /// <summary>Аргумент розрахунку як значення виразу.</summary>
    /// <param name="argument">Аргумент рядка.</param>
    /// <param name="entryRef">
    /// Віддавати <c>EntryRef</c>: <c>true</c> — формули <c>Strict</c> і правило категорії будь-якого режиму;
    /// <c>false</c> — формули <c>Legacy</c> (побітно як до RT-23a).
    /// </param>
    /// <param name="legacyNumericText">
    /// Текст, що розбирається як <c>double</c> (інваріантна культура), подавати числом —
    /// <c>true</c> лише для формул <c>Legacy</c>.
    /// </param>
    /// <remarks>
    /// ⛔ C1-05 (аудит 2026-10-09c). Чинна збірка подає параметром <c>double</c> будь-яке
    /// число і будь-який рядок, що розібрався <c>double.TryParse(InvariantCulture)</c>
    /// (<c>Utilities.cs:188-215</c>, <c>NCalcLegacyTests</c>). Без цього текстова колонка
    /// зі значенням <c>"1"</c> давала в <c>Legacy</c> <c>@X = 1</c> і <c>in(@X, 1, 2)</c>
    /// мовчки FALSE, і <c>if</c> ішов в іншу гілку. <c>Strict</c> і правило категорії
    /// лишаються як є: текст — це текст.
    ///
    /// ✎ RT-23a: <c>EntryRef</c> (<see cref="CalculationArgument.EntryId"/>) у рантаймі —
    /// число, id запису (§5.3), той самий вибір, що вже діє для <c>Lookup</c>-комірок у
    /// шаблонах. ✎ L2-3: <see cref="CalculationInputBuilder"/> заповнює його завжди, а тут
    /// його ігнорують формули <c>Legacy</c> (<c>D-161</c>); правило категорії читає id в обох режимах.
    /// </remarks>
    internal static ExpressionValue ToValue(CalculationArgument argument, bool entryRef, bool legacyNumericText)
        => entryRef && argument.EntryId is { } entryId
            ? ExpressionValue.Number(entryId)
            : argument.Value is { } number
                ? ExpressionValue.Number(number)
                : argument.ValueString is { } text
                    ? legacyNumericText
                      && double.TryParse(
                          text,
                          System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture,
                          out var parsed)
                        ? ExpressionValue.LegacyNumber(parsed)
                        : ExpressionValue.Text(text)
                    : ExpressionValue.Null;
}
