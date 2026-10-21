# Stripe Checkout Example

A small, standalone Node.js example of one-time payments with Stripe Checkout.
It uses the official Stripe SDK and SQLite for a durable application receipt ledger.
It is **test-mode only**, not a production storefront.

## What it demonstrates

- Server-side Checkout Session creation with a fixed Stripe Price ID.
- Idempotency keys for safe retries of the same checkout request.
- Webhook signature verification against the original request bytes.
- Payment confirmation from signed webhooks, never from a browser redirect.
- Handling of delayed payment success.
- Transactional event deduplication and one receipt per Checkout Session.
- Tests that run locally without credentials or requests to Stripe.

## Requirements

- Node.js 24 or newer (the built-in SQLite module may emit an experimental warning).
- A Stripe test account or sandbox for the interactive Checkout flow.
- Stripe CLI for forwarding webhooks during local development.

## Run locally

```sh
npm install
cp .env.example .env
```

Create a **one-time price** in Stripe test mode. Edit `.env`:

| Variable | Purpose |
| --- | --- |
| `STRIPE_SECRET_KEY` | Test secret key, starting with `sk_test_` |
| `STRIPE_PRICE_ID` | Existing test-mode one-time `price_...` identifier |
| `STRIPE_WEBHOOK_SECRET` | Signing secret printed by the CLI listener below |
| `APP_URL` | `http://localhost:3000` for local use |
| `PORT` | HTTP port; defaults to `3000` |
| `DATABASE_PATH` | SQLite file; defaults to `./data/payments.db` |

Start forwarding webhooks in a separate terminal:

```sh
stripe login
stripe listen \
  --events checkout.session.completed,checkout.session.async_payment_succeeded \
  --forward-to localhost:3000/webhook
```

Copy that listener's `whsec_...` secret into `.env`, then start the server:

```sh
npm start
```

Create a checkout:

```sh
curl -X POST http://localhost:3000/checkout \
  -H "Idempotency-Key: example_order_00000001"
```

Open the returned `url` in a browser. Use Stripe's test card `4242 4242 4242 4242`,
a future expiry date and any valid test CVC. Never enter real card details.
See [Stripe's testing documentation](https://docs.stripe.com/testing).

Reuse the same idempotency key when retrying the same request. Use a new key for
a new purchase. Stripe retains idempotency results for a limited period; this
example is not a permanent application order system.

After a paid webhook, a receipt is recorded in `data/payments.db`. Opening
`/success` does not create a receipt or prove payment.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/health` | Process health |
| POST | `/checkout` | Create a test Checkout Session; returns its URL |
| POST | `/webhook` | Verify and process signed Stripe events |
| GET | `/success` | Informational return response |
| GET | `/cancel` | Informational cancellation response |

The return routes deliberately serve JSON. This is a backend code example,
not a designed checkout page; the payment form itself is hosted by Stripe.

## Tests

```sh
npm test
```

Tests use SDK-generated webhook signatures, an isolated local SQLite fixture
and a stubbed Checkout client. They cover invalid signatures, tampered bodies,
unpaid sessions, duplicate events, duplicate session receipts, persistence
across reopening the database and server-controlled prices.

No real Stripe API requests or charges are made by these tests.

## Flow

```text
Client -> POST /checkout -> Stripe Checkout Session
Client -> Stripe-hosted checkout
Stripe -> signed POST /webhook -> verify -> transaction -> receipt
Client -> /success (informational only)
```

## Scope and limitations

This example records payment receipts; it does not deliver goods, grant access,
handle refunds, manage subscriptions, calculate tax or track fulfilment.
There is no authentication, browser UI, rate limiting or production order model.
Do not expose it as a public production payment service.

Before adapting it for production, add authenticated order ownership,
server-generated purchase identifiers, catalog validation, abuse controls and
monitoring. Bind every accepted payment to a known order and verify its expected
price, amount, currency and environment before granting access. Shared Stripe
accounts may emit events belonging to other applications; this example does not
filter those by application order metadata.

Use a durable shared database for multiple instances. Put fulfilment work in a
transactional outbox/worker and make downstream side effects idempotent too.
Handle refunds, disputes and delayed-payment failures according to your product.
Test retries and recovery before enabling live payments. The startup guard
intentionally rejects live keys.

Never commit `.env`, API keys, webhook secrets or the local receipt database.
