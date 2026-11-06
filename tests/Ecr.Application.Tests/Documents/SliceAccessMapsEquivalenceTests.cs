// tests/Ecr.Application.Tests/Documents/SliceAccessMapsEquivalenceTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ⚡ Перф зрізу (HSE401, ~200k рішень на запит): прискорені згортки рішень доступу в мапи відповіді —
/// <see cref="SlicePermissionMaps"/> (<c>cellPermissions</c>/<c>cellConfirmations</c>) і
/// <see cref="CellStyleAccessPolicy"/> (<c>styleAccess</c>) — дають РІВНО те саме, що попередні реалізації.
/// </summary>
/// <remarks>
/// ⛔ Зона безпеки (S6, причини відмов, підтвердження): еталон — дослівні копії старого коду нижче
/// (<see cref="OldPermissionLoop"/>, <see cref="OldStyleBuild"/>), порівняння — ВКЛЮЧНО з порядком ключів
/// (порядок серіалізації JSON). Набори строкаті й детерміновані (фіксовані зерна): невидимі колонки, рядки без ключа,
/// обчислювані колонки з <c>CalculatedCell</c>, підтвердження з <c>null</c>-деталлю, повністю заборонені рядки й
/// колонки, «усе заборонено» з нічиєю причин і обрізання за <see cref="CellStyleAccessPolicy.MaxKeys"/>.
/// </remarks>
public sealed class SliceAccessMapsEquivalenceTests
{
    private const int SeedCount = 60;

    private static readonly PeriodKey Period = new(202601);

