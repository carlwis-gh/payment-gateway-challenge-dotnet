using System.Diagnostics.Metrics;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Payment and bank metrics on the "PaymentGateway" meter (DESIGN D18). Built on
/// System.Diagnostics.Metrics, so any listener can read them (dotnet-counters locally, an
/// OpenTelemetry exporter in production) without the gateway depending on one.
/// </summary>
/// <remarks>
/// Tags are bounded on purpose: outcome, bank result and a validated currency. Payment ids and
/// merchant ids would give every payment its own time series, so they belong in logs, not here.
/// HTTP request rates, status codes and latencies are already emitted by ASP.NET Core's own
/// meters (Microsoft.AspNetCore.Hosting), so they are not repeated.
/// </remarks>
public sealed class PaymentMetrics
{
    public const string MeterName = "PaymentGateway";

    private readonly Counter<long> _payments;
    private readonly Counter<long> _bankRequests;
    private readonly Histogram<double> _bankDuration;

    public PaymentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _payments = meter.CreateCounter<long>(
            "payment_gateway.payments", unit: "{payment}",
            description: "Payment requests by outcome: authorized, declined or rejected.");
        _bankRequests = meter.CreateCounter<long>(
            "payment_gateway.bank.requests", unit: "{request}",
            description: "Calls to the acquiring bank by result. timed_out and unknown are payments whose outcome is unknown.");
        _bankDuration = meter.CreateHistogram<double>(
            "payment_gateway.bank.duration", unit: "s",
            description: "Duration of calls to the acquiring bank, by result.");
    }

    /// <summary>A request failed validation. Currency is not tagged: an unvalidated value is unbounded.</summary>
    public void PaymentRejected() =>
        _payments.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));

    /// <summary>The bank gave a definitive answer.</summary>
    public void PaymentProcessed(PaymentStatus status, string currency) =>
        _payments.Add(1,
            new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Authorized ? "authorized" : "declined"),
            new KeyValuePair<string, object?>("currency", currency));

    /// <summary>One bank call finished, successfully or not.</summary>
    public void BankCallCompleted(BankResult result, TimeSpan duration)
    {
        var tag = new KeyValuePair<string, object?>("result", ResultTag(result));
        _bankRequests.Add(1, tag);
        _bankDuration.Record(duration.TotalSeconds, tag);
    }

    private static string ResultTag(BankResult result) => result switch
    {
        BankResult.Authorized => "authorized",
        BankResult.Declined => "declined",
        BankResult.NotProcessed => "not_processed",
        BankResult.TimedOut => "timed_out",
        _ => "unknown"
    };
}
