using PaymentGateway.Api.Interfaces;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Validation;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Coordinates validation, the bank call and storing the outcome (DESIGN 4.2, 5.3).
/// </summary>
public sealed class PaymentProcessor : IPaymentProcessor
{
    private readonly IAcquiringBankClient _bankClient;
    private readonly IPaymentsRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly PaymentMetrics _metrics;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(
        IAcquiringBankClient bankClient,
        IPaymentsRepository repository,
        TimeProvider timeProvider,
        PaymentMetrics metrics,
        ILogger<PaymentProcessor> logger)
    {
        _bankClient = bankClient;
        _repository = repository;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<PaymentResult> ProcessAsync(string merchantId, PostPaymentRequest? request)
    {
        // The merchant id comes from the authenticated caller (DESIGN D3), so it is never missing here.
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);

        // AC-3: Validate the body before anything else; the bank is not called.
        var errors = PaymentRequestValidator.Validate(request, _timeProvider.GetUtcNow());
        if (errors.Count > 0)
        {
            // DESIGN D18: A spike in rejections points at a merchant integration problem, not the bank.
            _metrics.PaymentRejected();
            _logger.LogInformation("Payment request rejected with {ErrorCount} validation errors", errors.Count);
            return new PaymentResult.Rejected(errors);
        }

        // Validation guarantees the body is present from here on.
        var valid = request!;

        // idempotency: (DESIGN 8.3) Reserve the merchant's Idempotency-Key here, before the bank
        // call, and replay the stored outcome for a repeated key instead of calling the bank again.
        return await AuthorizeAsync(merchantId, valid);
    }

    private async Task<PaymentResult> AuthorizeAsync(string merchantId, PostPaymentRequest request)
    {
        // telemetry: (DESIGN 8.5) Start a "bank.authorize" span here once traces are exported.
        // The request's cancellation token is deliberately not passed on: a merchant disconnect
        // must not abort the bank call halfway and leave the outcome unknown.
        var started = _timeProvider.GetTimestamp();
        var bankResult = await _bankClient.AuthorizeAsync(new BankAuthorizationRequest(
            request.CardNumber!,
            request.ExpiryMonth!.Value,
            request.ExpiryYear!.Value,
            request.Currency!,
            request.Amount!.Value,
            request.Cvv!));
        // DESIGN D18: Bank latency and result, to catch a slowing or failing bank early.
        _metrics.BankCallCompleted(bankResult.Outcome, _timeProvider.GetElapsedTime(started));

        switch (bankResult.Outcome)
        {
            // AC-1, AC-2: The bank gave a definitive answer.
            case BankResult.Authorized:
            case BankResult.Declined:
                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    Status = bankResult.Outcome == BankResult.Authorized ? PaymentStatus.Authorized : PaymentStatus.Declined,
                    // AC-9: Only the last four digits are kept, so the full card number can never be returned.
                    CardNumberLastFour = request.CardNumber![^4..],
                    ExpiryMonth = request.ExpiryMonth!.Value,
                    ExpiryYear = request.ExpiryYear!.Value,
                    Currency = request.Currency!,
                    Amount = request.Amount!.Value,
                    MerchantId = merchantId,
                    // DESIGN D15: Taken after the bank answers, so it is when the outcome became known.
                    CreatedAt = _timeProvider.GetUtcNow(),
                    AuthorizationCode = bankResult.AuthorizationCode
                };

                _repository.Add(payment);
                // The authorization code is a bank reference, not card data, so it is safe to log.
                _logger.LogInformation("Payment {PaymentId} {Status} for merchant {MerchantId} with authorization code {AuthorizationCode}",
                    payment.Id, payment.Status, merchantId, payment.AuthorizationCode);
                // DESIGN D18: A sudden drop in the authorized share is a key alert.
                _metrics.PaymentProcessed(payment.Status, payment.Currency);
                return new PaymentResult.Processed(payment);

            // AC-4: Bank returned 503 or 400, so it did not process the payment; a retry is safe.
            case BankResult.NotProcessed:
                return new PaymentResult.BankFailure(StatusCodes.Status502BadGateway, StringResources.BankNotProcessed);

            // AC-5: Bank timed out; the card may or may not have been charged (D7).
            case BankResult.TimedOut:
                // idempotency: (DESIGN 8.3) Keep the key locked with an unknown outcome and queue the
                // payment for reconciliation with the bank, so a retry cannot charge the card twice.
                return new PaymentResult.BankFailure(StatusCodes.Status504GatewayTimeout, StringResources.BankTimeout);

            // AC-4: Unexpected bank response; the outcome is unknown, as for a timeout (D7).
            case BankResult.Unknown:
            default:
                // idempotency: (DESIGN 8.3) Lock the key and queue for reconciliation, as for a timeout.
                return new PaymentResult.BankFailure(StatusCodes.Status502BadGateway, StringResources.BankOutcomeUnknown);
        }
    }
}
