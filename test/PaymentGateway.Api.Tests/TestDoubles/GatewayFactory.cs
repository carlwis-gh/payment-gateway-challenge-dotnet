using Microsoft.AspNetCore.Hosting;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Interfaces;

namespace PaymentGateway.Api.Tests.TestDoubles;

/// <summary>The real API with a fake bank, a fixed clock and two test merchants (DESIGN 7.1).</summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public const string MerchantA = "merchant-a";
    public const string MerchantB = "merchant-b";
    public const string MerchantAKey = "test-key-merchant-a";
    public const string MerchantBKey = "test-key-merchant-b";

    public FakeBankClient Bank { get; } = new();
    public FixedTimeProvider Clock { get; } = new(TestData.Now);

    /// <summary>Every log call the gateway makes, at every level and category.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Capture at Trace, below the configured levels, so a test sees anything that could ever
        // be logged, not just what today's configuration lets through.
        builder.ConfigureLogging(logging => logging
            .AddProvider(Logs)
            .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IAcquiringBankClient>(Bank);
        });
        builder.ConfigureTestServices(services =>
            services.PostConfigure<MerchantApiKeyOptions>(MerchantAuth.Scheme, options =>
                options.Merchants = new()
                {
                    new() { MerchantId = MerchantA, KeyHash = Hash(MerchantAKey) },
                    new() { MerchantId = MerchantB, KeyHash = Hash(MerchantBKey) }
                }));
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
