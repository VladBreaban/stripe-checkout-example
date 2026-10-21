# Stripe Checkout — ASP.NET Core Example

A standalone **C# / .NET 10** backend example using ASP.NET Core Minimal APIs,
the official **Stripe.net** SDK and SQLite. Test payments only; live keys are
rejected at startup. No JavaScript or Node.js is required.

## Features

- One-time Stripe Checkout Sessions with server-controlled prices and redirects.
- Request idempotency keys for safe checkout retries.
- Signed webhook verification over the original request body.
- Paid and delayed-payment-success events.
- Transactional, durable receipts with event and session deduplication.
- xUnit unit and HTTP integration tests with no real Stripe API calls.

## Run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and the [Stripe CLI](https://docs.stripe.com/stripe-cli).
Create a one-time price in your Stripe sandbox/test environment.

```sh
dotnet restore StripeCheckout.slnx
dotnet user-secrets set "Stripe:SecretKey" "sk_test_YOUR_KEY" --project src/StripeCheckout.Api
dotnet user-secrets set "Stripe:PriceId" "price_YOUR_PRICE" --project src/StripeCheckout.Api
dotnet user-secrets set "Stripe:AppUrl" "http://localhost:3000" --project src/StripeCheckout.Api
```

In a separate terminal:

```sh
stripe login
stripe listen --events checkout.session.completed,checkout.session.async_payment_succeeded \
  --forward-to localhost:3000/webhook
```

Use the signing secret printed by that listener:

```sh
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_YOUR_LISTENER_SECRET" --project src/StripeCheckout.Api
dotnet run --project src/StripeCheckout.Api --no-launch-profile -- \
  --environment Development --urls http://localhost:3000
```

User-secrets are loaded in the Development environment and stay outside the repo.
For other environments, supply `Stripe__SecretKey`, `Stripe__WebhookSecret`,
`Stripe__PriceId` and `Stripe__AppUrl` as environment variables.
Optional `DatabasePath` defaults to `data/payments.db` under the API project.
Do not put real keys in checked-in settings files.

Create a test Checkout Session:

```sh
curl -X POST http://localhost:3000/checkout \
  -H "Idempotency-Key: example_purchase_00001"
```

Open the returned `url` and pay using Stripe's test card `4242 4242 4242 4242`,
a future expiry date and a test CVC. Never use a real card.
Reuse the idempotency key for retries of the same request; use a new key for a
new purchase. Stripe's idempotency retention is limited, so this is not a
permanent order system.

The signed paid webhook writes the receipt. Visiting `/success` never confirms
payment or changes the database. Return pages serve JSON; the payment form is
hosted by Stripe. This repository is a backend example, not a frontend mockup.

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/health` | Health check |
| POST | `/checkout` | Create a test Checkout Session |
| POST | `/webhook` | Verify and process signed events |
| GET | `/success` | Informational checkout return |
| GET | `/cancel` | Informational cancellation return |

For a registered webhook endpoint, configure the Stripe API version expected by
the installed SDK. Version mismatches are rejected, not silently ignored.

## Tests

```sh
dotnet test StripeCheckout.slnx
```

Tests use ASP.NET Core's in-process test server, isolated temporary SQLite files,
a stubbed Checkout gateway and HMAC-signed webhook fixtures. No API keys,
Stripe account or network calls to Stripe are needed for the tests.

## Code map

- `Program.cs`: dependency injection, configuration validation and HTTP routes.
- `Payments.cs`: Stripe Checkout service and typed configuration.
- `ReceiptLedger.cs`: transactional event/receipt persistence.
- `PaymentsTests.cs`: unit tests and HTTP integration tests.

## Before adapting for production

This example records receipts, not fulfilment or access grants. It intentionally
has no authentication, tax calculation, subscriptions, refunds or dispute logic.
Do not expose it as a production payment service.

Add authenticated order ownership, server-generated purchase IDs, rate limiting,
monitoring and validation of each payment against the expected order, amount,
currency, price and environment. If other apps share the Stripe account, filter
events by application/order metadata before acting on them.

Use a shared durable database for multiple instances and a transactional outbox
for fulfilment. Make downstream side effects idempotent. Handle failed delayed
payments, refunds and disputes according to your business rules. Test recovery
and retries before enabling live mode. Keep the live-key guard until that work
is complete.
