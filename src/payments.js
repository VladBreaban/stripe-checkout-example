import { DatabaseSync } from 'node:sqlite';

// This is an application receipt ledger, not a copy of the Stripe catalog.
export function openLedger(path) {
  const db = new DatabaseSync(path);
  db.exec(`
    PRAGMA journal_mode = WAL;
    CREATE TABLE IF NOT EXISTS processed_events (
      event_id TEXT PRIMARY KEY
    );
    CREATE TABLE IF NOT EXISTS payment_receipts (
      session_id TEXT PRIMARY KEY,
      amount_total INTEGER NOT NULL,
      currency TEXT NOT NULL,
      recorded_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
  `);
  return db;
}

export function recordPayment(db, event) {
  const supported = [
    'checkout.session.completed',
    'checkout.session.async_payment_succeeded',
  ];
  if (!supported.includes(event.type)) return { ignored: true };
  const session = event.data.object;
  // Checkout completion alone does not prove a delayed payment has settled.
  if (session.mode !== 'payment' || session.payment_status !== 'paid') {
    return { ignored: true };
  }
  if (!session.id || !Number.isSafeInteger(session.amount_total) ||
      session.amount_total < 0 || typeof session.currency !== 'string') {
    throw new Error('Invalid paid session');
  }
  db.exec('BEGIN IMMEDIATE');
  try {
    const result = db.prepare(
      'INSERT OR IGNORE INTO processed_events (event_id) VALUES (?)',
    ).run(event.id);
    if (result.changes) {
      db.prepare(`
        INSERT OR IGNORE INTO payment_receipts
          (session_id, amount_total, currency) VALUES (?, ?, ?)
      `).run(session.id, session.amount_total, session.currency);
    }
    db.exec('COMMIT');
    return { duplicate: result.changes === 0 };
  } catch (error) {
    db.exec('ROLLBACK');
    throw error;
  }
}

export async function createCheckout(stripe, config, idempotencyKey) {
  if (!/^[a-zA-Z0-9_-]{16,100}$/.test(idempotencyKey ?? '')) {
    throw new Error('Provide an Idempotency-Key of 16–100 letters, digits, underscores or hyphens');
  }
  return stripe.checkout.sessions.create({
    mode: 'payment',
    line_items: [{ price: config.priceId, quantity: 1 }],
    success_url: `${config.appUrl}/success`,
    cancel_url: `${config.appUrl}/cancel`,
  }, { idempotencyKey: `checkout:${idempotencyKey}` });
}
