using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Tests.TestDoubles;
using PaymentGateway.Api.Validation;

namespace PaymentGateway.Api.Tests;

/// <summary>Every boundary in DESIGN 2.2 (AC-3).</summary>
public class PaymentRequestValidatorTests
{
    private static IReadOnlyList<string> Validate(Action<PostPaymentRequest> change, DateTimeOffset? now = null)
    {
        var request = TestData.ValidRequest();
        change(request);
        return PaymentRequestValidator.Validate(request, now ?? TestData.Now);
    }

    [Fact]
    public void ValidRequest_HasNoErrors()
    {
        Assert.Empty(PaymentRequestValidator.Validate(TestData.ValidRequest(), TestData.Now));
    }

    [Fact]
    public void NullRequest_IsRejected()
    {
        Assert.Equal(new[] { "Request body is required" }, PaymentRequestValidator.Validate(null, TestData.Now));
    }

    [Fact]
    public void EmptyRequest_ReportsEveryMissingField()
    {
        var errors = PaymentRequestValidator.Validate(new PostPaymentRequest(), TestData.Now);

        Assert.Equal(
            new[]
            {
                "cardNumber is required", "expiryMonth is required", "expiryYear is required",
                "currency is required", "amount is required", "cvv is required"
            },
            errors);
    }

    [Theory]
    [InlineData("12345678931234")]
    [InlineData("1234567890123416789")]
    public void CardNumber_Accepted(string cardNumber)
    {
        Assert.Empty(Validate(r => r.CardNumber = cardNumber));
    }

    [Theory]
    [InlineData("1234567890123")]
    [InlineData("12345678901234567890")]
    [InlineData("123456789O123456")]
    [InlineData("1234 5678 9012 3456")]
    [InlineData("1234-5678-9012-3456")]
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢٣٤٥")] // Arabic-Indic digits: char.IsDigit would accept these
    [InlineData("")]
    public void CardNumber_Rejected(string cardNumber)
    {
        Assert.Equal(new[] { "cardNumber must be 14 to 19 numeric characters" }, Validate(r => r.CardNumber = cardNumber));
    }

    [Theory]
    [InlineData("4111111111111111")] // Well-known Visa test number
    [InlineData("5555555555554444")] // Well-known Mastercard test number
    [InlineData("378282246310005")]  // Odd length: the doubling must start from the right
    [InlineData("00000000000000")]   // All zeroes sums to 0, a multiple of 10
    public void CardNumber_PassingLuhn_IsAccepted(string cardNumber)
    {
        Assert.Empty(Validate(r => r.CardNumber = cardNumber));
    }

