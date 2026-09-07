// src/Ecr.Domain/Entities/Calculations/MethodologyVersion.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Версія методології — те, що реально рахує. Опублікована **незмінна**;
/// зміна — клон плюс нове вікно дії.
/// </summary>
/// <remarks>
/// Три режими тут визначають числа, і жоден не є технічною дрібницею:
/// <see cref="NumericMode"/> — момент округлення (ФВ-9.9),
/// <see cref="CalendarMode"/> — тривалість періоду (ФВ-16.11),
/// <see cref="TraceLevel"/> — обсяг журналу (ФВ-9.13).
/// Перші два **обов'язкові в diff при публікації**: їх зміна тихо змінює
/// всі результати.
/// <para>
/// ⚠ <c>EffectiveTo</c> немає навмисно — і це не пропуск, а те, що робить
/// перетин вікон **неможливим за побудовою**: версія чинна від свого
/// <see cref="EffectiveFrom"/> до <see cref="EffectiveFrom"/> наступної.
/// Друге поле дозволяло б задати дірку між версіями або накладку, і жодна
/// перевірка при публікації не встигла б за руками, що правлять їх окремо.
/// Схема (`calc.MethodologyVersion`) саме така; це `calc`-частина `Q-027`.
/// </para>
/// </remarks>
public sealed class MethodologyVersion : Entity<int>
{
    private MethodologyVersion() { }

    /// <summary>Створює чернетку версії.</summary>
    /// <param name="methodologyId">Методологія-контейнер.</param>
    /// <param name="version">Номер версії; унікальний у межах методології.</param>
    /// <param name="level">Рівень драбини виразності (ФВ-13.5).</param>
    /// <param name="createdByUserId">Автор. Саме він **не зможе** опублікувати (D-40).</param>
    /// <param name="utcNow">Час створення в UTC.</param>
    public MethodologyVersion(
        int methodologyId,
        string version,
        CalculationLevel level,
        int createdByUserId,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        MethodologyId = methodologyId;
        Version = version;
        Level = level;
        CreatedByUserId = createdByUserId;
        CreatedAt = utcNow;

        // ⚠ Legacy за замовчуванням, а не Strict. Нова версія має рахувати
        // так само, як чинна система, доки хтось свідомо не вирішить інакше:
        // мовчазний перехід на Strict змінив би числа всіх звітів у момент
        // створення версії, яку ще навіть не опублікували (ФВ-9.9).
        NumericMode = NumericMode.Legacy;
        CalendarMode = CalendarMode.Actual;
        TraceLevel = TraceLevel.ErrorsOnly;
        Status = TemplateVersionStatus.Draft;
    }

    public int MethodologyId { get; private set; }

    /// <summary>Номер версії; у схемі — <c>Version</c>.</summary>
    public string Version { get; private set; } = null!;

    /// <summary>Рівень драбини виразності: конфігурація, скрипт, модуль (ФВ-13.5).</summary>
    public CalculationLevel Level { get; private set; }

    /// <summary>Початок дії; <c>null</c> — версія ще не опублікована.</summary>
    public DateOnly? EffectiveFrom { get; private set; }

    public TemplateVersionStatus Status { get; private set; }

    /// <summary>Арифметика: <c>Legacy</c> відтворює числа чинної системи (ФВ-9.9).</summary>
    public NumericMode NumericMode { get; private set; }

    /// <summary>Джерело тривалості періоду (ФВ-16.11). Не зберігається на періоді (D-112).</summary>
    public CalendarMode CalendarMode { get; private set; }

    public TraceLevel TraceLevel { get; private set; }

