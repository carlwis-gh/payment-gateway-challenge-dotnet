using Microsoft.Extensions.Logging.Abstractions;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;
using PaymentGateway.Api.Tests.TestDoubles;

namespace PaymentGateway.Api.Tests;

public sealed class PaymentProcessorTests : IDisposable
{
    private const string Merchant = "merchant-a";

    private readonly FakeBankClient _bank = new();
    private readonly PaymentsRepository _repository = new();
    private readonly MetricRecorder _metrics = new();
    private readonly PaymentProcessor _processor;

    public PaymentProcessorTests()
    {
        var timeProvider = new FixedTimeProvider(TestData.Now);
        _processor = new PaymentProcessor(_bank, _repository, timeProvider, _metrics.Metrics, NullLogger<PaymentProcessor>.Instance);
    }

    public void Dispose() => _metrics.Dispose();

    private Task<PaymentResult> Process(PostPaymentRequest? request, string merchant = Merchant) =>
        _processor.ProcessAsync(merchant, request);

    private Payment? Stored(Guid id, string merchant = Merchant) => _repository.Get(id, merchant);

    [Theory]
    [InlineData(TestData.AuthorizedCard, PaymentStatus.Authorized)] // AC-1
    [InlineData(TestData.DeclinedCard, PaymentStatus.Declined)] // AC-2
    public async Task BankOutcome_IsMappedToStatusAndStored(string card, PaymentStatus expected)
    {
        var result = await Process(TestData.ValidRequest(card));

        var payment = Assert.IsType<PaymentResult.Processed>(result).Payment;
        Assert.Equal(expected, payment.Status);
        Assert.NotEqual(Guid.Empty, payment.Id);
        Assert.Equal(card[^4..], payment.CardNumberLastFour);
        Assert.Equal(Merchant, payment.MerchantId);
        Assert.Equal(12, payment.ExpiryMonth);
        Assert.Equal(2030, payment.ExpiryYear);
        Assert.Equal("GBP", payment.Currency);
        Assert.Equal(1250, payment.Amount);
        Assert.Equal(TestData.Now, payment.CreatedAt);
        Assert.Equal(payment, Stored(payment.Id));
    }

    [Theory]
    [InlineData(TestData.AuthorizedCard, FakeBankClient.AuthorizationCode)]
    [InlineData(TestData.DeclinedCard, null)] // the bank sends no code with a decline
    public async Task BanksAuthorizationCode_IsStoredWithThePayment(string card, string? expectedCode)
    {
        var payment = Assert.IsType<PaymentResult.Processed>(await Process(TestData.ValidRequest(card))).Payment;

        Assert.Equal(expectedCode, payment.AuthorizationCode);
        Assert.Equal(expectedCode, Stored(payment.Id)!.AuthorizationCode);
    }