    private static readonly EditDenyReason[] Reasons =
    [
        EditDenyReason.CalculatedCell, EditDenyReason.ColumnReadOnly, EditDenyReason.RowReadOnly,
        EditDenyReason.NoGrant, EditDenyReason.DocumentSubmitted, EditDenyReason.OutsidePermitWindow,
    ];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (var seed = 0; seed < SeedCount; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Мапи_причин_і_підтверджень_тотожні_старому_циклу(int seed)
    {
        var world = World.Create(seed);

        var (permissions, confirmations) = SlicePermissionMaps.Build(
            world.Decisions, world.RowKeys, world.CanRead, world.AllCodes, world.Computed);
        var (oldPermissions, oldConfirmations) = OldPermissionLoop(
            world.Decisions, world.RowKeys, world.CanRead, world.AllCodes, world.Computed);

        Assert.Equal(oldPermissions.ToList(), permissions.ToList());
        Assert.Equal(oldConfirmations.ToList(), confirmations.ToList());
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Згортка_фарбування_тотожна_старій(int seed)
    {
        var world = World.Create(seed);

        var actual = CellStyleAccessPolicy.Build(world.Decisions, world.RowKeys, world.VisibleCodes);
        var expected = OldStyleBuild(world.Decisions, world.RowKeys, world.VisibleCodes);

        AssertSame(expected, actual);
    }

    [Fact]
    public void Набори_справді_проходять_крізь_усі_гілки()
    {
        // ⛔ Проти хибнозеленого: тотожність на порожніх чи одноманітних наборах нічого не доводить.
        var styleShapes = new HashSet<string>(StringComparer.Ordinal);
        var permissionsSeen = false;
        var confirmationsSeen = false;
        var nullDetailSeen = false;
        var calculatedSkipped = false;
        var invisibleDenied = false;

        for (var seed = 0; seed < SeedCount; seed++)
        {
            var world = World.Create(seed);
            var style = OldStyleBuild(world.Decisions, world.RowKeys, world.VisibleCodes);

            styleShapes.Add(style switch
            {
                { CanStyle: false, DeniedReason: null } => "empty",
                { CanStyle: false } => "all-denied",
                { Truncated: true } => "truncated",
                { StyleDeniedRowKeys.Count: > 0, StyleDeniedColumnCodes.Count: > 0, StyleDeniedCells.Count: > 0 } => "rows+cols+cells",
                _ => "partial",
            });

            var (permissions, confirmations) = OldPermissionLoop(
                world.Decisions, world.RowKeys, world.CanRead, world.AllCodes, world.Computed);
            permissionsSeen |= permissions.Count > 0;
            confirmationsSeen |= confirmations.Count > 0;
            nullDetailSeen |= confirmations.Values.Any(v => v.Length == 0);

            foreach (var (address, decision) in world.Decisions)
            {
                calculatedSkipped |= !decision.IsAllowed && decision.Reason == EditDenyReason.CalculatedCell
                                     && world.Computed.Contains(address.ColumnDefId)
                                     && world.CanRead(address.ColumnDefId)
                                     && world.RowKeys.ContainsKey(address.TableRowId);
                invisibleDenied |= !decision.IsAllowed && !world.CanRead(address.ColumnDefId);
            }
        }

        Assert.Equal(["all-denied", "empty", "partial", "rows+cols+cells", "truncated"], styleShapes.Order(StringComparer.Ordinal));
        Assert.True(permissionsSeen && confirmationsSeen && nullDetailSeen && calculatedSkipped && invisibleDenied);
    }

    private static void AssertSame(CellStyleAccessDto expected, CellStyleAccessDto actual)
    {
        Assert.Equal(expected.CanStyle, actual.CanStyle);
        Assert.Equal(expected.DeniedReason, actual.DeniedReason);
        Assert.Equal(expected.StyleDeniedRowKeys, actual.StyleDeniedRowKeys);
        Assert.Equal(expected.StyleDeniedColumnCodes, actual.StyleDeniedColumnCodes);
        Assert.Equal(expected.StyleDeniedCells.ToList(), actual.StyleDeniedCells.ToList());
        Assert.Equal(expected.Truncated, actual.Truncated);
    }

    /// <summary>Строкатий детермінований набір рішень зрізу.</summary>
    private sealed record World(
        Dictionary<CellAddress, EditDecision> Decisions,
        Dictionary<long, string> RowKeys,
        Dictionary<int, string> AllCodes,
        Dictionary<int, string> VisibleCodes,
        HashSet<int> Hidden,
        HashSet<int> Computed)
    {
        public bool CanRead(int columnId) => !Hidden.Contains(columnId);

        public static World Create(int seed)
        {
            var random = new Random(seed);

            // Форми: 0 — порожньо; 1 — усе заборонено (нічия причин); 2 — обрізання (> MaxKeys рядків цілком);
            // інші — змішані з цілком забороненими рядками/колонками.
            var shape = seed % 6;
            var rowCount = shape switch { 0 => 0, 2 => CellStyleAccessPolicy.MaxKeys + 40, _ => random.Next(3, 40) };
            var columnCount = shape == 0 ? 0 : random.Next(3, 12);

            var rowKeys = new Dictionary<long, string>();
            for (var r = 1; r <= rowCount; r++)
            {
                // Рядок без ключа (не з цього зрізу) — ~5 %; ключі з різною довжиною, щоб ординальний порядок мав значення.
                if (shape >= 3 && random.Next(20) == 0)
                {
                    continue;
                }

                rowKeys[r] = r % 3 == 0 ? $"r{r}" : $"R{r:D3}";
            }

            var allCodes = new Dictionary<int, string>();
            var visible = new Dictionary<int, string>();
            var hidden = new HashSet<int>();
            var computed = new HashSet<int>();
            for (var c = 1; c <= columnCount; c++)
            {
                allCodes[c] = c % 2 == 0 ? $"c{c}" : $"COL_{c}";

                if (shape >= 3 && random.Next(6) == 0)
                {
                    hidden.Add(c);
                }
                else
                {
                    visible[c] = allCodes[c];
                }

                if (random.Next(4) == 0)
                {
                    computed.Add(c);
                }
            }

            var deniedRows = new HashSet<long>();
            var deniedColumns = new HashSet<int>();
            if (shape >= 3)
            {
                for (var r = 1; r <= rowCount; r++)
                {
                    if (random.Next(5) == 0)
                    {
                        deniedRows.Add(r);
                    }
                }

                for (var c = 1; c <= columnCount; c++)
                {
                    if (random.Next(4) == 0)
                    {
                        deniedColumns.Add(c);
                    }
                }
            }

            var decisions = new Dictionary<CellAddress, EditDecision>();

            // Рядки йдуть не за порядком ключів — як із бази.
            var order = Enumerable.Range(1, rowCount).OrderBy(_ => random.Next()).ToList();

            // Колонка без опису (у ключах немає) — ~ще одна.
            var columnIds = Enumerable.Range(1, columnCount).Append(columnCount + 1).ToList();

            foreach (var r in order)
            {
                foreach (var c in columnIds)
                {
                    if (c > columnCount && random.Next(3) != 0)
                    {
                        continue;
                    }

                    EditDecision decision;
                    if (shape == 1)
                    {
                        // Нічия двох причин: перемагає менше значення enum.
                        decision = EditDecision.Deny((r + c) % 2 == 0 ? EditDenyReason.NoGrant : EditDenyReason.RowReadOnly);
                    }
                    else if (shape == 2)
                    {
                        decision = r == 1 && c == 1
                            ? EditDecision.Allow()
                            : r == 1 || c == 1 ? EditDecision.Deny(EditDenyReason.ColumnReadOnly) : EditDecision.Deny(EditDenyReason.NoGrant);
                    }
                    else if (deniedRows.Contains(r) || deniedColumns.Contains(c))
                    {
                        decision = EditDecision.Deny(Reasons[random.Next(Reasons.Length)]);
                    }
                    else
                    {
                        decision = random.Next(8) switch
                        {
                            0 => EditDecision.Deny(Reasons[random.Next(Reasons.Length)], "detail"),
                            1 => EditDecision.AllowWithConfirmation(random.Next(2) == 0 ? null : $"confirm {r}/{c}"),
                            2 => EditDecision.AllowMarked("marked"),
                            3 when computed.Contains(c) => EditDecision.Deny(EditDenyReason.CalculatedCell),
                            _ => EditDecision.Allow(),
                        };
                    }

                    decisions[new CellAddress(Period, r, c)] = decision;
                }
            }

            return new World(decisions, rowKeys, allCodes, visible, hidden, computed);
        }
    }

    /// <summary>Дослівна копія попереднього циклу з <c>GetTableSliceHandler</c> (до перф-правки).</summary>
    private static (Dictionary<string, string>, Dictionary<string, string>) OldPermissionLoop(
        Dictionary<CellAddress, EditDecision> decisions,
        Dictionary<long, string> keyById,
        Func<int, bool> canReadColumn,
        Dictionary<int, string> columnCodeById,
        HashSet<int> computedColumns)
    {
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, decision) in decisions)
        {
            if (!keyById.TryGetValue(address.TableRowId, out var rowKey))
            {
                continue;
            }

            if (!canReadColumn(address.ColumnDefId)
                || !columnCodeById.TryGetValue(address.ColumnDefId, out var code))
            {
                continue;
            }

            var key = $"{rowKey}:{code}";

            if (!decision.IsAllowed)
            {
                if (decision.Reason == EditDenyReason.CalculatedCell
                    && computedColumns.Contains(address.ColumnDefId))
                {
                    continue;
                }

                permissions[key] = decision.Reason.ToString();
                continue;
            }

            if (decision.RequiresConfirmation)
            {
                confirmations[key] = decision.Detail ?? string.Empty;
            }
        }

        return (permissions, confirmations);
    }

