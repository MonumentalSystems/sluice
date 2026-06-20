namespace Sluice.Nwc;

/// <summary>
/// A rolling daily spend cap (UTC) for NWC <c>pay_invoice</c> — the money guardrail. Reserve before paying
/// (so concurrent requests can't both slip under the cap), and refund if the payment fails. In-memory: a
/// pod restart resets the counter (acceptable for the small caps this gates; the cap itself is the bound).
/// </summary>
public sealed class NwcSpendCap
{
    private readonly long _capSat;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private DateOnly _day;
    private long _spent;

    public NwcSpendCap(long capSat, Func<DateTime>? utcNow = null)
    {
        _capSat = capSat;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _day = DateOnly.FromDateTime(_utcNow());
    }

    public long CapSat => _capSat;

    public long SpentToday
    {
        get { lock (_lock) { Roll(); return _spent; } }
    }

    /// <summary>Reserve <paramref name="sat"/> against today's budget. False (no reservation) if it would
    /// exceed the cap or the amount is non-positive.</summary>
    public bool TryReserve(long sat)
    {
        if (sat <= 0)
            return false;
        lock (_lock)
        {
            Roll();
            if (_spent + sat > _capSat)
                return false;
            _spent += sat;
            return true;
        }
    }

    /// <summary>Release a prior reservation (the payment failed).</summary>
    public void Refund(long sat)
    {
        lock (_lock)
        {
            Roll();
            _spent = Math.Max(0, _spent - sat);
        }
    }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(_utcNow());
        if (today != _day)
        {
            _day = today;
            _spent = 0;
        }
    }
}