    // DESIGN D11, 2.3: Only the last four digits are stored; never the full card number or CVV,
    // nor anything derived from them (the accepted idempotency risk in section 6 depends on this).
    [Fact]
    public async Task StoredPayment_HoldsNoCardNumberOrCvv()
    {
        var request = TestData.ValidRequest();
        request.Cvv = "4831";
        var payment = Assert.IsType<PaymentResult.Processed>(await Process(request)).Payment;

        var stored = System.Text.Json.JsonSerializer.Serialize(Stored(payment.Id));
        Assert.DoesNotContain(TestData.AuthorizedCard, stored);
        Assert.DoesNotContain("\"4831\"", stored);
        var allowed = new[]
        {
            nameof(Payment.Id), nameof(Payment.Status), nameof(Payment.CardNumberLastFour), nameof(Payment.ExpiryMonth),
            nameof(Payment.ExpiryYear), nameof(Payment.Currency), nameof(Payment.Amount), nameof(Payment.MerchantId),
            nameof(Payment.CreatedAt), nameof(Payment.AuthorizationCode)
        };
        // A new field on the stored record has to be added here deliberately.
        Assert.Equal(allowed.Order(), typeof(Payment).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task BankReceivesTheFullRequest()
    {
        await Process(TestData.ValidRequest());

        var call = Assert.Single(_bank.Calls);
        Assert.Equal(new BankAuthorizationRequest(TestData.AuthorizedCard, 12, 2030, "GBP", 1250, "123"), call);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankMerchantId_Throws_WithoutCallingTheBank(string merchant) // Auth guarantees a merchant
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Process(TestData.ValidRequest(), merchant));
        Assert.Equal(0, _bank.CallCount);
    }

    [Fact]
    public async Task InvalidBody_IsRejected_WithoutCallingTheBank() // AC-3
    {
        var request = TestData.ValidRequest();
        request.Currency = "JPY";

        var result = await Process(request);

        var rejected = Assert.IsType<PaymentResult.Rejected>(result);
        Assert.Contains("currency must be one of GBP, EUR, USD", rejected.Errors);
        Assert.Equal(0, _bank.CallCount);
    }

    [Fact]
    public async Task NullBody_IsRejected()
    {
        Assert.IsType<PaymentResult.Rejected>(await Process(null));
        Assert.Equal(0, _bank.CallCount);
    }

    [Theory]
    [InlineData(BankResult.NotProcessed, 502, StringResources.BankNotProcessed)]
    [InlineData(BankResult.Unknown, 502, StringResources.BankOutcomeUnknown)]
    [InlineData(BankResult.TimedOut, 504, StringResources.BankTimeout)]
    public async Task BankFailure_ReturnsStatus_AndStoresNoPayment(BankResult bankResult, int expectedStatus, string expectedMessage) // AC-4, AC-5
    {
        _bank.NextResult = bankResult;

        var result = await Process(TestData.ValidRequest());

        var failure = Assert.IsType<PaymentResult.BankFailure>(result);
        Assert.Equal(expectedStatus, failure.StatusCode);
        Assert.Equal(expectedMessage, failure.Message);
        Assert.Equal(1, _bank.CallCount);
    }

    // DESIGN D7: A merchant must be able to tell a safe retry from one that may charge twice.
    [Fact]
    public void BankFailureMessages_SayWhetherTheOutcomeIsKnown()
    {
        Assert.Contains("not processed", StringResources.BankNotProcessed);
        Assert.Contains("outcome is unknown", StringResources.BankOutcomeUnknown);
        Assert.Contains("outcome is unknown", StringResources.BankTimeout);
    }

    [Theory]
    [InlineData(TestData.AuthorizedCard, "authorized")]
    [InlineData(TestData.DeclinedCard, "declined")]
    public async Task ProcessedPayment_IsCounted_ByOutcomeAndCurrency(string card, string outcome) // DESIGN D18
    {
        await Process(TestData.ValidRequest(card));

        var payment = Assert.Single(_metrics.Of("payment_gateway.payments"));
        Assert.Equal(1, payment.Value);
        Assert.Equal(outcome, payment.Tags["outcome"]);
        Assert.Equal("GBP", payment.Tags["currency"]);

        Assert.Equal(outcome, Assert.Single(_metrics.Of("payment_gateway.bank.requests")).Tags["result"]);
        Assert.Equal(outcome, Assert.Single(_metrics.Of("payment_gateway.bank.duration")).Tags["result"]);
    }

    [Fact]
    public async Task RejectedRequest_IsCounted_WithoutCurrency_AndNoBankMetric() // DESIGN D18
    {
        var request = TestData.ValidRequest();
        request.Currency = "not-a-currency";

        await Process(request);

        var payment = Assert.Single(_metrics.Of("payment_gateway.payments"));
        Assert.Equal("rejected", payment.Tags["outcome"]);
        // An unvalidated value would make the metric's cardinality unbounded.
        Assert.False(payment.Tags.ContainsKey("currency"));
        Assert.Empty(_metrics.Of("payment_gateway.bank.requests"));
    }

    [Theory]
    [InlineData(BankResult.NotProcessed, "not_processed")]
    [InlineData(BankResult.Unknown, "unknown")]
    [InlineData(BankResult.TimedOut, "timed_out")]
    public async Task BankFailure_IsCountedByResult_AndNoPaymentCounted(BankResult bankResult, string tag) // DESIGN D18
    {
        _bank.NextResult = bankResult;

        await Process(TestData.ValidRequest());

        Assert.Equal(tag, Assert.Single(_metrics.Of("payment_gateway.bank.requests")).Tags["result"]);
        Assert.Equal(tag, Assert.Single(_metrics.Of("payment_gateway.bank.duration")).Tags["result"]);
        Assert.Empty(_metrics.Of("payment_gateway.payments"));
    }

    [Fact]
    public async Task MetricTags_NeverIncludePaymentOrMerchantIds() // DESIGN D18: bounded cardinality
    {
        await Process(TestData.ValidRequest());

        var allowed = new[] { "outcome", "currency", "result" };
        Assert.All(_metrics.Measurements, m => Assert.All(m.Tags.Keys, key => Assert.Contains(key, allowed)));
    }

    // Section 6 risk, accepted: there is no idempotency (DESIGN 8.3), so a repeated request is a
    // new payment. This pins that behaviour so adding duplicate prevention is a deliberate change.
    [Fact]
    public async Task RepeatedRequest_IsProcessedAsANewPayment()
    {
        var first = Assert.IsType<PaymentResult.Processed>(await Process(TestData.ValidRequest()));
        var second = Assert.IsType<PaymentResult.Processed>(await Process(TestData.ValidRequest()));

        Assert.NotEqual(first.Payment.Id, second.Payment.Id);
        Assert.Equal(2, _bank.CallCount);
    }
}
