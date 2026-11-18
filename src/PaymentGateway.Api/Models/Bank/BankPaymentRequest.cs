using System.Text.Json.Serialization;

namespace PaymentGateway.Api.Models.Bank;

/// <summary>Wire format of the acquiring bank request (DESIGN 5.2.3, D12).</summary>
public sealed record BankPaymentRequest
{
    [JsonPropertyName("card_number")] public required string CardNumber { get; init; }
    [JsonPropertyName("expiry_date")] public required string ExpiryDate { get; init; }
    [JsonPropertyName("currency")] public required string Currency { get; init; }
    [JsonPropertyName("amount")] public required long Amount { get; init; }
    [JsonPropertyName("cvv")] public required string Cvv { get; init; }
}
