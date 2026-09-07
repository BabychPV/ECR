// tests/Ecr.Expressions.Tests/Binding/ArgumentDeclarationCheckerTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Пастка 2 директиви ПК-1 №05 §7: джерело істини про аргументи — оголошений
/// список, а не текст виразу.
/// </summary>
/// <remarks>
/// ⛔ Половина набору стереже правило, половина — його <b>межу</b>. Правило без
/// межі тут шкідливе: «усе, що розійшлося, — помилка» відхилило б 359
/// аргументів корпусу, оголошених і не вжитих, і зупинило б міграцію на рівному
/// місці.
/// </remarks>
public sealed class ArgumentDeclarationCheckerTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Токен_поза_списком_названий_поіменно()
    {
        // ⛔ Упаде, щойно звірка почне читати аргументи з ТЕКСТУ виразу.
        // Збірка підставляє рівно те, що в списку; `@Duration` не потрапить у
        // вираз узагалі, і формула поверне правдоподібне число.
        var audit = Audit("@Total * @Density / @Duration", "Total;Density");

        Assert.Single(audit.Undeclared);
        Assert.Equal("Duration", audit.Undeclared[0].Name);

        // Позиція названа, щоб редактор підсвітив саме той токен, а не формулу.
        Assert.True(audit.Undeclared[0].Position > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Один_токен_названий_один_раз_скільки_б_разів_не_вжитий()
    {
        // ⚠ Виправляють СПИСОК, а не кожне вживання: другий рядок про той
        // самий токен додає переліку лише довжини.
        var audit = Audit("@Duration + @Duration * 2", "Total");

        Assert.Single(audit.Undeclared);
        Assert.Equal("Duration", audit.Undeclared[0].Name);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Оголошений_і_невжитий_аргумент_помилкою_НЕ_є()
    {
        // ⛔ Найважливіший тест межі: 359 таких аргументів у 186 формулах
        // корпусу. Правило «розійшлося — помилка» відхилило б їх усі.
        var audit = Audit("@Total * 2", "Total;Density");

        Assert.Empty(audit.Undeclared);
        Assert.Equal(["Density"], audit.UnusedDeclared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Контекстний_аргумент_глушить_обидва_боки()
    {
        // Бік другий: оголошений контекстний аргумент — не «переданий даремно».
        var declaredOnly = Audit("@Total * 2", "Total;CalculationDate", "CalculationDate");
        Assert.Empty(declaredOnly.UnusedDeclared);
        Assert.Empty(declaredOnly.Undeclared);

        // ⛔ Бік перший з тієї самої причини: контекстний аргумент зв'язує
        // ЗБІРКА, а не список, тож невизначеним він не буває. Якби глушник
        // діяв лише на попередження, `@CalculationDate` у виразі без згадки в
        // списку став би помилкою публікації — а він визначений завжди.
        var usedOnly = Audit("@Total * @CalculationDate", "Total", "CalculationDate");
        Assert.Empty(usedOnly.Undeclared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порожній_глушник_нічого_не_глушить()
    {
        // ⚠ Межа глушника: він мусить бути ЯВНИМ переліком, а не «усе, що
        // схоже на системне». Без нього `@CalculationDate` — звичайний токен.
        var audit = Audit("@Total * @CalculationDate", "Total");

        Assert.Single(audit.Undeclared);
        Assert.Equal("CalculationDate", audit.Undeclared[0].Name);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Імена_нормалізуються_так_само_як_у_чинній_системі()
    {
        // ⛔ Чинна система замінює крапки підкресленнями і в тексті, і в
        // `FInfo_Arguments` (`Utilities.cs:220`) — заміна симетрична. Сире
        // порівняння оголосило б невизначеними 2534 дотових посилання корпусу
        // лише через те, якою половиною пари вони записані в списку.
        Assert.Empty(Audit("@GCV.HSE400_FG_Makat", "GCV_HSE400_FG_Makat").Undeclared);
        Assert.Empty(Audit("@GCV.HSE400_FG_Makat", "GCV.HSE400_FG_Makat").Undeclared);

        // ⚠ І регістр: імена параметрів чинна система порівнює без його
        // урахування (`Utilities.cs:186`).
        Assert.Empty(Audit("@Total", "total").Undeclared);

        // ⚠ У списку джерела ім'я може стояти і з `@`, і без нього — обидві
        // форми в корпусі законні, і жодна з них не робить аргумент іншим.
        var withSigil = Audit("@Total", "@Total");
        Assert.Empty(withSigil.Undeclared);
        Assert.Empty(withSigil.UnusedDeclared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Токен_у_гілці_умови_видно_так_само_як_на_верхньому_рівні()
    {
        // ⚠ Форма корпусу — вкладені `if`, і токен, схований у гілці, ловиться
        // тим самим правилом. Обхід лише верхнього рівня пропустив би його.
        var audit = Audit(
            "if(@FlareUnitMode = 1, 0, @Total * @Density)", "FlareUnitMode;Total");

        Assert.Single(audit.Undeclared);
        Assert.Equal("Density", audit.Undeclared[0].Name);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Список_розбирається_за_крапкою_з_комою()
    {
        var parsed = ArgumentDeclarationChecker.ParseDeclaration(" Total ;Density;; @Duration ");

        Assert.Equal(["Total", "Density", "@Duration"], parsed);

        // Порожнє джерело — порожній перелік, а не виняток: формула без
        // аргументів законна.
        Assert.Empty(ArgumentDeclarationChecker.ParseDeclaration(null));
        Assert.Empty(ArgumentDeclarationChecker.ParseDeclaration("  "));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порожній_список_робить_невизначеним_кожен_токен()
    {
        // ⛔ «Список порожній» — це твердження «формула не приймає нічого», і
        // воно не те саме, що «списку немає». Плутати їх не можна: у першому
        // випадку кожен токен виразу справді невизначений.
        var audit = Audit("@Total * 2", string.Empty);

        Assert.Single(audit.Undeclared);
        Assert.Equal("Total", audit.Undeclared[0].Name);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static ArgumentAudit Audit(string expression, string declaration, params string[] contextual)
        => ArgumentDeclarationChecker.Audit(
            Root(expression),
            ArgumentDeclarationChecker.ParseDeclaration(declaration),
            contextual);

    /// <summary>
    /// Розбір — СПРАВЖНІЙ: заглушити його означало б перевіряти заглушку.
    /// </summary>
    private static AstNode Root(string expression)
    {
        var parsed = Expr.Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        return parsed.Expression!.Root;
    }
}