    [Theory]
    [InlineData("4111111111111112")] // Check digit off by one
    [InlineData("4111111111111121")] // Two adjacent digits swapped
    [InlineData("378282246310006")]
    public void CardNumber_FailingLuhn_IsRejected(string cardNumber)
    {
        Assert.Equal(new[] { "cardNumber is not a valid card number" }, Validate(r => r.CardNumber = cardNumber));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void ExpiryMonth_Accepted(int month)
    {
        Assert.Empty(Validate(r => { r.ExpiryMonth = month; r.ExpiryYear = 2030; }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public void ExpiryMonth_Rejected(int month)
    {
        Assert.Equal(new[] { "expiryMonth must be between 1 and 12" }, Validate(r => r.ExpiryMonth = month));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(999)]
    [InlineData(10000)]
    public void ExpiryYear_NotFourDigits_Rejected(int year)
    {
        Assert.Equal(new[] { "expiryYear must be a four-digit year" }, Validate(r => r.ExpiryYear = year));
    }

    [Fact]
    public void ExpiryYear_9999_DoesNotOverflow()
    {
        Assert.Empty(Validate(r => { r.ExpiryMonth = 12; r.ExpiryYear = 9999; }));
    }

    [Theory]
    [InlineData(4, 2026)] // Assumption-6: the current month is still valid.
    [InlineData(5, 2026)] // next month
    [InlineData(1, 2027)] // next year
    public void Expiry_CurrentOrLater_Accepted(int month, int year)
    {
        Assert.Empty(Validate(r => { r.ExpiryMonth = month; r.ExpiryYear = year; }));
    }

    [Theory]
    [InlineData(3, 2026)] // last month
    [InlineData(12, 2025)] // last year
    [InlineData(4, 2025)] // same month, past year
    public void Expiry_InThePast_Rejected(int month, int year)
    {
        Assert.Equal(
            new[] { "expiryMonth and expiryYear must be in the future" },
            Validate(r => { r.ExpiryMonth = month; r.ExpiryYear = year; }));
    }

    [Fact]
    public void Expiry_LastDayOfExpiryMonth_StillValid()
    {
        var lastMoment = new DateTimeOffset(2026, 4, 30, 23, 59, 59, TimeSpan.Zero);

        Assert.Empty(Validate(r => { r.ExpiryMonth = 4; r.ExpiryYear = 2026; }, lastMoment));
    }

    [Fact]
    public void Expiry_FirstDayAfterExpiryMonth_Rejected()
    {
        var firstMoment = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.NotEmpty(Validate(r => { r.ExpiryMonth = 4; r.ExpiryYear = 2026; }, firstMoment));
    }

    [Fact]
    public void Expiry_DecemberToJanuaryRollover()
    {
        var december = new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(Validate(r => { r.ExpiryMonth = 12; r.ExpiryYear = 2026; }, december));
        Assert.Empty(Validate(r => { r.ExpiryMonth = 1; r.ExpiryYear = 2027; }, december));
        Assert.NotEmpty(Validate(r => { r.ExpiryMonth = 11; r.ExpiryYear = 2026; }, december));
    }

    [Fact]
    public void Expiry_ComparedInUtc()
    {
        // 1 May 00:30 in UTC+2 is still 30 April in UTC, so an April card is valid.
        var lateApril = new DateTimeOffset(2026, 5, 1, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.Empty(Validate(r => { r.ExpiryMonth = 4; r.ExpiryYear = 2026; }, lateApril));
    }

    [Theory]
    [InlineData("GBP")]
    [InlineData("EUR")]
    [InlineData("USD")]
    public void Currency_Accepted(string currency)
    {
        Assert.Empty(Validate(r => r.Currency = currency));
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("gbp")]
    public void Currency_Unsupported_Rejected(string currency)
    {
        Assert.Equal(new[] { "currency must be one of GBP, EUR, USD" }, Validate(r => r.Currency = currency));
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("GBPP")]
    [InlineData("")]
    public void Currency_WrongLength_Rejected(string currency)
    {
        Assert.Equal(new[] { "currency must be exactly 3 characters" }, Validate(r => r.Currency = currency));
    }

    [Fact]
    public void Amount_One_Accepted()
    {
        Assert.Empty(Validate(r => r.Amount = 1));
    }

    [Fact]
    public void Amount_Max_Accepted()
    {
        Assert.Empty(Validate(r => r.Amount = long.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Amount_NotPositive_Rejected(long amount)
    {
        Assert.Equal(new[] { "amount must be greater than zero" }, Validate(r => r.Amount = amount));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("0000")]
    public void Cvv_Accepted(string cvv)
    {
        Assert.Empty(Validate(r => r.Cvv = cvv));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("12a")]
    [InlineData("")]
    public void Cvv_Rejected(string cvv)
    {
        Assert.Equal(new[] { "cvv must be 3 or 4 numeric characters" }, Validate(r => r.Cvv = cvv));
    }

    [Fact]
    public void AllErrors_AreCollected_AndNoneEchoValues()
    {
        var request = new PostPaymentRequest
        {
            CardNumber = "98765X",
            ExpiryMonth = 77,
            ExpiryYear = 31,
            Currency = "XYZ",
            Amount = -4242,
            Cvv = "9z"
        };

        var errors = PaymentRequestValidator.Validate(request, TestData.Now);

        Assert.Equal(6, errors.Count);
        foreach (var error in errors)
        {
            Assert.DoesNotContain("98765X", error);
            Assert.DoesNotContain("77", error);
            Assert.DoesNotContain("XYZ", error);
            Assert.DoesNotContain("4242", error);
            Assert.DoesNotContain("9z", error);
        }
    }
}
