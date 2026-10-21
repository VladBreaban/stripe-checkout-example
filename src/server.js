import Stripe from 'stripe';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { openLedger } from './payments.js';
import { createApp } from './app.js';

for (const name of ['STRIPE_SECRET_KEY', 'STRIPE_WEBHOOK_SECRET', 'STRIPE_PRICE_ID', 'APP_URL']) {
  if (!process.env[name]) throw new Error(`Missing ${name}`);
}
// Deliberately test-only: this educational example must not charge live cards.
if (!process.env.STRIPE_SECRET_KEY.startsWith('sk_test_')) {
  throw new Error('Use a Stripe test-mode secret key');
}
const url = new URL(process.env.APP_URL);
if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password ||
    url.pathname !== '/' || url.search || url.hash) {
  throw new Error('APP_URL must be a plain HTTP(S) origin');
}
if (url.protocol === 'http:' && !['localhost', '127.0.0.1'].includes(url.hostname)) {
  throw new Error('Use HTTPS outside localhost');
}
const config = {
  priceId: process.env.STRIPE_PRICE_ID,
  webhookSecret: process.env.STRIPE_WEBHOOK_SECRET,
  appUrl: url.origin,
};
const dbPath = process.env.DATABASE_PATH || './data/payments.db';
mkdirSync(dirname(dbPath), { recursive: true });
const db = openLedger(dbPath);
const stripe = new Stripe(process.env.STRIPE_SECRET_KEY, { maxNetworkRetries: 2 });
const server = createApp({ stripe, config, db });
server.listen(Number(process.env.PORT || 3000), '0.0.0.0', () => {
  console.log('Stripe Checkout example listening');
});
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => { db.close(); process.exit(0); }));
}