    /// <summary>Дослівна копія попереднього <c>CellStyleAccessPolicy.Build</c> (до перф-правки).</summary>
    private static CellStyleAccessDto OldStyleBuild(
        Dictionary<CellAddress, EditDecision>? decisions,
        Dictionary<long, string> rowKeyById,
        Dictionary<int, string> visibleColumnCodeById)
    {
        var cells = new List<(string RowKey, string ColumnCode, EditDecision Decision)>();
        if (decisions is not null)
        {
            foreach (var (address, decision) in decisions)
            {
                if (rowKeyById.TryGetValue(address.TableRowId, out var rowKey)
                    && visibleColumnCodeById.TryGetValue(address.ColumnDefId, out var code))
                {
                    cells.Add((rowKey, code, decision));
                }
            }
        }

        if (cells.Count == 0)
        {
            return new CellStyleAccessDto(false, null, [], [], new Dictionary<string, string>(), false);
        }

        var denied = cells.Where(c => !c.Decision.IsAllowed).ToList();

        if (denied.Count == cells.Count)
        {
            var reason = denied
                .GroupBy(c => c.Decision.Reason)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => (int)g.Key)
                .First().Key;

            return new CellStyleAccessDto(false, reason.ToString(), [], [], new Dictionary<string, string>(), false);
        }

        var rowTotals = cells.GroupBy(c => c.RowKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var columnTotals = cells.GroupBy(c => c.ColumnCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var deniedRows = denied.GroupBy(c => c.RowKey, StringComparer.Ordinal)
            .Where(g => g.Count() == rowTotals[g.Key])
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var deniedColumns = denied.GroupBy(c => c.ColumnCode, StringComparer.Ordinal)
            .Where(g => g.Count() == columnTotals[g.Key])
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var residual = denied
            .Where(c => !deniedRows.Contains(c.RowKey) && !deniedColumns.Contains(c.ColumnCode))
            .OrderBy(c => c.RowKey, StringComparer.Ordinal)
            .ThenBy(c => c.ColumnCode, StringComparer.Ordinal)
            .ToList();

        var budget = CellStyleAccessPolicy.MaxKeys;
        var rowKeys = deniedRows.Order(StringComparer.Ordinal).Take(budget).ToList();
        budget -= rowKeys.Count;
        var columnCodes = deniedColumns.Order(StringComparer.Ordinal).Take(budget).ToList();
        budget -= columnCodes.Count;

        var cellMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in residual.Take(budget))
        {
            cellMap[$"{cell.RowKey}:{cell.ColumnCode}"] = cell.Decision.Reason.ToString();
        }

        var truncated = rowKeys.Count < deniedRows.Count
                        || columnCodes.Count < deniedColumns.Count
                        || cellMap.Count < residual.Count;

        return new CellStyleAccessDto(true, null, rowKeys, columnCodes, cellMap, truncated);
    }
}