    /// <summary>Контрольна сума вмісту версії; фіксується при публікації.</summary>
    public byte[]? ContentHash { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Автор версії. Публікувати її він **не може** (D-40).</summary>
    public int CreatedByUserId { get; private set; }

    public int? PublishedByUserId { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public string? ChangeReason { get; private set; }

    /// <summary>Чи опублікована версія.</summary>
    public bool IsPublished => Status == TemplateVersionStatus.Published;

    /// <summary>Задає режими обчислення. Лише для чернетки.</summary>
    /// <param name="numericMode">Арифметичний режим.</param>
    /// <param name="calendarMode">Календарна конвенція.</param>
    /// <param name="traceLevel">Обсяг журналу.</param>
    /// <exception cref="DomainException">Версія вже опублікована.</exception>
    /// <remarks>
    /// ⛔ Обидва перші режими тихо змінюють **усі** числа версії. Дозволити їх
    /// правку після публікації означало б, що поданий звіт можна перерахувати
    /// інакше, не змінивши жодної формули.
    /// </remarks>
    public void SetModes(NumericMode numericMode, CalendarMode calendarMode, TraceLevel traceLevel)
    {
        RequireDraft("режими обчислення");

        NumericMode = numericMode;
        CalendarMode = calendarMode;
        TraceLevel = traceLevel;
    }

    /// <summary>Фіксує контрольну суму вмісту. Лише для чернетки.</summary>
    /// <param name="hash">SHA-256 нормалізованого вмісту версії.</param>
    public void SetContentHash(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        RequireDraft("контрольну суму");

        ContentHash = hash;
    }

    /// <summary>
    /// Публікація. **Чотири очі** (D-40): публікувати власну правку заборонено
    /// системно, а не інструкцією.
    /// </summary>
    /// <param name="publishedByUserId">Хто публікує.</param>
    /// <param name="changeReason">Причина зміни; обов'язкова (ФВ-14.7).</param>
    /// <param name="effectiveFrom">Від якої дати версія чинна.</param>
    /// <param name="testsPassed">Чи зелений останній прогін тестів (ФВ-9.12).</param>
    /// <param name="utcNow">Час публікації в UTC.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0409</c> — публікує автор; <c>ECR-CALC-0422</c> — немає
    /// зеленого тесту або порожня причина.
    /// </exception>
    /// <remarks>
    /// Перевірку перетину вікон робить <see cref="Methodology.PublishVersion"/>:
    /// версія не бачить сусідів і бачити не повинна. Diff **результатів** —
    /// use-case: сутність не має доступу до даних.
    /// </remarks>
    public void Publish(
        int publishedByUserId,
        string changeReason,
        DateOnly effectiveFrom,
        bool testsPassed,
        DateTime utcNow)
    {
        RequireDraft("публікацію");

        // ⛔ Чотири очі. Це не бюрократія: публікація версії методології —
        // найнебезпечніша операція в системі, бо змінює вже подані числа. Той,
        // хто писав формулу, дивиться на неї як автор, і саме тому не бачить
        // у ній того, що побачить інший.
        if (publishedByUserId == CreatedByUserId)
        {
            throw new DomainException(
                "ECR-CALC-0409",
                $"Користувач {publishedByUserId} є автором версії {Version} і не може її опублікувати "
                + "(правило чотирьох очей, D-40).");
        }

        // Причина обов'язкова (ФВ-14.7). Порожній рядок і пробіли — те саме,
        // що її відсутність: за півроку «оновлення» пояснює рівно нічого.
        if (string.IsNullOrWhiteSpace(changeReason))
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Публікація версії {Version} без причини зміни неможлива (ФВ-14.7).");
        }

        // ⛔ Зелений тест обов'язковий (ФВ-9.12). Тести — це дані з очікуваним
        // результатом і допуском; публікація без них означала б, що числа
        // перевіряє той, хто відкриє звіт.
        if (!testsPassed)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Публікація версії {Version} без зеленого тесту заборонена (ФВ-9.12).");
        }

        EffectiveFrom = effectiveFrom;
        ChangeReason = changeReason;
        PublishedByUserId = publishedByUserId;
        PublishedAt = utcNow;
        Status = TemplateVersionStatus.Published;
    }

