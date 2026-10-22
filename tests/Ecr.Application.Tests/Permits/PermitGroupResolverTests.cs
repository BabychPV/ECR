// tests/Ecr.Application.Tests/Permits/PermitGroupResolverTests.cs
using Ecr.Application.Permits.Groups;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>Знімок груп у памʼяті: належність, розгортання, неактивна група, ревізія та інвалідація (Permits, PRMG).</summary>
public sealed class PermitGroupResolverTests
{
    private const int Water = 900;
    private const int Air = 901;

    private static (PermitGroupResolver Resolver, InMemoryPermitGroupStore Store) Build(PermitGroupSnapshotCache? cache = null)
    {
        var store = new InMemoryPermitGroupStore { Revision = 1 };
        store.Items.Add(new PermitGroupSnapshotItem(Water, true, [10, 11, 12]));
        store.Items.Add(new PermitGroupSnapshotItem(Air, false, [20]));

        return (new PermitGroupResolver(store, cache ?? new PermitGroupSnapshotCache()), store);
    }

    private static AccessProfile Profile(params string[] denies) => new()
    {
        CacheKey = "k",
        UserId = 1,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(),
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(denies),
        RoleIds = new HashSet<int>(),
    };

    [Fact]
    public async Task Запис_реєстру_зі_складу_активної_групи_приймається_а_поза_складом_ні()
    {
        var (resolver, _) = Build();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.True(resolver.Accepts(Water, 10));
        Assert.True(resolver.Accepts(Water, 12));
        Assert.False(resolver.Accepts(Water, 20));
        Assert.False(resolver.Accepts(Water, Water));
    }

    [Fact]
    public async Task Ціль_що_не_група_і_невідома_ціль_нічого_не_приймають_а_розгортаються_в_себе()
    {
        var (resolver, _) = Build();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.False(resolver.Accepts(18, 18));
        Assert.False(resolver.Accepts(18, 10));
        Assert.False(resolver.IsGroup(18));
        Assert.Empty(resolver.MemberRegistryIds(18));
        Assert.Equal([18], resolver.ExpandRegistryIds(18));
    }

    [Fact]
    public async Task Група_розгортається_у_склад_у_порядку_складу()
    {
        var (resolver, _) = Build();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.True(resolver.IsGroup(Water));
        Assert.Equal([10, 11, 12], resolver.MemberRegistryIds(Water));
        Assert.Equal([10, 11, 12], resolver.ExpandRegistryIds(Water));
    }

    [Fact]
    public async Task Неактивна_група_лишається_групою_і_розгортається_але_нових_значень_не_приймає()
    {
        var (resolver, _) = Build();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.True(resolver.IsGroup(Air));
        Assert.Equal([20], resolver.ExpandRegistryIds(Air));
        Assert.False(resolver.Accepts(Air, 20));
    }

    [Fact]
    public async Task AcceptsFor_відмовляє_для_реєстру_під_забороною_так_само_як_для_чужого()
    {
        var (resolver, _) = Build();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.True(resolver.AcceptsFor(Profile(), Water, 11));
        Assert.False(resolver.AcceptsFor(Profile("Registry:11"), Water, 11));
        Assert.True(resolver.AcceptsFor(Profile("Registry:11"), Water, 10));
    }

    [Fact]
    public async Task Знімок_не_перечитується_поки_свіжий_і_перечитується_після_зміни_ревізії()
    {
        var (resolver, store) = Build();
        await resolver.EnsureFreshAsync(CancellationToken.None);
        await resolver.EnsureFreshAsync(CancellationToken.None);
        Assert.Equal(1, store.RevisionReads);
        Assert.Equal(1, store.SnapshotLoads);

        // Склад змінили, ревізія піднялась, але знімок свіжий - зміни ще не видно.
        store.Items[0] = new PermitGroupSnapshotItem(Water, true, [10]);
        store.Revision = 2;
        await resolver.EnsureFreshAsync(CancellationToken.None);
        Assert.True(resolver.Accepts(Water, 12));
    }

    [Fact]
    public async Task Invalidate_змушує_перевірити_ревізію_одразу_і_нова_ревізія_підміняє_склад()
    {
        var cache = new PermitGroupSnapshotCache();
        var (resolver, store) = Build(cache);
        await resolver.EnsureFreshAsync(CancellationToken.None);

        store.Items[0] = new PermitGroupSnapshotItem(Water, true, [10]);
        store.Revision = 2;
        cache.Invalidate();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.True(resolver.Accepts(Water, 10));
        Assert.False(resolver.Accepts(Water, 12));
        Assert.Equal(2, store.SnapshotLoads);
    }

    [Fact]
    public async Task Інвалідація_без_зміни_ревізії_не_перечитує_знімок()
    {
        var cache = new PermitGroupSnapshotCache();
        var (resolver, store) = Build(cache);
        await resolver.EnsureFreshAsync(CancellationToken.None);

        cache.Invalidate();
        await resolver.EnsureFreshAsync(CancellationToken.None);

        Assert.Equal(2, store.RevisionReads);
        Assert.Equal(1, store.SnapshotLoads);
    }

    [Fact]
    public void До_першого_EnsureFresh_жодна_ціль_не_група()
    {
        var (resolver, _) = Build();

        Assert.False(resolver.IsGroup(Water));
        Assert.False(resolver.Accepts(Water, 10));
    }

    [Fact]
    public void Фейк_що_кидає_ловить_звернення_на_гарячому_шляху()
    {
        var fake = new FakePermitGroupResolver { ThrowOnUse = true };

        Assert.Throws<InvalidOperationException>(() => fake.Accepts(1, 2));
        Assert.Equal(1, fake.Calls);
    }
}
