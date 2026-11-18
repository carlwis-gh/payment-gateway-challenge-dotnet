using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests;

/// <summary>
/// Runs only when BANK_SIMULATOR_URL is set (e.g. http://localhost:8080) and the simulator is
/// up via docker-compose (DESIGN 7.1). Skipped otherwise so `dotnet test` needs no Docker.
/// </summary>
public sealed class SimulatorFactAttribute : TheoryAttribute
{
    public const string EnvironmentVariable = "BANK_SIMULATOR_URL";

    public SimulatorFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to run smoke tests against the bank simulator";
        }
    }
}

public class BankSimulatorSmokeTests
{
    [SimulatorFact]
    [InlineData("2222405343248877", BankResult.Authorized)]
    [InlineData("2222405343298112", BankResult.Declined)]
    [InlineData("2222405343218110", BankResult.NotProcessed)]
    public async Task CardEnding_DecidesTheOutcome(string cardNumber, BankResult expected)
    {
        var baseUrl = new Uri(Environment.GetEnvironmentVariable(SimulatorFactAttribute.EnvironmentVariable)!);
        var client = new AcquiringBankClient(
            new HttpClient(),
            Options.Create(new BankClientOptions { BaseUrl = baseUrl }),
            NullLogger<AcquiringBankClient>.Instance);

        var result = await client.AuthorizeAsync(new BankAuthorizationRequest(cardNumber, 4, 2030, "GBP", 100, "123"));

        Assert.Equal(expected, result.Outcome);
        // The simulator sends a code only with an authorization.
        Assert.Equal(expected == BankResult.Authorized, result.AuthorizationCode is not null);
    }
}
