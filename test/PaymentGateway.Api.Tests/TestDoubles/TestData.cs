using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests.TestDoubles;

public static class TestData
{
    /// <summary>Fixed "now" for deterministic expiry checks: 15 April 2026, UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 4, 15, 10, 30, 0, TimeSpan.Zero);

    public const string AuthorizedCard = "2222405343248877";
    public const string DeclinedCard = "2222405343298112";
    public const string UnavailableCard = "2222405343218110";

    public static PostPaymentRequest ValidRequest(string cardNumber = AuthorizedCard) => new()
    {
        CardNumber = cardNumber,
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 1250,
        Cvv = "123"
    };
}
