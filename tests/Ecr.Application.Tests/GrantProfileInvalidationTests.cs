// tests/Ecr.Application.Tests/GrantProfileInvalidationTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests;

/// <summary>Скидання кешу профілів після переносу версії: fail-closed, без 500 (ent6 A1).</summary>
public sealed class GrantProfileInvalidationTests
{
    [Fact]
    public void Звичайний_перелік_скидає_кожного_користувача_і_не_чіпає_решту_кешу()
    {
        var cache = Substitute.For<IAccessProfileInvalidator>();

        GrantProfileInvalidation.Run(cache, Substitute.For<ILogger>(), new GrantedUsers([1, 2, 3], Overflow: false));

        cache.Received(1).InvalidateUser(1);
        cache.Received(1).InvalidateUser(2);
        cache.Received(1).InvalidateUser(3);
        cache.DidNotReceive().InvalidateAll();
    }

    [Fact]
    public void Переповнення_переліку_скидає_весь_кеш_а_не_лише_видимих_користувачів()
    {
        var cache = Substitute.For<IAccessProfileInvalidator>();

        GrantProfileInvalidation.Run(cache, Substitute.For<ILogger>(), new GrantedUsers([1, 2], Overflow: true));

        // ⛔ Мутація: прибрати гілку Overflow — решта користувачів лишається зі старим профілем (fail-open).
        cache.Received(1).InvalidateAll();
        cache.DidNotReceive().InvalidateUser(Arg.Any<int>());
    }

    [Fact]
    public void Виняток_посеред_циклу_не_виходить_назовні_і_скидає_весь_кеш()
    {
        var cache = Substitute.For<IAccessProfileInvalidator>();
        cache.When(c => c.InvalidateUser(2)).Throw(new InvalidOperationException("boom"));

        var ex = Record.Exception(() =>
            GrantProfileInvalidation.Run(cache, Substitute.For<ILogger>(), new GrantedUsers([1, 2, 3], Overflow: false)));

        Assert.Null(ex);
        cache.Received(1).InvalidateAll();
    }

    [Fact]
    public void Збій_і_повного_скидання_теж_не_дає_винятку()
    {
        var cache = Substitute.For<IAccessProfileInvalidator>();
        cache.When(c => c.InvalidateUser(Arg.Any<int>())).Throw(new InvalidOperationException("boom"));
        cache.When(c => c.InvalidateAll()).Throw(new InvalidOperationException("boom2"));

        var ex = Record.Exception(() =>
            GrantProfileInvalidation.Run(cache, Substitute.For<ILogger>(), new GrantedUsers([1], Overflow: false)));

        Assert.Null(ex);
        // Мутація: прибрати MarkInvalidationFailed у ClearAll — червоне.
        cache.Received(1).MarkInvalidationFailed();
    }

    [Fact]
    public void Успішне_повне_скидання_не_вмикає_fail_closed()
    {
        var cache = Substitute.For<IAccessProfileInvalidator>();
        cache.When(c => c.InvalidateUser(Arg.Any<int>())).Throw(new InvalidOperationException("boom"));

        GrantProfileInvalidation.Run(cache, Substitute.For<ILogger>(), new GrantedUsers([1], Overflow: false));

        cache.DidNotReceive().MarkInvalidationFailed();
    }
}
