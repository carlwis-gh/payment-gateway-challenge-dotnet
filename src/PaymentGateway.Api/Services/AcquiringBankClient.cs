using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Interfaces;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Encapsulates communication with the acquiring bank: format mapping, timeout and error
/// translation (DESIGN 5.2.3, 5.3).
/// </summary>
public sealed class AcquiringBankClient : IAcquiringBankClient
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly ILogger<AcquiringBankClient> _logger;

    public AcquiringBankClient(HttpClient httpClient, IOptions<BankClientOptions> options, ILogger<AcquiringBankClient> logger)
    {
        _httpClient = httpClient;
        _timeout = options.Value.Timeout;
        _logger = logger;

        // The timeout is applied per call below so that it can be told apart from caller cancellation.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.BaseAddress ??= options.Value.BaseUrl;
    }

    public async Task<BankAuthorizationResult> AuthorizeAsync(BankAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        var body = ToBankRequest(request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            // telemetry: (DESIGN 8.5) HttpClient instrumentation would trace this call and propagate the
            // trace context to the bank. Make sure the request body (card number, CVV) is never captured.
            // resilience: (DESIGN 8.6) With a circuit breaker on this client, an open circuit would
            // throw before sending and should map to BankResult.NotProcessed (the bank never saw it).
            using var response = await _httpClient.PostAsJsonAsync("payments", body, timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning("Acquiring bank did not process the payment: HTTP {StatusCode}", (int)response.StatusCode);
                return new(BankResult.NotProcessed);
            }

            // The gateway validates before calling, so a 400 means our request was wrong: a gateway
            // defect (DESIGN 5.2.3), logged as an error.
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogError("Acquiring bank rejected the request as invalid: HTTP {StatusCode}", (int)response.StatusCode);
                return new(BankResult.NotProcessed);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("Acquiring bank returned unexpected HTTP {StatusCode}", (int)response.StatusCode);
                return new(BankResult.Unknown);
            }

            var bankResponse = await response.Content.ReadFromJsonAsync<BankPaymentResponse>(timeoutCts.Token);
            var outcome = bankResponse?.Authorized switch
            {
                true => BankResult.Authorized,
                false => BankResult.Declined,
                null => LogUnreadable()
            };

            // Kept for tracing and reconciliation. The simulator sends "" on a decline, stored as null.
            var authorizationCode = string.IsNullOrWhiteSpace(bankResponse?.AuthorizationCode)
                ? null
                : bankResponse.AuthorizationCode;
            return new(outcome, outcome == BankResult.Unknown ? null : authorizationCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Acquiring bank did not respond within {Timeout}", _timeout);
            return new(BankResult.TimedOut);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // We cannot tell whether the bank received the request, so the outcome is unknown.
            _logger.LogError("Acquiring bank call failed: {ExceptionType}", ex.GetType().Name);
            return new(BankResult.Unknown);
        }
    }

    /// <summary>expiry_date is MM/YYYY with the month zero-padded (DESIGN 5.2.3).</summary>
    public static BankPaymentRequest ToBankRequest(BankAuthorizationRequest request) => new()
    {
        CardNumber = request.CardNumber,
        ExpiryDate = string.Create(CultureInfo.InvariantCulture, $"{request.ExpiryMonth:D2}/{request.ExpiryYear:D4}"),
        Currency = request.Currency,
        Amount = request.Amount,
        Cvv = request.Cvv
    };

    private BankResult LogUnreadable()
    {
        _logger.LogError("Acquiring bank returned 200 without an authorized flag");
        return BankResult.Unknown;
    }
}
