using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Domain;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests;

public class PaymentsRepositoryTests
{
    private readonly PaymentsRepository _repository = new();

    private static Payment NewPayment(string merchantId = "merchant-a") => new()
    {
        Id = Guid.NewGuid(),
        Status = PaymentStatus.Authorized,
        CardNumberLastFour = "8877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 1250,
        MerchantId = merchantId,
        CreatedAt = TestDoubles.TestData.Now
    };

    [Fact]
    public void StoresAndRetrievesForTheOwningMerchant()
    {
        var payment = NewPayment();
        _repository.Add(payment);

        Assert.Equal(payment, _repository.Get(payment.Id, "merchant-a"));
    }

    [Fact]
    public void AnotherMerchant_GetsNothing()
    {
        var payment = NewPayment();
        _repository.Add(payment);

        Assert.Null(_repository.Get(payment.Id, "merchant-b"));
    }

    [Fact]
    public void MerchantIdMatch_IsCaseSensitive()
    {
        var payment = NewPayment();
        _repository.Add(payment);

        Assert.Null(_repository.Get(payment.Id, "MERCHANT-A"));
    }

    [Fact]
    public void UnknownId_GetsNothing()
    {
        Assert.Null(_repository.Get(Guid.NewGuid(), "merchant-a"));
    }

    // DESIGN 5.3: The repository is a singleton shared by every request, so it must be thread-safe.
    [Fact]
    public void ConcurrentAddsAndGets_LoseNothing_AndKeepMerchantsApart()
    {
        var payments = Enumerable.Range(0, 2000)
            .Select(i => NewPayment(i % 2 == 0 ? "merchant-a" : "merchant-b"))
            .ToList();

        // Every payment is written on one thread and read back on others while writes continue.
        Parallel.ForEach(payments, new ParallelOptions { MaxDegreeOfParallelism = 16 }, payment =>
        {
            _repository.Add(payment);
            Assert.Equal(payment, _repository.Get(payment.Id, payment.MerchantId));
            var other = payment.MerchantId == "merchant-a" ? "merchant-b" : "merchant-a";
            Assert.Null(_repository.Get(payment.Id, other));
        });

        Assert.All(payments, payment => Assert.Equal(payment, _repository.Get(payment.Id, payment.MerchantId)));
    }

    [Fact]
    public void ConcurrentDuplicateAdds_StoreExactlyOne()
    {
        var payment = NewPayment();
        var stored = 0;

        Parallel.For(0, 64, _ =>
        {
            try
            {
                _repository.Add(payment);
                Interlocked.Increment(ref stored);
            }
            catch (InvalidOperationException)
            {
            }
        });

        Assert.Equal(1, stored);
    }

    [Fact]
    public void DuplicateId_Throws()
    {
        var payment = NewPayment();
        _repository.Add(payment);

        Assert.Throws<InvalidOperationException>(() => _repository.Add(payment));
    }
}
