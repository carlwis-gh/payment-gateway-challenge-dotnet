using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.TestDoubles;

public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Owns a meter factory and records every measurement from the PaymentGateway meter it created.
/// Listening only to meters from its own factory keeps parallel tests apart (DESIGN D18).
/// </summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly MeterListener _listener = new();

    public MetricRecorder()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = _services.GetRequiredService<IMeterFactory>();
        Metrics = new PaymentMetrics(factory);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PaymentMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public PaymentMetrics Metrics { get; }

    public ConcurrentQueue<Measurement> Measurements { get; } = new();

    public IReadOnlyList<Measurement> Of(string instrument) => Measurements.Where(m => m.Instrument == instrument).ToList();

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
}
