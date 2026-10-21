import test from 'node:test';
import assert from 'node:assert/strict';
import Stripe from 'stripe';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCheckout, openLedger, recordPayment } from '../src/payments.js';
import { createApp } from '../src/app.js';

const paidEvent = (id = 'evt_example', type = 'checkout.session.completed') => ({
  id, type, data: { object: {
    id: 'cs_example', mode: 'payment', payment_status: 'paid',
    amount_total: 1999, currency: 'eur',
  } },
});

test('checkout uses a server-controlled price and a stable retry key', async () => {
  let captured;
  const stripe = { checkout: { sessions: { create: async (...args) => {
    captured = args; return { url: 'https://checkout.stripe.com/example' };
  } } } };
  await createCheckout(stripe, { priceId: 'price_test', appUrl: 'http://localhost:3000' }, 'order_123456789012345');
  assert.deepEqual(captured[0].line_items, [{ price: 'price_test', quantity: 1 }]);
  assert.equal(captured[1].idempotencyKey, 'checkout:order_123456789012345');
  await assert.rejects(createCheckout(stripe, {}, 'bad'));
});

test('unpaid sessions are ignored; duplicate events and sessions produce one durable receipt', () => {
  const directory = mkdtempSync(join(tmpdir(), 'stripe-example-'));
  const path = join(directory, 'payments.db');
  let db = openLedger(path);
  try {
    const unpaid = paidEvent();
    unpaid.data.object.payment_status = 'unpaid';
    assert.equal(recordPayment(db, unpaid).ignored, true);
    recordPayment(db, paidEvent());
    assert.equal(recordPayment(db, paidEvent()).duplicate, true);
    recordPayment(db, paidEvent('evt_other', 'checkout.session.async_payment_succeeded'));
    db.close();
    db = openLedger(path);
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM payment_receipts').get().n, 1);
  } finally { db.close(); rmSync(directory, { recursive: true }); }
});

test('HTTP webhook verifies real SDK signatures; redirect does not confirm payment', async () => {
  const db = openLedger(':memory:'); // Isolated test fixture only.
  const stripe = new Stripe('sk_test_fixture_only');
  const config = { webhookSecret: 'whsec_fixture_only' };
  const server = createApp({ stripe, config, db });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    assert.equal((await fetch(`${base}/success`)).status, 200);
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM payment_receipts').get().n, 0);
    const payload = JSON.stringify(paidEvent());
    assert.equal((await fetch(`${base}/webhook`, { method: 'POST', body: payload })).status, 400);
    const signature = stripe.webhooks.generateTestHeaderString({ payload, secret: config.webhookSecret });
    const send = body => fetch(`${base}/webhook`, {
      method: 'POST', body, headers: { 'stripe-signature': signature },
    });
    assert.equal((await send(payload + ' ')).status, 400);
    assert.equal((await send(payload)).status, 200);
    assert.equal((await send(payload)).status, 200);
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM payment_receipts').get().n, 1);
    assert.equal((await fetch(`${base}/checkout`, { method: 'POST' })).status, 400);
  } finally {
    await new Promise(resolve => server.close(resolve));
    db.close();
  }
});
