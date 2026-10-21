import { createServer } from 'node:http';
import { createCheckout, recordPayment } from './payments.js';

function json(res, status, data) {
  res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(data));
}

async function rawBody(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > 1024 * 1024) throw new Error('Body too large');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

export function createApp({ stripe, config, db }) {
  return createServer(async (req, res) => {
    try {
      const path = new URL(req.url, 'http://localhost').pathname;
      if (req.method === 'GET' && path === '/health') {
        return json(res, 200, { ok: true });
      }
      if (req.method === 'POST' && path === '/webhook') {
        // Signature verification MUST receive the original bytes, not parsed JSON.
        let event;
        try {
          const payload = await rawBody(req);
          const signature = req.headers['stripe-signature'];
          if (typeof signature !== 'string') throw new Error('Missing signature');
          event = stripe.webhooks.constructEvent(payload, signature, config.webhookSecret);
        } catch {
          return json(res, 400, { error: 'Invalid webhook signature or payload' });
        }
        // A storage failure returns 500 so Stripe retries. Never acknowledge first.
        recordPayment(db, event);
        return json(res, 200, { received: true });
      }
      if (req.method === 'POST' && path === '/checkout') {
        const key = req.headers['idempotency-key'];
        if (typeof key !== 'string' || !/^[a-zA-Z0-9_-]{16,100}$/.test(key)) {
          return json(res, 400, { error: 'A valid Idempotency-Key header is required' });
        }
        // The browser cannot choose the amount, currency or redirect destination.
        const session = await createCheckout(stripe, config, key);
        return json(res, 200, { url: session.url });
      }
      if (req.method === 'GET' && path === '/success') {
        return json(res, 200, {
          message: 'Checkout returned successfully. Payment is confirmed only by the signed webhook.',
        });
      }
      if (req.method === 'GET' && path === '/cancel') {
        return json(res, 200, { message: 'Checkout cancelled. You can try again.' });
      }
      return json(res, 404, { error: 'Not found' });
    } catch {
      // Do not expose SDK errors, payloads or credentials to the browser or logs.
      console.error('Request failed; inspect Stripe events and database availability.');
      if (!res.headersSent) json(res, 500, { error: 'Request could not be processed' });
      else res.end();
    }
  });
}
