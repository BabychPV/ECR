using Ecr.Bootstrap.Excel;
using Xunit;

namespace Ecr.Bootstrap.Tests;

/// <summary>
/// Одиниця із заголовка колонки (ФВ-16.12: нерозпізнане — у звіт, не здогадкою).
/// </summary>
/// <remarks>
/// ⛔ Мутаційні точки: прибери з <c>Ambiguous</c> рядок «год» — і
/// <see cref="Год_неоднозначна_і_не_розпізнається"/> червоніє (т/год мовчки
/// стала б т/рік або т/годину, розбіжність 8760 разів); прибери перевірку
/// <c>KnownDerived</c> — червоніє
/// <see cref="Похідна_одиниця_поза_каталогом_не_вигадується"/>.
/// </remarks>
public sealed class UnitRecognizerTests
{
    [Theory]
    [InlineData("Викид, т/рік", "Викид", "t_per_year")]
    [InlineData("Витрата (м³)", "Витрата", "m3")]
    [InlineData("Концентрація [мг/м3]", "Концентрація", "mg_per_m3")]
    [InlineData("Потужність викиду (г/с)", "Потужність викиду", "g_per_s")]
    [InlineData("Питомий викид, кг/т", "Питомий викид", "kg_per_t")]
    [InlineData("Газ (нм3)", "Газ", "Nm3")]
    [InlineData("Газ (н.м3)", "Газ", "Nm3")]
    [InlineData("Газ (Nm3)", "Газ", "Nm3")]
    [InlineData("Газ (стм3)", "Газ", "Sm3")]
    [InlineData("Газ (Sm3)", "Газ", "Sm3")]
    [InlineData("Потік, Nm3/s", "Потік", "Nm3_per_s")]
    [InlineData("Потік, Sm3/h", "Потік", "Sm3_per_h")]
    [InlineData("Енергія (ГДж)", "Енергія", "GJ")]
    [InlineData("Температура (°C)", "Температура", "degC")]
    [InlineData("Маса (t_per_year)", "Маса", "t_per_year")]
    [InlineData("Концентрація, mg/Nm3", "Концентрація", "mg_per_Nm3")]
    [InlineData("Концентрація, mg/Sm3", "Концентрація", "mg_per_Sm3")]
    [InlineData("Витрата, Nm3/day", "Витрата", "Nm3_per_day")]
    [InlineData("Витрата, Sm3/day", "Витрата", "Sm3_per_day")]
    [InlineData("Витрата (нм3/доба)", "Витрата", "Nm3_per_day")]
    [InlineData("Викид, tonnes", "Викид", "t")]
    [InlineData("Маса (tonne)", "Маса", "t")]
    public void Розпізнана_одиниця_відокремлюється_від_заголовка(string raw, string header, string code)
    {
        var result = UnitRecognizer.Parse(raw);

        Assert.Equal(header, result.Header);
        Assert.Equal(code, result.UnitCode);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData("Викид (т/год)")]
    [InlineData("Витрата, т/год")]
    public void Год_неоднозначна_і_не_розпізнається(string raw)
    {
        var result = UnitRecognizer.Parse(raw);

        Assert.Null(result.UnitCode);
        Assert.Equal("т/год", result.UnitText);
        Assert.Contains("год", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Відсоток_неоднозначний_навіть_після_коми()
    {
        var result = UnitRecognizer.Parse("Частка сірки, %");

        Assert.Null(result.UnitCode);
        Assert.Equal("%", result.UnitText);
        Assert.Contains("pct_wt", result.Problem, StringComparison.Ordinal);
        Assert.Equal("Частка сірки", result.Header);
    }

    [Fact]
    public void Похідна_одиниця_поза_каталогом_не_вигадується()
    {
        var result = UnitRecognizer.Parse("Викид (г/хв)");

        Assert.Null(result.UnitCode);
        Assert.Contains("g_per_min", result.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Примітка (для довідки)")]
    [InlineData("Назва, опис")]
    [InlineData("Джерело викиду")]
    public void Текст_що_не_схожий_на_одиницю_лишається_заголовком(string raw)
    {
        var result = UnitRecognizer.Parse(raw);

        Assert.Equal(raw, result.Header);
        Assert.Null(result.UnitText);
        Assert.Null(result.UnitCode);
    }

    /// <summary>
    /// Умови приведення об'єму («20°С., 1 атм.») і мітка сценарію («МТБ», «HMB») —
    /// не одиниці вимірювання. Інструмент їх не вгадує: колонка лишається без
    /// одиниці, позначення йде у звіт, а «°С» у складі рядка не робить з нього
    /// градуси (degC).
    /// </summary>
    [Theory]
    [InlineData("Густина (20°С., 1 атм.)", "20°С., 1 атм.")]
    [InlineData("Густина (0°С., 1 атм.)", "0°С., 1 атм.")]
    [InlineData("Метод (МТБ)", "МТБ")]
    public void Умови_приведення_і_мітки_не_є_одиницями(string raw, string unitText)
    {
        var result = UnitRecognizer.Parse(raw);

        Assert.Equal(unitText, result.UnitText);
        Assert.Null(result.UnitCode);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void Коротке_невідоме_позначення_в_дужках_іде_у_звіт()
    {
        var result = UnitRecognizer.Parse("Об'єм (бар)");

        Assert.Equal("бар", result.UnitText);
        Assert.Null(result.UnitCode);
        Assert.NotNull(result.Problem);
    }
}
