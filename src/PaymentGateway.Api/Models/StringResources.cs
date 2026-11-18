namespace PaymentGateway.Api.Models;

/// <summary>Messages returned to merchants in error and Rejected bodies.</summary>
public static class StringResources
{
    public const string PaymentNotFound = "Payment not found";
    public const string Unauthorized = "A valid API key is required";

    // DESIGN D7: The three bank failures carry distinct messages, because they mean different
    // things to a merchant deciding whether to retry. Only "not processed" is safe to retry.
    public const string BankNotProcessed = "The acquiring bank is unavailable; the payment was not processed";
    public const string BankOutcomeUnknown = "The acquiring bank returned an unexpected response; the payment outcome is unknown";
    public const string BankTimeout = "The acquiring bank did not respond in time; the payment outcome is unknown";

    public const string InvalidJsonBody = "Request body is missing or is not valid JSON";
    public const string UnsupportedContentType = "Content-Type must be application/json";
    public const string UnexpectedError = "An unexpected error occurred";

    /// <summary>A body field that could not be read as its declared type, e.g. "amount is invalid".</summary>
    public static string FieldIsInvalid(string fieldName) => $"{fieldName} is invalid";
}
