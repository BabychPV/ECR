// tests/Ecr.Api.Tests/BudgetMetricsFilterTests.cs
using System.Diagnostics.Metrics;
using System.Reflection;
using Ecr.Api.Observability;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Фільтр бюджету міряє РЕАЛЬНІ дії й РЕАЛЬНУ кількість комірок
/// (аудит 2026-09-16, §9).
/// </summary>
/// <remarks>
/// ⛔ Дві половини того самого дефекту. Перша: ключі таблиці маршрутів були
/// <c>Documents.Slice</c> і <c>Documents.PatchCells</c>, яких НЕ ІСНУЄ —
/// читання зрізу віддає <c>CellsController.GetSlice</c>, пакетний запис —
/// <c>CellsController.Patch</c>, тож дві найгарячіші метрики бюджету
/// (<c>cells_read</c>, <c>cells_write</c>) не спрацьовували ЖОДНОГО разу. Друга:
/// навіть коли б спрацювали, фільтр передавав літеральний <c>0</c> у лічильник
/// комірок — графік бюджету стверджував би, що система читає й пише рівно нуль
/// комірок.
/// </remarks>
public sealed class BudgetMetricsFilterTests
{
    [Theory]
    [InlineData("Cells", "GetSlice", EcrMetrics.CellsRead)]
    [InlineData("Cells", "Patch", EcrMetrics.CellsWrite)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ключі_таблиці_маршрутів_відповідають_СПРАВЖНІМ_діям(
        string controller, string action, string metric)
    {
        // ⛔ Мутаційна суть: із попередніми ключами (`Documents.Slice`,
        // `Documents.PatchCells`) обидва рядки нижче нічого не знаходять.
        var routes = (IReadOnlyDictionary<string, (string Metric, string Operation)>)
            typeof(BudgetMetricsFilter)
                .GetField("Routes", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

        Assert.True(
            routes.TryGetValue($"{controller}.{action}", out var mapping),
            $"Дії {controller}.{action} немає в таблиці бюджету — метрика не спрацює ніколи.");

        Assert.Equal(metric, mapping.Metric);

        // І та сама дія справді існує в контролері — інакше ключ був би
        // правильним лише на вигляд.
        var type = typeof(Program).Assembly
            .GetTypes()
            .Single(t => t.Name == $"{controller}Controller");

        Assert.Contains(action, type.GetMethods().Select(m => m.Name), StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Лічильник_комірок_бере_РЕАЛЬНЕ_число_а_не_нуль()
    {
        // ⛔ Друга половина §9: до фіксу тут стояв літеральний `cellCount: 0`.
        using var listener = new MeterListener();
        var recorded = new List<(string Metric, int? Cells)>();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EcrMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            int? cells = null;
            foreach (var tag in tags)
            {
                if (tag.Key is "cells" or "formulas" && tag.Value is int value)
                {
                    cells = value;
                }
            }

            recorded.Add((instrument.Name, cells));
        });

        listener.Start();

        using var meterFactory = new TestMeterFactory();
        var metrics = new EcrMetrics(meterFactory);
        var filter = new BudgetMetricsFilter(metrics);

        var http = new DefaultHttpContext();

        // Саме те, що робить дія після фіксу: повідомляє реальну кількість.
        EcrMetrics.ReportCount(http, 137);

        await filter.OnActionExecutionAsync(
            Context(http, "Cells", "GetSlice"),
            () => Task.FromResult(new ActionExecutedContext(
                Context(http, "Cells", "GetSlice"), [], controller: null!)));

        var measurement = Assert.Single(recorded, r => r.Metric == EcrMetrics.CellsRead);

        Assert.Equal(137, measurement.Cells);
        Assert.NotEqual(0, measurement.Cells);
    }

    private static ActionExecutingContext Context(HttpContext http, string controller, string action)
    {
        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = action;

        return new ActionExecutingContext(
            new ActionContext(http, routeData, new ControllerActionDescriptor()),
            [],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            controller: null!);
    }

    /// <summary>Фабрика метрик, що тримає власний <see cref="Meter"/>.</summary>
    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var meter = new Meter(options.Name, options.Version);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }
}
