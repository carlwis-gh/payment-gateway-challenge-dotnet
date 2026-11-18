# Payment Gateway

A .NET 8 ASP.NET Core API that lets a merchant submit a card payment and retrieve it later.
Valid requests are sent to the acquiring bank (simulated); the outcome is stored in memory.
See [DESIGN.md](DESIGN.md) for the requirements, decisions and test strategy. The challenge
brief is [here](https://github.com/cko-recruitment/).

## Prerequisites

- .NET 8 SDK
- Docker Compose (only needed to run the bank simulator)

## Run

### Everything in Docker

```sh
docker compose up --build
```

This builds the gateway image and starts it next to the bank simulator. The gateway listens on
`http://localhost:5080`, with Swagger at [`http://localhost:5080/swagger`](http://localhost:5080/swagger),
and logs JSON. The container serves plain HTTP, so replace `https://localhost:7092` with
`http://localhost:5080` in the examples below.

### From source

Start the bank simulator (listens on `http://localhost:8080`):

```sh
docker compose up -d bank_simulator
```

Run the API from the repository root:

```sh
dotnet run --project src/PaymentGateway.Api
```

The development profile serves the API at `https://localhost:7092` and opens Swagger at
[`https://localhost:7092/swagger`](https://localhost:7092/swagger). It also listens on
`http://localhost:5067`, but only to redirect to HTTPS, so use the HTTPS address in requests (curl
does not follow the redirect for a POST). If curl rejects the certificate, trust the .NET
development certificate once with `dotnet dev-certs https --trust`.
The bank URL and timeout are configured in `appsettings.json` under `Bank:BaseUrl` and
`Bank:Timeout`, and can be overridden with the `Bank__BaseUrl` and `Bank__Timeout` environment variables.

Payments are held in memory and cleared when the API restarts.

There is no duplicate prevention: every POST is a new payment, so retrying a request can charge the
card twice. Idempotency is out of scope (see DESIGN.md, section 8.3).

## API

### `POST /api/payments`

| Header | Rules |
| --- | --- |
| `Authorization` | Required. `Bearer <api key>`. The key identifies the merchant; see Authentication below. |

```sh
curl -X POST https://localhost:7092/api/payments \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sk_test_merchant_a" \
  -d '{"cardNumber":"2222405343248877","expiryMonth":12,"expiryYear":2030,"currency":"GBP","amount":1250,"cvv":"123"}'
```

```json
{
  "id": "3f2b8c1e-5d4a-4e7b-9a10-2c6f8d0b1e77",
  "status": "Authorized",
  "cardNumberLastFour": "8877",
  "expiryMonth": 12,
  "expiryYear": 2030,
  "currency": "GBP",
  "amount": 1250,
  "createdAt": "2026-04-15T10:30:00+00:00"
}
```

The simulator decides by the card's last digit: odd is Authorized, even is Declined, 0 makes the
bank unavailable. The gateway also requires the card number to pass the Luhn check, so use these:

| Card number | Outcome |
| --- | --- |
| `2222405343248877` | Authorized |
| `2222405343298112` | Declined |
| `2222405343218110` | 502, bank unavailable (not processed) |

| Situation | Status | Body |
| --- | --- | --- |
| Authorized or Declined | 200 | Payment details |
| Invalid request, malformed JSON or a non-JSON `Content-Type` | 400 | `{"status":"Rejected","errors":[...]}` |
| Missing or invalid API key | 401 | `{"error":"..."}` |
| Bank did not process the payment (bank 503 or 400) | 502 | `{"error":"...the payment was not processed"}` |
| Unexpected bank response | 502 | `{"error":"...the payment outcome is unknown"}` |
| Bank timeout | 504 | `{"error":"...the payment outcome is unknown"}` |
| Unexpected gateway error | 500 | `{"error":"..."}` |

The error message says whether a retry is safe. "Not processed" means the bank never handled the payment.
"Outcome unknown" (an unexpected bank response, or a timeout) means the bank may have charged the card,
so a retry may charge it twice (DESIGN.md, D7).

### `GET /api/payments/{id}`

Requires the API key. Returns 200 with the payment details, 401 without a valid key, and 404
for an unknown or malformed id or a payment that belongs to another merchant.

### Authentication

Merchants are configured in the `MerchantAuth` section as a merchant id and the SHA-256 hash of
its API key; raw keys are never stored. `appsettings.Development.json` has two development
merchants:

| Merchant | Development key |
| --- | --- |
| `merchant-a` | `sk_test_merchant_a` |
| `merchant-b` | `sk_test_merchant_b` |

The gateway refuses to start without at least one valid merchant entry, so it never reports ready
while answering every request with 401. To add a merchant, hash its key and add an entry:

```sh
printf '%s' 'the-new-key' | shasum -a 256
```

## Health and metrics

| Endpoint | Meaning |
| --- | --- |
| `GET /health/live` | The process is running. Runs no checks. |
| `GET /health/ready` | Ready for traffic. No checks are registered yet; a database check would go here. |

Neither needs an API key, and neither checks the bank: a bank outage should produce 502s, not
restarts or an empty load balancer (DESIGN.md, D18).

### Metrics

Payment and bank metrics are on the `PaymentGateway` meter (DESIGN.md, D18):

| Metric | Tags | Shows |
| --- | --- | --- |
| `payment_gateway.payments` | `outcome` (authorized, declined, rejected), `currency` (processed payments only) | Payment volumes and the authorized share |
| `payment_gateway.bank.requests` | `result` (authorized, declined, not_processed, timed_out, unknown) | Bank errors; `timed_out` + `unknown` are payments whose outcome is unknown |
| `payment_gateway.bank.duration` | `result` | Bank latency (50th, 95th and 99th percentiles) |

HTTP request counts, status codes and latencies come from ASP.NET Core's built-in
`Microsoft.AspNetCore.Hosting` meter.

Nothing exports these yet (DESIGN.md, 8.5); you read them from the running process with
[`dotnet-counters`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters).

1. Install it once. It goes in `~/.dotnet/tools`, which you may need to add to your `PATH`:

   ```sh
   dotnet tool install -g dotnet-counters
   ```

2. Start the bank simulator and run the gateway from source (see Run above). `dotnet-counters`
   attaches to a local process, so it cannot read the gateway inside the Docker container.

3. In another terminal, watch the counters live:

   ```sh
   dotnet-counters monitor -n PaymentGateway.Api --counters PaymentGateway,Microsoft.AspNetCore.Hosting
   ```

   If more than one `PaymentGateway.Api` is running, use `-p <pid>` instead of `-n`.

4. Send some payments with the curl example and the test cards above. Each card gives a known
   result: `2222405343248877` counts as authorized, `2222405343298112` as declined,
   `2222405343218110` as bank `not_processed`, and an unsupported currency such as `JPY` as rejected.

Counters are shown as a rate per refresh interval (one second by default), so a value shows up in
the second the payment happens and then falls back to 0. To keep a record you can read afterwards,
write a CSV instead:

```sh
dotnet-counters collect -n PaymentGateway.Api --counters PaymentGateway --format csv -o counters.csv --duration 00:00:30
```

## Tests

```sh
dotnet test
```

The smoke tests against the real simulator are skipped unless `BANK_SIMULATOR_URL` is set:

```sh
BANK_SIMULATOR_URL=http://localhost:8080 dotnet test
```

The container smoke test exercises the built image running next to the simulator (needs `curl` and `jq`):

```sh
docker compose up -d --build
```

```sh
scripts/container-smoke-test.sh
```

GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs the full suite, smoke
tests included, against the simulator on every push and pull request to `main`, then builds the
Docker image, starts it with compose and runs the container smoke test against it.

## Logs

Logs are structured: each field (payment id, status, merchant id, trace id) is a separate property,
and every line of an authenticated request carries the merchant id and trace id.
They are JSON by default and in Docker, and single-line text in Development (`dotnet run`). The
format is set by `Logging:Console:FormatterName`. Card numbers, CVVs, request bodies and API keys are
never logged.

```sh
docker compose logs -f payment_gateway
```

```sh
docker compose logs -f bank_simulator
```

## Solution structure

```text
src/PaymentGateway.Api/
  Program.cs        Startup: DI registrations and the middleware pipeline
  Controllers/      Payment API (HTTP mapping)
  Interfaces/       Contracts for the services below (one per file)
  Services/         Payment Processor, Acquiring Bank Client, Payment Repository,
                    API key authentication handler, payment metrics
  Config/           Options bound from configuration (merchant API keys, bank client)
  Validation/       Request body validation (pure, static)
  Models/           Payment result, status and error strings
    Domain/         The stored Payment entity
    Requests/       API request body
    Responses/      API response bodies
    Bank/           Acquiring bank wire formats and outcomes
test/PaymentGateway.Api.Tests/   xUnit unit, API, log and smoke tests
scripts/container-smoke-test.sh  HTTP smoke test of the running container
imposters/                       Bank simulator configuration
Dockerfile                       Multi-stage build of the gateway image
docker-compose.yml               Bank simulator and gateway services
.github/workflows/ci.yml         Build, test (with the simulator), image build and container smoke test
```
