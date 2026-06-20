namespace Sluice.Barkd;

/// <summary>A BOLT11 invoice created on the bark wallet (barkd <c>POST /api/v1/lightning/receives/invoice</c>).</summary>
public sealed record BarkdInvoice(string Invoice, string PaymentHash);

/// <summary>Settlement view of a lightning receive (barkd <c>GET /api/v1/lightning/receives/{id}</c>).</summary>
public sealed record BarkdReceiveStatus(bool Found, bool Settled, DateTime? FinishedAt);

/// <summary>Wallet info/balance for the admin surface.</summary>
public sealed record BarkdWalletInfo(bool Reachable, long SpendableSat, long PendingLightningSendSat, long PendingExitSat, string? Raw);

/// <summary>Result of a lightning send (barkd <c>POST /api/v1/lightning/pay</c> → only a success message,
/// no preimage). <see cref="Message"/> is barkd's success text.</summary>
public sealed record BarkdPayResult(string Message);

/// <summary>A settled lightning movement (one leg of barkd <c>GET /api/v1/wallet/movements</c>, filtered to
/// the <c>bark.lightning_receive</c>/<c>bark.lightning_send</c> subsystems). <see cref="Direction"/> is
/// "incoming" or "outgoing"; amounts in sat; <see cref="PaymentHash"/> is derived from the BOLT11.</summary>
public sealed record BarkdMovement(
    long Id, string Direction, long AmountSat, long FeeSat,
    string? Invoice, string? PaymentHash, DateTime? CreatedAt, DateTime? SettledAt, string Status);

/// <summary>
/// Typed client for barkd — bark's official REST daemon (bearer-auth, cluster-internal). The wallet
/// runtime lives in the barkd pod; this client only creates invoices, polls settlement, and reads balance.
/// All calls throw <see cref="BarkdException"/> on transport/HTTP errors (callers decide retry semantics).
/// </summary>
public interface IBarkdClient
{
    bool IsConfigured { get; }

    Task<BarkdInvoice> CreateInvoiceAsync(long amountSat, string description, CancellationToken ct = default);

    Task<BarkdReceiveStatus> GetReceiveStatusAsync(string paymentHash, CancellationToken ct = default);

    Task<BarkdWalletInfo> GetWalletInfoAsync(CancellationToken ct = default);

    /// <summary>Pay a BOLT11 invoice / offer / lightning address. <paramref name="amountSat"/> is required
    /// only for amountless invoices (else null — barkd reads the amount from the invoice). Throws
    /// <see cref="BarkdException"/> on any non-success (the caller treats that as a failed payment).</summary>
    Task<BarkdPayResult> PayInvoiceAsync(string destination, long? amountSat, string? comment, CancellationToken ct = default);

    /// <summary>Settled LIGHTNING movements (receives + sends), newest-first, for NWC list_transactions.
    /// Filters barkd's generic movement log to the two lightning subsystems and ignores rounds/boards.</summary>
    Task<IReadOnlyList<BarkdMovement>> ListLightningMovementsAsync(int max, CancellationToken ct = default);
}

public sealed class BarkdException : Exception
{
    public BarkdException(string message, Exception? inner = null) : base(message, inner) { }
}
