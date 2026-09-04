using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// <see cref="RowKey"/> — не <see cref="EcrCode"/>: він допускає цифрові ключі
/// (<c>"7001001"</c>) і GUID, бо стоїть в окремій позиції граматики.
/// </summary>
public sealed class RowKeyTests
{
    [Theory]
    [InlineData("7001001")]
    [InlineData("C009")]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90")]
    [InlineData("row-1.2")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Допустимий_ключ_приймається(string key)
    {
        Assert.Equal(key, RowKey.Create(key).Value);

        // Той самий "7001001" для EcrCode недопустимий — саме тому це різні типи,
        // а не один із двома режимами перевірки.
        if (char.IsDigit(key[0]))
        {
            Assert.False(EcrCode.TryCreate(key, out _));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_динамічного_рядка_це_GUID_у_форматі_N_без_дефісів()
    {
        var key = RowKey.NewDynamic();

        Assert.Equal(32, key.Value.Length);
        Assert.DoesNotContain('-', key.Value);
        Assert.All(key.Value, c => Assert.True(Uri.IsHexDigit(c), $"Символ '{c}' не є шістнадцятковим."));

        // Формат "N" має лишатися валідним RowKey — інакше динамічний рядок
        // неможливо було б ані записати, ані згадати у виразі.
        Assert.True(RowKey.TryCreate(key.Value, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Два_виклики_NewDynamic_дають_різні_ключі()
    {
        var first = RowKey.NewDynamic();
        var second = RowKey.NewDynamic();

        Assert.NotEqual(first, second);
    }
}
