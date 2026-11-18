using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Validation;

/// <summary>
/// Pure validation of the payment request body (DESIGN 1.3, 2.2, 5.4).
/// All errors are collected and returned together. No message echoes a submitted value.
/// </summary>
public static class PaymentRequestValidator
{
    /// <summary>Approved currencies (DESIGN D5).</summary>
    // Assumption-7: Currency codes are matched case-sensitively in upper case (GBP, not gbp).
    // config: (DESIGN 8.7) Load the supported currencies from configuration instead of code.
    public static readonly IReadOnlySet<string> SupportedCurrencies =
        new HashSet<string>(StringComparer.Ordinal) { "GBP", "EUR", "USD" };

    public static IReadOnlyList<string> Validate(PostPaymentRequest? request, DateTimeOffset utcNow)
    {
        var errors = new List<string>();

        if (request is null)
        {
            errors.Add("Request body is required");
            return errors;
        }

        if (request.CardNumber is null)
        {
            errors.Add("cardNumber is required");
        }
        else if (!IsAsciiDigits(request.CardNumber, minLength: 14, maxLength: 19))
        {
            errors.Add("cardNumber must be 14 to 19 numeric characters");
        }
        // DESIGN D6: A mistyped card number fails the checksum and is Rejected without a bank call.
        else if (!PassesLuhn(request.CardNumber))
        {
            errors.Add("cardNumber is not a valid card number");
        }

        var monthValid = false;
        if (request.ExpiryMonth is null)
        {
            errors.Add("expiryMonth is required");
        }
        else if (request.ExpiryMonth is < 1 or > 12)
        {
            errors.Add("expiryMonth must be between 1 and 12");
        }
        else
        {
            monthValid = true;
        }

        var yearValid = false;
        if (request.ExpiryYear is null)
        {
            errors.Add("expiryYear is required");
        }
        else if (request.ExpiryYear is < 1000 or > 9999)
        {
            errors.Add("expiryYear must be a four-digit year");
        }
        else
        {
            yearValid = true;
        }

        // Assumption-6: The card is valid until the end of its expiry month, so the current month
        // is accepted. Comparing month indexes avoids DateTime overflow at year 9999.
        if (monthValid && yearValid)
        {
            var today = utcNow.UtcDateTime;
            var expiryIndex = request.ExpiryYear!.Value * 12 + request.ExpiryMonth!.Value;
            var currentIndex = today.Year * 12 + today.Month;
            if (expiryIndex < currentIndex)
            {
                errors.Add("expiryMonth and expiryYear must be in the future");
            }
        }

        if (request.Currency is null)
        {
            errors.Add("currency is required");
        }
        else if (request.Currency.Length != 3)
        {
            errors.Add("currency must be exactly 3 characters");
        }
        else if (!SupportedCurrencies.Contains(request.Currency))
        {
            errors.Add("currency must be one of GBP, EUR, USD");
        }

        if (request.Amount is null)
        {
            errors.Add("amount is required");
        }
        else if (request.Amount <= 0)
        {
            errors.Add("amount must be greater than zero");
        }

        if (request.Cvv is null)
        {
            errors.Add("cvv is required");
        }
        else if (!IsAsciiDigits(request.Cvv, minLength: 3, maxLength: 4))
        {
            errors.Add("cvv must be 3 or 4 numeric characters");
        }

        return errors;
    }

    /// <summary>
    /// ASCII 0-9 only. <see cref="char.IsDigit(char)"/> also accepts non-ASCII digits and must
    /// not be used (DESIGN 5.4).
    /// </summary>
    private static bool IsAsciiDigits(string value, int minLength, int maxLength)
    {
        if (value.Length < minLength || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Luhn (mod 10) checksum. Starting from the rightmost digit, every second digit is doubled
    /// (subtracting 9 when the result exceeds 9); the total must be a multiple of 10.
    /// Assumes ASCII digits only, which <see cref="IsAsciiDigits"/> has already checked.
    /// </summary>
    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
