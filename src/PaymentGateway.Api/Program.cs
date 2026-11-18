using System.Security.Claims;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Interfaces;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(options =>
    {
        // A body that is not JSON (e.g. Content-Type: text/plain) is Rejected like malformed JSON
        // (DESIGN 5.4), not answered with the framework's 415. Without this filter the error stays
        // in ModelState and the controller turns it into the Rejected body.
        foreach (var filter in options.Filters.OfType<UnsupportedContentTypeFilter>().ToList())
        {
            options.Filters.Remove(filter);
        }
    })
    .AddJsonOptions(options =>
    {
        // Status is serialised as a string (DESIGN 5.2.2).
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // The web defaults accept numbers in strings; "amount": "100" must be rejected (DESIGN 2.2).
        options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // Every invalid POST must return the Rejected body, not the framework's 400 (DESIGN 5.4).
        options.SuppressModelStateInvalidFilter = true;
    });

// DESIGN D16: Logs are structured. The console formatter is chosen in configuration
// (Logging:Console:FormatterName): JSON by default so a log pipeline can query by field, and the
// readable "simple" formatter in Development.
// DESIGN D18: Payment and bank metrics on the "PaymentGateway" meter, and health endpoints.
// telemetry: (DESIGN 8.5) Register OpenTelemetry here to export them: ASP.NET Core and HttpClient
// tracing, the "PaymentGateway" meter, and an OTLP exporter.
builder.Services.AddSingleton<PaymentMetrics>();
// No checks are registered yet, so readiness only says the process has started with valid
// configuration. The bank is deliberately not checked (DESIGN D18).
// persistence: (DESIGN 8.1) Register the database check here, tagged "ready".
builder.Services.AddHealthChecks();
// Merchants authenticate with an API key; the merchant id comes from the verified key (DESIGN D3).
builder.Services
    .AddAuthentication(MerchantAuth.Scheme)
    .AddScheme<MerchantApiKeyOptions, ApiKeyAuthenticationHandler>(MerchantAuth.Scheme,
        options => builder.Configuration.GetSection(MerchantAuth.ConfigSection).Bind(options));
// A missing merchant list or a malformed key hash fails at startup rather than leaving a gateway
// that reports ready but answers every request with 401 (DESIGN D3, D18).
builder.Services.AddOptions<MerchantApiKeyOptions>(MerchantAuth.Scheme)
    .Validate(options => options.Merchants.Count > 0 && options.Merchants.All(m => m.IsValid()),
        "MerchantAuth needs at least one merchant, each with a MerchantId and a 64-character hex SHA-256 KeyHash.")
    .ValidateOnStart();
builder.Services.AddAuthorization();

// rate-limiting: (DESIGN 8.6) Register AddRateLimiter with a token-bucket policy partitioned by
// merchant, so one merchant cannot flood the gateway or the bank.

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Payment Gateway API",
        Version = "v1",
        Description = "Submit card payments to the acquiring bank and retrieve them later. See DESIGN.md."
    });
    // The /// comments on the controller and models become the operation and field descriptions.
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml"));

    var scheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Merchant API key",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = MerchantAuth.Scheme
        }
    };
    options.AddSecurityDefinition(MerchantAuth.Scheme, scheme);
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

builder.Services.AddSingleton(TimeProvider.System);
// persistence: (DESIGN 8.1) Swap in a database-backed IPaymentsRepository here (scoped, with its
// methods made async; the processor and controller then await them).
builder.Services.AddSingleton<IPaymentsRepository, PaymentsRepository>();
builder.Services.AddScoped<IPaymentProcessor, PaymentProcessor>();

builder.Services.Configure<BankClientOptions>(builder.Configuration.GetSection(BankClientOptions.SectionName));
builder.Services.AddHttpClient<IAcquiringBankClient, AcquiringBankClient>((serviceProvider, client) =>
{
    client.BaseAddress = serviceProvider.GetRequiredService<IOptions<BankClientOptions>>().Value.BaseUrl;
});
// resilience: (DESIGN 8.6) Add a circuit breaker to the bank HttpClient here (e.g.
// AddResilienceHandler) so persistent 503s or timeouts fail fast. Retries would only cover 503,
// never timeouts, because a timed-out call may already have charged the card.

var app = builder.Build();

// Unhandled errors still return the documented error shape (DESIGN 2.3).
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    // The merchant scope below has already closed by the time the exception reaches here, so it
    // is opened again for this line (DESIGN D16). The verified user is still on the context.
    var merchantId = context.User.FindFirstValue(MerchantAuth.MerchantIdClaim);
    using (merchantId is null ? null : logger.BeginScope(new Dictionary<string, object> { ["MerchantId"] = merchantId }))
    {
        logger.LogError("Unhandled exception: {ExceptionType}", exception?.GetType().Name);
    }
    // ASP.NET Core's own meters already count these (http.server.request.duration with
    // status 500). telemetry: (DESIGN 8.5) Record the exception on the current trace span.

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new ErrorResponse(StringResources.UnexpectedError));
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// DESIGN D17: The container serves plain HTTP behind a TLS-terminating proxy, so redirecting there
// would only log "Failed to determine the https port" on every request. The official .NET images
// set DOTNET_RUNNING_IN_CONTAINER=true.
if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// DESIGN D16: Every log line for an authenticated request, including the bank client's, carries
// the merchant id from the verified API key. Never add card data to a scope.
app.Use(async (context, next) =>
{
    var merchantId = context.User.FindFirstValue(MerchantAuth.MerchantIdClaim);
    if (merchantId is null)
    {
        await next(context);
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    using (logger.BeginScope(new Dictionary<string, object> { ["MerchantId"] = merchantId }))
    {
        await next(context);
    }
});
// rate-limiting: (DESIGN 8.6) app.UseRateLimiter() goes here, after auth, so limits are keyed on
// the verified merchant.

app.MapControllers();

// DESIGN D18: Liveness runs no checks: the process answering is enough, so a bank outage never
// makes an orchestrator restart a healthy gateway. Readiness runs the checks tagged "ready".
// Neither needs an API key.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

/// <summary>Exposed so tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program
{
}
