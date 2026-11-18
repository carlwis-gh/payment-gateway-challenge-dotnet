# Payment Gateway Design

This is the .NET version of the Payment Gateway challenge. The challenge details can be found [here](https://github.com/cko-recruitment/).

## 0. Overview

The Payment Gateway is an ASP.NET Core Web API that lets a merchant submit a card payment and later retrieve it. It validates the request, forwards valid requests to an acquiring bank (simulated), stores the outcome, and returns one of three results: Authorized or Declined as per the bank simulator's response; or Rejected if the payment fails the API-level validation for any reason.

- **Approach:** four small components (API, Processor, Bank Client, Payment Repository), validation before any bank call, in-memory storage, no packages beyond ASP.NET Core, Swashbuckle (from the template, for Swagger) and the test libraries. See section 8 for deferred capabilities and the rationale for excluding them.
- **Reading guide:** the brief is covered by sections 1, 2, 4 and 5 and decisions D1 to D13. D14 to D18 (authorization code, timestamps, logging, container and CI, metrics and health) are production extras, and section 8 is what is left out and why; skip them if you are short of time.

## 1. Requirements

The product requirements for this initial phase are taken from the brief. Where the brief is ambiguous, or a requirement is added by this design, that is noted.

### 1.1 Payment flow participants

1. **Shopper:** individual purchasing a product or service using a payment card.
2. **Merchant:** seller of the product or service. Initiates payment requests through the Payment Gateway and retrieves payment details.
3. **Payment Gateway:** the system being developed. Validates payment requests, coordinates payment processing, communicates with the Acquiring Bank and returns results to merchants.
4. **Acquiring Bank:** external institution that processes authorisation requests and returns an authorisation or decline result.

The scope is limited to the Payment Gateway. The Shopper, Merchant and Acquiring Bank are external. The Acquiring Bank is simulated for testing.

### 1.2 Functional requirements

1. A merchant can process a payment through the gateway and receive one of:
   - **Authorized:** the payment was authorized by the call to the acquiring bank.
   - **Declined:** the payment was declined by the call to the acquiring bank.
   - **Rejected:** no payment could be created because invalid information was supplied, so the request was rejected without calling the acquiring bank.
2. A merchant can retrieve the details of a previously made payment.

### 1.3 Validation requirements

#### 1.3.1 Payment Request
All fields are required. If any rule fails, the request must not be sent to the bank.

| Field | Rules |
| --- | --- |
| Card number | 14 to 19 characters; numeric characters only; passes the Luhn checksum (D6, an addition to the brief) |
| Expiry month | Integer from 1 to 12 |
| Expiry year | The month/year combination must be in the future |
| Currency | Exactly 3 characters; one of at most 3 supported currency codes |
| Amount | Positive 64-bit integer representing the minor currency unit |
| CVV | 3 or 4 characters; numeric characters only |

"In the future" is read as including the current month, because a card is valid until the end of its expiry month (Assumption 6).

#### 1.3.2 Payment Retrieval

- The payment being retrieved (by id) has to match the merchant id of the merchant making the GET request.
- Payment retrieval returns the persisted payment details without reapplying payment request validation rules. Historical payments remain retrievable even after the associated card expires.

#### 1.3.3 Request headers

| Header | Applies to | Rules |
| --- | --- | --- |
| Authorization | POST and GET | Required; `Bearer <merchant API key>`. The key identifies the merchant (D3). |

### 1.4 Response requirements

- Successful payments must return the payment identifier, status, last four card digits, expiry month/year, currency and amount. The gateway also returns when the payment was recorded (`createdAt`, D15), an addition to the brief.
- Valid values for `status` on a successful payment request response received from the bank are `Authorized` and `Declined`.
- Rejected payments must include Status = `Rejected`. (The brief's response field list only names Authorized and Declined; returning Rejected follows from requirement 1.2.1.)
- Where the bank fails to return a definitive payment outcome, the gateway returns an appropriate HTTP error (i.e. not `Authorized`, `Declined` or `Rejected`). The error says whether the bank did not process the payment (safe to retry) or the outcome is unknown (a retry may charge twice), D7.
- The full card number and CVV must never be returned.

## 2. Acceptance Criteria

### 2.1 Functional criteria

> **Note:** One row per observable behaviour, written as given/when/then. The IDs are referenced from the test traceability table later.

| ID | Given / When | Then |
| --- | --- | --- |
| AC-1 | Valid request; card number ends in an odd digit | 200; status Authorized; id (GUID), last four digits, expiry, currency, amount returned; payment stored |
| AC-2 | Valid request; card number ends in an even digit (2,4,6,8) | 200; status Declined; same fields returned; payment stored |
| AC-3 | Any validation rule fails, or the body is missing, null, malformed JSON or sent with a non-JSON Content-Type | 400; body has status Rejected; bank is not called; nothing is stored |
| AC-4 | Valid request; bank is unavailable (card ends in 0 returns 503) or returns an unexpected error | 502; error body saying the payment was not processed (503, 400) or that its outcome is unknown (anything else); no payment stored; no retry |
| AC-5 | Valid request; bank call times out | 504; error body saying the outcome is unknown; no payment stored; no retry |
| AC-6 | GET with the id of a stored payment | 200; same details as the original response |
| AC-7 | GET with an unknown or malformed id | 404 |
| AC-8 | GET with the id of a stored payment and the merchant id that doesn't match | 404 (not 403, so the payment's existence is not revealed) |
| AC-9 | Any response | Never contains the full card number or CVV |
| AC-10 | POST or GET without a valid API key | 401; error body; checked before the body is read, so the bank and repository are not touched |

### 2.2 Validation boundary criteria

| Field | Accepted | Rejected |
| --- | --- | --- |
| Card number | 14 digits, 19 digits, odd and even lengths that pass Luhn | 13 digits, 20 digits, letters, spaces or hyphens, non-ASCII digits, empty, missing, a wrong check digit, two swapped digits |
| Expiry month | 1, 12 | 0, 13, missing |
| Expiry year | Four-digit year | Two-digit year (e.g. 30), missing |
| Expiry month/year | Current month and year; any later month; December to January rollover into next year | Any earlier month (including last month); year in the past |
| Currency | GBP, EUR, USD | JPY (not supported), gbp (lower case), GB (2 chars), GBPP (4 chars), empty, missing |
| Amount | 1 | 0, negative, 12.5 (non-integer), a string, a value too large for the type, missing |
| CVV | 3 digits, 4 digits | 2 digits, 5 digits, letters, missing |
| API key | A configured merchant's key | Missing, empty, unknown, not a Bearer value, a merchant id instead of a key |

### 2.3 Non-functional criteria

| Aspect | Rules |
| --- | --- |
| Security | The full card number and CVV are never persisted or logged |
| Security | Only the last four digits of a card are stored |
| Security | Validation errors never echo submitted values |
| Reliability | The bank call has a timeout |
| Reliability | Bank failures return a clear error and store no payment |
| Consistency | Every failure from the payment endpoints returns JSON in a documented shape (section 5.2.2). Requests that match no endpoint (an unknown path, or a method such as PUT) get the framework's empty 404 or 405 |
| Testability | The clock and the bank client are injectable so tests are deterministic |
| Operability | Logs are structured and carry the trace id and merchant id (D16); payment and bank metrics and health endpoints (D18); the gateway runs from one container image configured by environment and smoke tested in CI (D17) |
| Simplicity | No over-engineering, as the brief asks; each component has one clear job |

## 3. Scope, Assumptions and Decisions

### 3.1 Scope

- In scope
  - POST /api/payments
  - GET /api/payments/{id}, restricted to the requesting merchant's payments
  - Merchant authentication with API keys (D3)
  - Validation, including the Luhn check (D6)
  - Bank client, with a timeout and clear failure mapping (D7)
  - In-memory payment repository (Assumption 10)
  - OpenAPI (Swagger) documentation generated from the code
  - Structured, correlated logging (D16)
  - Payment and bank metrics, liveness and readiness endpoints (D18)
  - Container image, compose file and CI, including a smoke test of the running container (D17)
  - Automated tests (section 7).
- Out of scope
  - Durable persistence (8.1)
  - API key management and OAuth (8.2)
  - Idempotency (duplicate prevention) and reconciliation (D4, 8.3)
  - Scheme-aware card checks (8.4)
  - Exporting metrics and traces, dashboards and alerting (8.5)
  - Circuit breaking, retries and rate limiting (8.6)
  - Deploying the image, and the currencies still held in code (8.7)
  - Capture, refund, listing and other payment operations (8.8)
  - Section 8 has the reasons and how each would be added.

### 3.2 Assumptions

1. The connection between the merchant and the payment processor is secure (HTTPS and certificates).
2. The connection between the payment processor and the bank is secure.
3. The payment processor has been authorised to access the bank out of band; the gateway sends no bank credentials. (Merchants, by contrast, are authenticated by the gateway: D3.)
4. Merchants are issued API keys out of band; the gateway only verifies them (D3). Issuing, rotating and revoking keys is out of scope (8.2).
5. Selecting USD, GBP and EUR as currencies is acceptable.
6. Payment cards remain valid through the final day of the printed expiry month, so a card expiring in April 2026 is valid for any payment made in April 2026.
7. Currency codes are matched case-sensitively in upper case (GBP, not gbp).
8. Expiry year is a four-digit year; two-digit years are rejected.
9. The amount is a 64-bit integer in minor units and must be greater than zero; zero, negative values, non-integers and values that overflow are rejected.
10. The in-memory repository is acceptable, as the brief states no real storage engine is needed. Payments are lost on restart.
11. Multiple merchants can use the system, and each payment is tied to the merchant that requests it.
12. The bank simulator decides the outcome by the last digit of the card number. Any Luhn-valid number of the right length is usable for testing; the test data picks one for each ending.
13. The gateway processes payments synchronously for the purposes of this exercise. Asynchronous processing, automatic retries and reconciliation with the bank are outside scope.
14. Every POST is treated as a new payment attempt. A merchant that retries a request (for example after a timeout or a dropped connection) may be charged twice; preventing this is out of scope (D4, 8.3).

### 3.3 Decision log

**D1. Payment requests are only sent to the bank if validation succeeds.**

- It avoids the latency and cost of a bank call that cannot succeed.
- It protects the bank from requests that are guaranteed to fail (rate limiting, penalties).
- It keeps a clear boundary: the gateway validates format; the bank decides authorisation.

**D2. The payment id is a GUID.**

- It provides a sufficiently large identifier space to make accidental collisions extraordinarily unlikely.
- It makes sequential enumeration impractical.
- It avoids exposing transaction volume through sequential identifiers.

**D3. The merchant identity comes from a verified API key.**

- Each merchant sends `Authorization: Bearer <api key>`. `ApiKeyAuthenticationHandler`, a standard ASP.NET Core authentication scheme, hashes the key with SHA-256 and compares it in constant time against every hash in the `MerchantAuth` configuration section, with no early exit, so response timing does not reveal how close a guess was. The matching entry's merchant id goes on the principal as a `merchant_id` claim.
- Only hashes are configured, so a leaked configuration file does not leak usable keys. A malformed hash, or no merchants at all, fails at startup rather than on the first request.
- The controller is `[Authorize]`, so a call without a valid key gets 401 from the middleware before model binding. It costs nothing at the repository or the bank, and an invalid key with a malformed body is a 401, not a Rejected.
- The controller reads the merchant id only from the claim. Nothing the caller sends can choose it; a `Merchant-Id` header, if sent, is ignored.
- Payment ownership checks use that identity: a payment is only returned to the merchant that created it, and another merchant gets 404.
- Authentication stays at the edge: the processor, repository and bank client take the merchant id as a plain input and know nothing about keys.
- Static API keys are the simplest credential that removes spoofing. Key management and OAuth client credentials are the production upgrade (8.2).

**D4. Idempotency (duplicate prevention) is out of scope.**

- In production, payment submission must be idempotent: a merchant retrying after a timeout or dropped connection must not cause a second charge.
- Doing it properly is more than a lookup: the key has to be reserved before the bank call, concurrent duplicates have to be handled, and outcomes the bank never confirmed have to be reconciled. That is a significant addition to a deliberately simple solution, so it is left out and described in 8.3.
- The consequence is accepted for this phase: every POST is a new payment attempt (Assumption 14, section 6).

**D5. Currency codes are enforced against an approved list.**

- The brief says validation must cover no more than 3 currency codes.
- There is a list of approved currencies (GBP, EUR, USD) and any other code is rejected.

**D6. Card numbers must pass the Luhn checksum.**

- The brief does not require it, but every real card number passes it, so a number that fails is a typing or integration error that the bank would decline anyway. Catching it as Rejected saves a bank call (the same reasoning as D1).
- It runs only after the length and digit checks pass, so one card number produces at most one error.
- The simulator picks its outcome by the last digit, which Luhn fixes as the check digit. The test cards were chosen so each is Luhn-valid and still ends in the digit that gives the outcome it tests.

**D7. Bank failures are not Rejected, Authorized or Declined.**

- Bank failures (bank unavailable, unexpected errors and timeouts) return an HTTP error (502 or 504), never a payment status, so a bank failure cannot be mistaken for a bank answer.
- No payment is stored, because no payment outcome is known.
- The gateway does not retry, because retrying a payment call risks a double charge.
- A timeout does not guarantee that the acquiring bank failed to process the payment. The gateway cannot determine the outcome and therefore returns 504 without automatically retrying. Without idempotency (D4), a merchant retry after a 504 may cause a second charge.
- A 503 or 400 from the bank means the bank did not process the payment, so a retry is safe.
- An unexpected bank error (a network failure after the request was sent, an unreadable 200, or an unexpected status) has an unknown outcome, as for a timeout: the bank may have charged the card.
- The merchant has to be able to tell these apart, because the first is safe to retry and the other two are not. Each gets its own message (5.2.2): "the payment was not processed" for a 503 or 400, and "the payment outcome is unknown" for an unexpected error (502) or a timeout (504). The status code stays 502 for both bank errors, because both are a bad answer from the upstream; the message carries the retry guidance. A machine-readable error code would be the next step if merchants need to branch on it in code.
- This simplified implementation does not provide payment reconciliation or recovery of ambiguous outcomes (8.3).

**D8. Rejected requests are not stored and get no id.**

- The brief says no payment could be created.
- Storing rejects would keep invalid data around for no benefit.

**D9. HTTP status mapping**

- Authorized and Declined: 200 (both are valid processing outcomes).
- Rejected: 400.
- Missing or invalid API key: 401.
- Unknown payment id, or a payment owned by another merchant: 404.
- Bank failure: 502 (not processed, or outcome unknown; the message says which) or 504 (timeout, outcome unknown), D7.
- Unexpected gateway error (an unhandled exception): 500, with the error body.

**D10. The clock is injected**

- Expiry validation depends on the current date. An injectable clock (TimeProvider) lets tests fix the date and check month boundaries deterministically.
- Comparisons use UTC.

**D11. Sensitive data handling**

- The card number and CVV exist only in the request model and the bank call.
- Only the last four digits are stored and returned.
- The merchant id is stored with each payment but is not returned by the API.
- Request bodies are not logged verbatim.
- Validation errors do not echo values.

**D12. Request/Response model handling**

- The bank request/response models are separate from the merchant-facing API models because
  - it avoids accidentally exposing bank-specific fields to the external-facing API
  - it avoids coupling the API contract to the bank's API

**D13. Amount limits**

- There is no business-enforced limit on the amount being charged beyond it being a positive 64-bit integer.

**D14. The bank's authorization code is stored with the payment.**

- The acquiring bank returns an `authorization_code` with each authorization. It is the bank's reference for the payment, so it is what links a gateway payment to the bank's records.
- It is stored on the Payment for tracing, debugging and reconciliation with the bank (8.3), and written to the payment log line. It is a bank reference, not card data, so logging it is safe (D11).
- It is not returned by the API, as the response contract does not include it (5.2.2). Exposing it to merchants would be a separate contract change.
- It is null when the bank sends none: declines carry an empty code, and failed or unreadable bank calls store no payment at all.

**D15. Each payment records when it was created.**

- `CreatedAt` is taken from the injected clock (D10) after the bank answers, so it is when the outcome became known. It is UTC and returned as ISO 8601 with an offset.
- Merchants need it for their own reconciliation and support ("when did this charge happen?"); it would also be the column a retention or reconciliation job filters on (8.3).
- It is not when the request arrived. With a synchronous call and a 10-second bank timeout the two differ by at most that timeout, so one timestamp is enough here. Once payments are reserved before the bank call (8.3) they become separate events, and the payment would record both: `RequestedAt` when the `Pending` row is written and `CompletedAt` when the outcome is known.

**D16. Logs are structured and correlated.**

- Every log call uses a message template, so fields such as `PaymentId`, `Status`, `MerchantId`, `AuthorizationCode` and `ErrorCount` are separate properties a log pipeline can query, not just text.
- The console formatter is chosen in configuration (`Logging:Console:FormatterName`): JSON by default and in the container, and the single-line "simple" formatter in Development for reading in a terminal. Timestamps are UTC.
- Scopes are included, so every JSON line carries the ASP.NET Core `TraceId`, `SpanId` and `RequestId`, and the bank call's lines share the trace id of the merchant request that caused them.
- After authentication, a small middleware puts the verified `MerchantId` on the log scope, so every line of a merchant's request (validation, the bank call, the outcome, an unhandled error) can be filtered by merchant, not just the outcome line.
- Card numbers, CVVs, request bodies and API keys are never logged (D11). Bank failures log the status code or exception type, not the exception message, which could quote request data. A test captures every log line at every level, across every path that handles card data or a key, and asserts none of them appears (section 7).

**D17. The gateway ships as a container, configured by environment, built and tested in CI.**

- A multi-stage `Dockerfile`: the .NET SDK image restores and publishes the API only, and the smaller ASP.NET runtime image runs it as the non-root `app` user on port 8080. It serves plain HTTP; TLS is terminated in front of it (Assumption 1), so HTTPS redirection is switched off when `DOTNET_RUNNING_IN_CONTAINER` is `true` (set by the .NET images) and stays on for `dotnet run`. Package restore sits on its own layer, so a code change does not re-download packages.
- `docker-compose.yml` runs the gateway next to the bank simulator. The gateway reaches the bank by service name (`Bank__BaseUrl=http://bank_simulator:8080`) and logs JSON.
- The bank base URL and timeout (`Bank:BaseUrl`, `Bank:Timeout`), the merchant key hashes and the log format are read from configuration, so each can be overridden per environment with a variable (`Bank__Timeout`, `MerchantAuth__Merchants__0__KeyHash` and so on).
- GitHub Actions (`.github/workflows/ci.yml`) runs on every push and pull request to main: restore, build in Release, start the bank simulator, run the whole test suite including the smoke tests and publish the test results. A second job then builds the image, starts it with the simulator through compose, and runs `scripts/container-smoke-test.sh` against the running container: health endpoints, an authorized payment and its retrieval, a decline, a bank-unavailable 502 and a 401. That tests the packaging, configuration and container networking, not just that the image builds.
- Deploying the image, and the configuration still held in code, are out of scope (8.7).

**D18. Payment and bank metrics, and health endpoints, without a monitoring stack.**

- Logs answer "what happened to this payment?"; they do not readily answer "is the bank getting slower?", "are declines rising?" or "how many payments have an unknown outcome?". A `PaymentGateway` meter (`System.Diagnostics.Metrics`, part of .NET) records:

| Metric | Type | Tags | Answers |
| --- | --- | --- | --- |
| `payment_gateway.payments` | Counter | `outcome` (authorized, declined, rejected); `currency` for processed payments only | Volumes, authorized share, merchant integration problems (a rejection spike) |
| `payment_gateway.bank.requests` | Counter | `result` (authorized, declined, not_processed, timed_out, unknown) | Bank error rate; `timed_out` + `unknown` is the count of payments with an unknown outcome |
| `payment_gateway.bank.duration` | Histogram (seconds) | `result` | Bank latency, and how close calls get to the timeout |

- Tags are bounded. Payment ids and merchant ids would create a time series per value, so they stay in logs. A rejected request's currency is not tagged, because it is unvalidated input.
- HTTP request counts, status codes and latencies, and unhandled exceptions, are already emitted by ASP.NET Core's built-in meters, so they are not duplicated.
- The meter is not tied to an exporter. Locally, `dotnet-counters monitor -n PaymentGateway.Api --counters PaymentGateway` shows it; exporting to a metrics backend is one registration (8.5).
- `/health/live` runs no checks: if the process answers, it is alive. `/health/ready` runs the checks tagged `ready`; there are none yet, so it means "started with valid configuration" (at least one merchant with a well-formed key hash is required at startup, D3, so a gateway that would answer every request with 401 never reports ready). Both are anonymous.
- The bank is checked by neither. A bank outage affects every gateway instance equally, so failing liveness would make the orchestrator restart healthy gateways in a loop, and failing readiness would take every instance out of rotation, turning a bank outage into a total outage instead of clear 502s. Bank health is watched through `payment_gateway.bank.requests` instead. The database check from 8.1 would be the first readiness check.

## 4. High-level Design

### 4.1 Component overview

```
                 +------------------------- Payment Gateway ------------------------+
                 |                                                                  |
Merchant ------> | Payment API --> Payment Processor --> Acquiring Bank Client -----| --> Acquiring Bank
 (HTTPS, JSON)   | (controller)      (validate,          (maps to bank format,      |    (simulator, :8080)
                 |     |            orchestrate)         handles errors/timeout)    |
                 |     |              |                                             |
                 |     |         POST |                                             |
                 |     |              v                                             |
                 |     +-- GET ---> Payment Repository (in-memory)                  |
                 +------------------------------------------------------------------+
```

### 4.2 Scenarios

**S1. Authorized or Declined (AC-1, AC-2).**
- API receives the request, Processor validates it, Bank Client sends it to the bank, the bank answers authorized true or false, Processor creates a Payment with a new GUID, the matching status and the merchant id, Repository stores it, API returns 200 with the payment details.
- Touches: API, Processor, Bank Client, Repository.

**S2. Rejected (AC-3).**
- API receives the request, Processor validation fails, API returns 400 with status Rejected and error messages. The bank and repository are not touched.
- Touches: API, Processor.

**S3. Bank unavailable or timeout (AC-4, AC-5).**
- Validation passes, Bank Client gets a 503 or 400, an unexpected status or a timeout, Processor reports a bank failure, API returns 502 or 504. No payment is stored (D7).
- Touches: API, Processor, Bank Client.

**S4. Retrieve an existing payment (AC-6).**
- API receives the id and the merchant id from the verified API key, Repository returns the payment matching both, API returns 200 with the stored details.
- Touches: API, Repository.

**S5. Retrieve an unknown or another merchant's payment (AC-7, AC-8).**
- API receives an id with no match for that merchant, returns 404.
- Touches: API, Repository.

## 5. Design

### 5.1 Entities

**Payment** represents an individual payment and its processing outcome. Persisted in the Payment Repository.

| Field | Type | Notes |
| --- | --- | --- |
| Id | Guid | Generated by the gateway |
| Status | PaymentStatus | Authorized or Declined when stored |
| CardNumberLastFour | string | Last four digits only |
| ExpiryMonth | int | 1 to 12 |
| ExpiryYear | int | Four digits |
| Currency | string | GBP, EUR or USD |
| Amount | long | Minor currency units |
| MerchantId | string | Used to associate ownership. Does not get returned by the API. |
| CreatedAt | DateTimeOffset | UTC time the bank's outcome was recorded (D15). Returned by the API. |
| AuthorizationCode | string, nullable | The bank's reference for the authorization, for tracing and reconciliation (D14). Null when the bank sends none (declines send an empty code). Does not get returned by the API. |

**PaymentStatus** has the values Authorized and Declined: the statuses a stored payment can have. Rejected is not a payment status, because a Rejected request never becomes a payment (D8); it is the fixed `status` of the Rejected response body (5.2.2).

### 5.2 Contracts

#### 5.2.1 Merchant API

Merchants submit a payment request using:

```
POST /api/payments
Content-Type: application/json
Authorization: Bearer <merchant API key>
```
where the API key identifies the merchant on the payment gateway (D3). It is required.

The request body is a JSON object with all fields required:

```json
{
  "cardNumber": "1234567890183456",
  "expiryMonth": 12,
  "expiryYear": 2030,
  "currency": "GBP",
  "amount": 1250,
  "cvv": "123"
}
```

| Field | Type | Description |
| --- | --- | --- |
| cardNumber | string | 14 to 19 numeric characters that pass the Luhn check. Sent as a string to preserve leading zeroes. |
| expiryMonth | integer | 1 to 12. |
| expiryYear | integer | Four digits. The month/year combination must be in the future, which includes the current month (Assumption 6). |
| currency | string | Three-letter upper-case code. Supported: GBP, EUR, USD. |
| amount | integer | Minor unit (1250 means GBP 12.50). Must be greater than zero. |
| cvv | string | 3 or 4 numeric characters. Sent as a string to preserve leading zeroes. |

Merchants retrieve a payment using:

```
GET /api/payments/{id}
Accept: application/json
Authorization: Bearer <merchant API key>
```

where id is the GUID returned when the payment was processed. The merchant proven by the API key must own the payment; otherwise it is reported as not found.

#### 5.2.2 Responses and status codes

Authorized or Declined (POST and GET return the same shape). Status is serialised as a string, not a number:

```json
{
  "id": "3f2b8c1e-5d4a-4e7b-9a10-2c6f8d0b1e77",
  "status": "Authorized",
  "cardNumberLastFour": "3456",
  "expiryMonth": 12,
  "expiryYear": 2030,
  "currency": "GBP",
  "amount": 1250,
  "createdAt": "2026-04-15T10:30:00+00:00"
}
```

Rejected (the status field follows from the brief; the errors list is an addition and never echoes submitted values):

```json
{
  "status": "Rejected",
  "errors": ["expiryMonth must be between 1 and 12"]
}
```

All other failures use one error shape:

```json
{
  "error": "The acquiring bank returned an unexpected response; the payment outcome is unknown"
}
```

The three bank failures have distinct messages, so a merchant can tell a safe retry from one that may charge twice (D7):

| Bank failure | HTTP | Message | Safe to retry? |
| --- | --- | --- | --- |
| Bank did not process the payment (503, 400) | 502 | The acquiring bank is unavailable; the payment was not processed | Yes |
| Unexpected bank response or network failure | 502 | The acquiring bank returned an unexpected response; the payment outcome is unknown | No: may charge twice |
| Bank timeout | 504 | The acquiring bank did not respond in time; the payment outcome is unknown | No: may charge twice |

| Situation | HTTP code | Body |
| --- | --- | --- |
| Authorized or Declined | 200 | Payment details |
| POST rejected (validation, missing fields, malformed JSON, non-JSON Content-Type) | 400 | status Rejected plus errors |
| POST or GET without a valid API key | 401 | Error body; `WWW-Authenticate: Bearer` |
| GET unknown or malformed id, or another merchant's payment | 404 | Error body |
| Bank did not process the payment, or unexpected bank error | 502 | Error body saying which, no payment details |
| Bank timeout | 504 | Error body (outcome unknown), no payment details |
| Unexpected gateway error | 500 | Error body |

#### 5.2.3 Bank contract and mapping

The simulator accepts `POST http://localhost:8080/payments` (start it with `docker-compose up`).

| Gateway field | Bank field | Mapping |
| --- | --- | --- |
| cardNumber | card\_number | As is |
| expiryMonth + expiryYear | expiry\_date | Single string MM/YYYY, month zero-padded (April 2025 becomes 04/2025) |
| currency | currency | As is |
| amount | amount | As is |
| cvv | cvv | As is |

Bank response: `authorized` (true or false) and `authorization_code`. The code is stored on the Payment (D14); an empty code, as sent with a decline, is stored as null.

| Bank behaviour | Gateway outcome |
| --- | --- |
| 200 with authorized true (card ends in 1, 3, 5, 7, 9) | Authorized |
| 200 with authorized false (card ends in 2, 4, 6, 8) | Declined |
| 503 (card ends in 0) | 502 "not processed" to merchant, no payment stored |
| 400 | Treated as a gateway defect (we validate first): logged as an error; 502 "not processed", no payment stored |
| Any other unexpected response | 502 "outcome unknown", no payment stored |
| Timeout | 504 "outcome unknown", no payment stored |

### 5.3 Components

| Component | Responsibility | Depends on | Lifetime |
| --- | --- | --- | --- |
| Payment API | Exposes POST /api/payments and GET /api/payments/{id}; takes the merchant id from the authenticated principal; maps results to HTTP codes and response bodies | Payment Processor, Payment Repository | Controller (per request) |
| Payment Processor | Coordinates validation, the bank call, handling of the bank response and payment creation | Validator, Bank Client, Repository, Clock, PaymentMetrics | Scoped |
| Acquiring Bank Client | Encapsulates communication with the bank: maps formats, applies the timeout, translates bank errors | HttpClient | Typed HttpClient |
| Payment Repository | Persists and retrieves payment records by id and merchant id (in-memory, thread-safe) | None | Singleton |

#### 5.3.1 Supporting pieces

- **PaymentRequestValidator:** pure function from a request and the current date to a list of errors. No I/O.
- **Clock (TimeProvider):** supplies the current UTC time so tests can fix the date.
- **Error handling:** one place that turns model-binding and JSON errors into the Rejected body.
- **PaymentMetrics:** the payment and bank instruments (D18), called by the processor.
- **Merchant log scope:** middleware after authentication that puts the merchant id on the log scope (D16).

### 5.4 Validation approach

- Request properties are nullable (or `required`) so a missing field is detected; a missing int would otherwise silently become 0.
- The automatic ModelState 400 is turned off so every invalid request returns the Rejected body.
- A body sent with a Content-Type other than JSON (e.g. `text/plain`) is Rejected with `Content-Type must be application/json`, rather than the framework's 415. The framework's unsupported-content-type filter is removed so the error reaches the same Rejected handling as malformed JSON.
- The card number and CVV are checked for ASCII digits 0 to 9 only. `char.IsDigit` also accepts non-ASCII digits and must not be used.
- All errors are collected and returned together; none echo the submitted value. The exception is a body that cannot be deserialised at all (malformed JSON, or a field of the wrong type such as `"amount": "abc"`): the JSON reader stops at the first such error, so the response lists only that one field, and the remaining fields are not evaluated.
- Expiry is valid if the first day of the month after the expiry month is after today (UTC), which matches Assumption 6.

## 6. Risks and Mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Merchant retries a payment (after a timeout, a dropped connection or by mistake) and is charged twice | High | Accepted for this phase: idempotency is out of scope (D4). The gateway never retries on its own, and every bank failure says whether the payment was not processed (safe to retry) or its outcome is unknown (D7). A test pins that a repeated request is a new payment. Idempotency is the fix (8.3) |
| Idempotency fingerprint (8.3, not built) cannot tell two cards apart that share the last four digits and expiry, so a key reused for a different card would replay the first payment | Low | Accepted: the fingerprint uses only fields the gateway already stores, so no value derived from the full card number is ever stored. Requires the merchant to reuse a key for a different card with the same amount, currency, last four and expiry |
| Card number, CVV or API key leaks through logs or error messages | High | Never log bodies; errors do not echo values; only last four stored; tests assert none appears in any response or in any log line at any level |
| Expiry boundary bugs (current month, December to January, time zone) | Medium | Injectable clock; UTC; boundary tests |
| Wrong translation to the bank format (MM/YYYY, padding) | Medium | Mapping in one place; unit tests; smoke tests against the simulator |
| Default model binding bypasses the Rejected body or turns missing fields into 0 | Medium | Nullable fields; automatic 400 off; malformed-JSON test |
| Amount overflow or float handling | Medium | 64-bit integer minor units; non-integers and overflow rejected |
| Payment id enumeration or reading another merchant's payment | Medium | GUID ids; merchant id from a verified API key, not a header; ownership check by filtering retrieval of payments by merchant id and payment id; 404 rather than 403 |
| Stolen or guessed API key | Medium | Keys stored only as SHA-256 hashes; constant-time comparison; keys never logged; HTTPS only (Assumption 1). Rotation and revocation are a next step (8.2) |
| Payments left with an unknown outcome after a timeout or unexpected bank error | Medium | The error tells the merchant the outcome is unknown; `payment_gateway.bank.requests` counts them by result (D18); reconciliation is a next step (8.3) |
| Non-ASCII digits accepted as numeric | Low | ASCII-only check and a test |
| In-memory repository loses data or races | Low (brief allows it) | ConcurrentDictionary, singleton, behind an interface so it can be swapped; a concurrent-access test |
| Bank outage restarts or drains healthy gateways | Medium | Health endpoints never check the bank (D18) |
| Bank contract changes | Low | One client class; contract tests against the simulator |

## 7. Tests

### 7.1 Strategy

| Level | What | Test doubles |
| --- | --- | --- |
| Unit: validator | Every boundary in section 2.2 | None (pure) |
| Unit: bank client | Request mapping, authorized, declined, 400, 503, timeout | Stub HttpMessageHandler |
| Unit: processor | Status mapping; bank not called on reject; no payment stored on reject or bank failure; distinct bank failure messages; id, merchant id and last four correct; a repeated request is a new payment (section 6); metrics recorded with the right, bounded tags (D18) | Fake bank client, in-memory repository, fixed clock, meter listener |
| Unit: repository | Store and retrieve; lookup by another merchant id returns nothing; concurrent adds and gets from many threads lose nothing and keep merchants apart; concurrent duplicate adds store one | None |
| API (WebApplicationFactory) | Status codes, JSON shape and casing, string enum, Rejected body for malformed JSON and a non-JSON Content-Type, 401 for a missing or invalid API key (with the bank not called, even with a malformed body), 404, error body shape including the 500 path, no full card number in responses, merchant A cannot retrieve merchant B's payment, health endpoints, startup fails without a configured merchant, the OpenAPI document carries the XML doc comments | Fake bank client |
| API: logs | Every log line, captured at Trace level across every category, contains no card number, CVV or API key on any path (authorized, declined, 502, 504, 500, both kinds of Rejected, 401); payment log lines carry the trace id, and every gateway log line after authentication (including the unhandled-error line) carries the merchant id | Fake bank client, capturing logger provider |
| Smoke: bank client | Odd, even and zero card endings against the real simulator. Optional locally; always run in CI (D17) | Simulator via docker-compose |
| Smoke: container | The built image, running under compose next to the simulator, exercised over HTTP by `scripts/container-smoke-test.sh`. Run in CI (D17) | None: the real container and simulator |

### 7.2 Traceability

| Acceptance criterion | Covered by |
| --- | --- |
| AC-1, AC-2 | Processor tests; API tests; smoke tests; container smoke test |
| AC-3 | Validator tests; API tests |
| AC-4, AC-5 | Bank client tests; Processor tests; API tests; container smoke test (503) |
| AC-6, AC-7 | API tests; repository tests |
| AC-8 | API tests; repository tests |
| AC-9 | API tests (assert no full card number or CVV in any body) |
| AC-10 | API tests |
| Risk: double charge on a repeated request (section 6) | Processor tests |
| 2.3 Security: card data and keys never logged | API log tests |
| 2.3 Security: only the last four digits stored | Processor test on the stored record's fields |
| 2.3 Operability: correlation, metrics, health | API log tests; processor metric tests; API health tests |
| 5.3 Repository is thread-safe | Repository concurrency tests |
| D17 Packaged container works | Container smoke test |

### 7.3 Deliberately not tested

ASP.NET framework behaviour, the simulator itself, and performance or load.

## 8. Out of Scope and Next Steps

This section lists only what the solution does not have. For each item it says why it was left out and how I would add it. What the solution does have is in the decision log (section 3.3).

Where a missing piece has an obvious home in the code, that point carries a tagged comment so it can be found with a search: `// persistence:`, `// idempotency:`, `// telemetry:`, `// resilience:`, `// rate-limiting:` and `// config:`.

The code comments point heavily at this document (section numbers, decision ids, acceptance criteria), so a reviewer can trace each piece of code to its reasoning. In production that would drift as the design changed, so I would keep only decision-id references (D1, D7 and so on), with the decision log kept append-only like Architecture Decision Records: a changed decision is marked superseded by a new one, never edited or renumbered. Section-number and acceptance-criteria references would come out of the production code (they would stay in the tests), and the tagged markers above would become tickets.

| Item | Why it is out of scope | Code marker |
| --- | --- | --- |
| 8.1 Durable persistence | The brief allows in-memory storage | `// persistence:` |
| 8.2 API key management and OAuth | Static keys are enough to remove spoofing | None |
| 8.3 Idempotency and reconciliation | Doing it safely outweighs the rest of the solution | `// idempotency:` |
| 8.4 Scheme-aware card checks | The brief's rules plus Luhn already stop malformed numbers | None |
| 8.5 Telemetry export, dashboards and alerting | Metrics and logs are emitted; shipping them needs infrastructure the exercise does not have | `// telemetry:` |
| 8.6 Circuit breaking, retries and rate limiting | Safe retries depend on idempotency | `// resilience:`, `// rate-limiting:` |
| 8.7 Deployment pipeline and remaining configuration | There is no environment to deploy to | `// config:` |
| 8.8 Other payment features | The brief covers only processing and retrieving one payment | None |

### 8.1 Durable Persistence
Payments are held in memory (Assumption 10), which has two drawbacks:
1. They are lost if the node goes down, the hosting process is killed or the app pool recycles.
2. The gateway can't be scaled out, because each instance would only see its own payments.

To add it, I would put a relational database such as PostgreSQL behind the existing `IPaymentsRepository` interface, with a primary key on `Id` and an index on `(MerchantId, Id)` for the GET lookup, so payment writes are durable, transactional and shared by every gateway instance. The interface is synchronous today because an in-memory store never waits; a database would make `Add` and `Get` async, so the processor and controller would change to await them. Nothing else would change. The same table would also hold the idempotency keys (8.3).

### 8.2 API Key Management and OAuth
Merchants are authenticated with static API keys whose hashes are held in configuration (D3). There is no way to issue, rotate or revoke a key without changing configuration and restarting, and no alternative to a long-lived shared secret.

To add it, I would:
1. Hold the key hashes in a secret store or the merchants database rather than configuration, so keys can be issued and revoked without a deployment.
2. Allow two active keys per merchant, so a merchant can rotate to a new key before the old one is revoked.
3. Offer OAuth 2.0 client credentials to larger merchants: short-lived signed JWTs from an identity provider, validated with `AddJwtBearer`. The controller would not change, because it only reads the `merchant_id` claim.
4. Rate limit per merchant (8.6), which also slows down key guessing.

### 8.3 Idempotency and Reconciliation
The solution does not prevent duplicate payments. Every POST is a new payment attempt, so a merchant that retries after a timeout, a dropped connection or a bug can charge a shopper twice (D4, Assumption 14). It is left out because doing it correctly (reserving keys before the bank call, handling concurrent duplicates, and resolving payments whose outcome the bank never confirmed) would add more moving parts than the rest of the solution put together. Until then the gateway never retries on its own, and tells the merchant when a payment's outcome is unknown (D7).

To add it, I would:
1. Require an `Idempotency-Key` header on POST: the merchant's own identifier for the payment, such as an order or invoice number. Keys are scoped per merchant. The card number cannot be used: the same shopper can legitimately make the same payment twice, and a PAN as a lookup key spreads cardholder data (PCI DSS).
2. Reserve the key before the bank call, by inserting the payment with a `Pending` status into the payments database, with a unique constraint on `(merchant_id, idempotency_key)`. The constraint makes the reservation atomic across gateway instances, and a concurrent duplicate gets a conflict instead of reaching the bank. Recording the key only after the bank answers would not prevent a double charge.
3. Store a fingerprint of the request (amount, currency, expiry and last four digits, never the full card number or CVV) to tell a genuine retry from a reused key. This is deliberately not unique to a card: two different cards with the same last four digits and expiry, sent with the same key, amount and currency, would be treated as a retry and the first payment replayed. That risk is accepted (section 6), because the alternative is storing something derived from the full card number, and keeping card data out of storage matters more. The card number is only held in memory for the length of the request (D11).
4. For a repeated key: replay the stored payment (200) for the same request, return 409 while the first attempt is still `Pending`, and 422 if the key was reused for a different request.
5. When the bank answers, update that one row to `Authorized` or `Declined`. If the bank did not process the payment (503 or 400), delete the `Pending` row so the merchant can retry with the same key. On a timeout or unexpected error, mark the row as outcome unknown, which keeps the key locked so a retry cannot charge the card again. This replaces "no payment is stored" in D7.
6. Keep the merchant API unchanged: `GET /api/payments/{id}` only returns `Authorized` or `Declined` payments, and returns 404 for a payment that is still `Pending` or has an unknown outcome.
7. Reconcile payments left `Pending` or with an unknown outcome. Because the payment id exists before the bank call, it can be sent to the bank as a reference, and a background job can ask the bank for the outcome and complete or release the payment. The stored authorization code (D14) and `CreatedAt` (D15) support this.
8. Keep keys for a fixed retention period (e.g. 24 hours), after which the same key is treated as a new payment.

Putting the key on the payment row means every write touches one row, so there is no gap between storing the payment and completing the key. The `// idempotency:` comments in the controller, processor and repository mark where these steps would go.

### 8.4 Scheme-Aware Card Checks
The gateway checks a card number's length, digits and Luhn checksum (D6), but not whether a card scheme actually issues it. A number can pass every check and still be one no bank will recognise, which costs a bank call to find out.

To add it, I would run these before the bank call too:
1. Check the card number against BIN (issuer prefix) ranges, to reject numbers no scheme issues and to route by scheme.
2. Validate the length per scheme (for example, American Express is always 15 digits) and the CVV length per scheme (4 for American Express, 3 otherwise).

### 8.5 Telemetry Export, Dashboards and Alerting
The gateway emits structured, correlated logs (D16), payment and bank metrics, and health endpoints (D18). What it does not do is ship any of it anywhere: metrics are only visible to a local listener such as `dotnet-counters`, trace ids are logged but spans are not exported, and nothing alerts. That needs a collector and a backend, which the exercise does not have.

To add it, I would (steps 1 and 2 are marked `// telemetry:` in the code; probes, dashboards and alerts live outside the code):
1. Register OpenTelemetry with an OTLP exporter for the `PaymentGateway` meter, ASP.NET Core's built-in meters and the HttpClient meters.
2. Export distributed traces (ASP.NET Core and HttpClient instrumentation), with the trace context propagated to the bank, a `bank.authorize` span around the call, the merchant id as a span attribute, and the request body never captured.
3. Point the orchestrator's liveness and readiness probes at `/health/live` and `/health/ready`, and the load balancer at readiness.
4. Build dashboards and alerts on the D18 metrics, with runbooks for the common failures: bank `not_processed` rate (bank unavailable), `timed_out` + `unknown` rate (payments needing reconciliation), bank latency approaching the timeout, a drop in the authorized share, and any 500s.

### 8.6 Circuit Breaking, Retries and Rate Limiting
Every bank call has a timeout and a clear failure mapping (D7), but the gateway keeps calling a bank that is failing, never retries, and accepts any volume of traffic. Retries are left out on purpose: without idempotency (8.3), retrying a call the bank may have processed risks a double charge.

To add it, I would (marked `// resilience:` and `// rate-limiting:`):
1. Add a circuit breaker to the bank client to detect persistent timeouts or 503s and fail fast. An open circuit would map to "not processed" (502), because the bank never saw the request.
2. Once idempotency is in place, retry automatically only where the bank definitely did not process the payment (503), never after a timeout.
3. Rate limit ingress with a token bucket per verified merchant id, registered after authentication, to protect the gateway and the bank from traffic spikes, abuse and denial of service.

### 8.7 Deployment Pipeline and Remaining Configuration
The gateway is packaged, configured by environment and built and tested in CI (D17), but nothing deploys it, and some settings and checks are still missing.

To add it, I would:
1. Move the supported currencies from code into configuration (marked `// config:`).
2. Push the image to a registry from CI, and deploy each environment from that one image, running as Production with merchant key hashes from a secret store (8.2). Under compose the container runs in Development, for Swagger and the development merchant keys.
3. Check formatting in CI with `dotnet format --verify-no-changes`. It fails today on line endings, because the template's `.editorconfig` asks for CRLF and the files use LF; fixing that means rewriting the line endings of nearly every file, so it would be its own change.

### 8.8 Other Payment Features
The brief covers only processing and retrieving a single payment. A production gateway would also need:
1. Two-step payments (authorize now, capture later), voids and refunds. These would be new operations on an existing payment (for example `POST /api/payments/{id}/captures`) with their own statuses, each sent to the bank.
2. Listing and searching a merchant's payments (by date, status or amount), with pagination. `CreatedAt` (D15) and the `(MerchantId, Id)` index (8.1) are the starting point.
3. More than one acquiring bank, with routing by currency or card scheme behind `IAcquiringBankClient`.
4. Notifications to the merchant (webhooks) when an outcome changes, which reconciliation (8.3) would need.
5. Full PCI DSS scope. Card data is only held in memory for the length of a request here (D11), but a production gateway would also need tokenisation, network segmentation and audited key management.

## 9. How to Run

From the repository root:

- **Everything in Docker:** `docker compose up --build` starts the simulator and the gateway; the gateway listens on http://localhost:5080.
- **Gateway from source:**
  1. Start the bank simulator: `docker compose up -d bank_simulator` (listens on http://localhost:8080).
  2. Run the API: `dotnet run --project src/PaymentGateway.Api`. It serves https://localhost:7092; http://localhost:5067 only redirects there. The bank base URL is read from configuration (`Bank:BaseUrl`).
- **Tests:** `dotnet test`. Set `BANK_SIMULATOR_URL=http://localhost:8080` to include the smoke tests against the simulator (CI always does).
- **Container smoke test:** `docker compose up -d --build`, then `scripts/container-smoke-test.sh` (needs `curl` and `jq`; CI runs it).
- **Health and metrics:** `GET /health/live` and `GET /health/ready`; `dotnet-counters monitor -n PaymentGateway.Api --counters PaymentGateway` for the metrics of a gateway run from source.

See the README for the API reference and examples.