    /// <summary>Виводить версію з обігу; вона лишається читабельною.</summary>
    /// <remarks>
    /// Опублікована версія не видаляється ніколи: на неї посилаються вже
    /// пораховані результати, і без неї їх неможливо ні пояснити, ні
    /// відтворити.
    /// </remarks>
    public void Deprecate()
    {
        if (!IsPublished)
        {
            throw new DomainException(
                "ECR-CALC-0422", $"Версія {Version} не опублікована: виводити з обігу нема чого.");
        }

        Status = TemplateVersionStatus.Deprecated;
    }

    /// <summary>
    /// Клон цієї версії як **чернетка**: єдиний спосіб змінити опубліковану
    /// (ФВ-9.1, ФВ-13.2).
    /// </summary>
    /// <param name="newVersion">Номер нової версії; унікальний у межах методології.</param>
    /// <param name="createdByUserId">Автор клону. Саме він **не зможе** його опублікувати (D-40).</param>
    /// <param name="utcNow">Час створення в UTC.</param>
    /// <returns>Чернетку без вікна дії і без контрольної суми.</returns>
    /// <remarks>
    /// ⛔ Режими переносяться, а не беруться за замовчуванням. Конструктор
    /// ставить <see cref="Domain.Enums.NumericMode.Legacy"/> і
    /// <see cref="Domain.Enums.CalendarMode.Actual"/> — правильно для НОВОЇ
    /// методології і руйнівно для клону: версія, зроблена «щоб виправити одну
    /// формулу», мовчки перерахувала б усі числа іншою арифметикою і на іншій
    /// тривалості періоду (ФВ-9.9, ФВ-16.11). Ані diff публікації, ані golden
    /// set цього б не назвали причиною — вони показують результат, а не режим.
    /// <para>
    /// ⚠ Дочірні записи (формули, константи, правила) сюди не переносяться:
    /// сутність їх не бачить і бачити не повинна. Копіює їх сховище —
    /// <c>IMethodologyDraftStore</c>, — і воно ж відповідає за те, щоб не
    /// забути жодного набору.
    /// </para>
    /// </remarks>
    public MethodologyVersion CloneAsDraft(string newVersion, int createdByUserId, DateTime utcNow)
    {
        var draft = new MethodologyVersion(MethodologyId, newVersion, Level, createdByUserId, utcNow);
        draft.SetModes(NumericMode, CalendarMode, TraceLevel);

        return draft;
    }

    /// <summary>Заводить формулу в цій версії. Лише для чернетки (ФВ-9.15).</summary>
    /// <param name="code">Код формули — те, на що посилається <c>!Name</c>.</param>
    /// <param name="expression">Вираз діалекту методологій.</param>
    /// <param name="resultType">Що формула повертає: число чи текст.</param>
    /// <param name="outputUnitId">Одиниця результату; <c>null</c> — безрозмірна або текстова.</param>
    /// <returns>Нову формулу, прив'язану до цієї версії.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0409</c> — версія не чернетка; <c>ECR-CALC-0422</c> —
    /// порожній вираз або одиниця на текстовому результаті.
    /// </exception>
    public MethodologyFormula AddFormula(
        EcrCode code, string expression, FormulaResultType resultType, int? outputUnitId)
    {
        RequireDraft("склад формул");

        var formula = new MethodologyFormula(Id, code, expression);
        Apply(formula, expression, resultType, outputUnitId);

        return formula;
    }

