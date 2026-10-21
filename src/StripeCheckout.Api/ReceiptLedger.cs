using Microsoft.Data.Sqlite;
using Stripe;
using Stripe.Checkout;

namespace StripeCheckout.Api;

// An application receipt ledger, not a replica of Stripe's catalog.
public sealed class ReceiptLedger
{
    private readonly string connectionString;

    public ReceiptLedger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder {
            DataSource = path, Pooling = false, DefaultTimeout = 10,
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS processed_events (event_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS payment_receipts (
                session_id TEXT PRIMARY KEY,
                amount_total INTEGER NOT NULL,
                currency TEXT NOT NULL,
                recorded_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public bool Record(Event stripeEvent)
    {
        if (stripeEvent.Type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded"))
            return false;
        if (stripeEvent.Livemode || stripeEvent.Data.Object is not Session session ||
            session.Mode != "payment" || session.PaymentStatus != "paid")
            return false;
        if (string.IsNullOrWhiteSpace(stripeEvent.Id) || string.IsNullOrWhiteSpace(session.Id) ||
            session.AmountTotal is null or < 0 || string.IsNullOrWhiteSpace(session.Currency))
            throw new InvalidOperationException("Invalid paid session.");

        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO processed_events(event_id) VALUES ($event)";
        command.Parameters.AddWithValue("$event", stripeEvent.Id);
        var isNew = command.ExecuteNonQuery() != 0;
        if (isNew)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT OR IGNORE INTO payment_receipts(session_id, amount_total, currency)
                VALUES ($session, $amount, $currency)
                """;
            command.Parameters.AddWithValue("$session", session.Id);
            command.Parameters.AddWithValue("$amount", session.AmountTotal.Value);
            command.Parameters.AddWithValue("$currency", session.Currency);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return isNew;
    }

    public long CountReceipts()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM payment_receipts";
        return (long)command.ExecuteScalar()!;
    }
}
