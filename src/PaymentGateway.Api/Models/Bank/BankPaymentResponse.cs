using System.Text.Json.Serialization;

namespace PaymentGateway.Api.Models.Bank;

/// <summary>Wire format of the acquiring bank response.</summary>
public sealed record BankPaymentResponse
{
    [JsonPropertyName("authorized")] public bool? Authorized { get; init; }
    [JsonPropertyName("authorization_code")] public string? AuthorizationCode { get; init; }
}