    /// <summary>Змінює формулу цієї версії. Лише для чернетки (ФВ-9.15).</summary>
    /// <param name="formula">Формула, яка вже належить цій версії.</param>
    /// <param name="expression">Новий вираз.</param>
    /// <param name="resultType">Що формула повертає.</param>
    /// <param name="outputUnitId">Одиниця результату; <c>null</c> — зняти.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0409</c> — версія не чернетка або формула чужа;
    /// <c>ECR-CALC-0422</c> — порожній вираз або одиниця на текстовому результаті.
    /// </exception>
    /// <remarks>
    /// ⛔ Правка йде **через версію**, а не через саму формулу, і саме тому
    /// <see cref="MethodologyFormula.SetExpression"/> недоступний за межами
    /// домену. Формула не знає, опублікована її версія чи ні; знає це версія —
    /// і поки шлях один, «редагувати можна лише чернетку» тримає домен, а не
    /// пам'ять того, хто пише обробник.
    /// </remarks>
    public void EditFormula(
        MethodologyFormula formula, string expression, FormulaResultType resultType, int? outputUnitId)
    {
        ArgumentNullException.ThrowIfNull(formula);

        RequireDraft("формули");
        RequireOwn(formula);

        Apply(formula, expression, resultType, outputUnitId);
    }

    /// <summary>Дозволяє прибрати формулу з цієї версії. Лише для чернетки.</summary>
    /// <param name="formula">Формула, яка вже належить цій версії.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0409</c> — версія не чернетка або формула чужа.
    /// </exception>
    /// <remarks>
    /// ⚠ Сама сутність нічого не видаляє: колекції формул у версії немає, і
    /// заводити її заради видалення означало б тягнути десятки рядків на кожне
    /// читання версії. Метод **дозволяє** видалення, а виконує його сховище —
    /// але без цього дозволу воно не має права виконати нічого.
    /// </remarks>
    public void RemoveFormula(MethodologyFormula formula)
    {
        ArgumentNullException.ThrowIfNull(formula);

        RequireDraft("склад формул");
        RequireOwn(formula);
    }

    /// <summary>Ставить формулі вираз, тип результату і одиницю.</summary>
    /// <param name="formula">Формула цієї версії.</param>
    /// <param name="expression">Вираз.</param>
    /// <param name="resultType">Тип результату.</param>
    /// <param name="outputUnitId">Одиниця; <c>null</c> — зняти.</param>
    /// <remarks>
    /// ⛔ Порядок кроків несучий. <c>SetOutputUnit</c> відхиляє одиницю на
    /// текстовому результаті, а <c>SetResultType</c> — текст на формулі, де
    /// одиниця вже стоїть. Тому одиниця знімається ПЕРШОЮ: інакше числову
    /// формулу з тоннами неможливо було б перевести в текст узагалі — і не
    /// через заборону, а через порядок викликів.
    /// </remarks>
    private static void Apply(
        MethodologyFormula formula, string expression, FormulaResultType resultType, int? outputUnitId)
    {
        formula.SetExpression(expression);
        formula.ClearOutputUnit();
        formula.SetResultType(resultType);

        if (outputUnitId is { } unit)
        {
            formula.SetOutputUnit(unit);
        }
    }

    /// <summary>Відхиляє формулу, яка належить іншій версії.</summary>
    /// <param name="formula">Формула, яку править виклик.</param>
    /// <remarks>
    /// ⛔ Не формальність. Формула адресується власним ключем, а версія —
    /// своїм; обробник, який прочитав їх окремо, без цієї перевірки правив би
    /// формулу ОПУБЛІКОВАНОЇ версії, тримаючи в руках чернетку — і перевірка
    /// стану версії була б зелена, бо дивилася б не на ту версію.
    /// </remarks>
    private void RequireOwn(MethodologyFormula formula)
    {
        if (formula.MethodologyVersionId != Id)
        {
            throw new DomainException(
                "ECR-CALC-0409",
                $"Формула «{formula.Code}» належить версії {formula.MethodologyVersionId}, "
                + $"а не {Id}: правити її через цю версію не можна.");
        }
    }

    /// <summary>Відхиляє зміну опублікованої версії.</summary>
    private void RequireDraft(string what)
    {
        if (Status != TemplateVersionStatus.Draft)
        {
            throw new DomainException(
                "ECR-CALC-0409",
                $"Версія {Version} у стані {Status}: змінювати {what} не можна. "
                + "Опублікована версія незмінна — зміна це клон і нове вікно дії (ФВ-13.2).");
        }
    }
}
