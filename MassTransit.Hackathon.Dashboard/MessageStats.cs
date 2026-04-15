namespace MassTransit.Hackathon.Dashboard;

/// <summary>
/// Thread-safe counters for each message type (Burger / Fries / Soda).
/// </summary>
internal sealed class MessageStats
{
    private long _burger;
    private long _fries;
    private long _soda;

    public long Burger => Volatile.Read(ref _burger);
    public long Fries  => Volatile.Read(ref _fries);
    public long Soda   => Volatile.Read(ref _soda);
    public long Total  => Burger + Fries + Soda;

    public void Record(string line)
    {
        if      (line.Contains("Burger", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _burger);
        else if (line.Contains("Fries",  StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _fries);
        else if (line.Contains("Soda",   StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _soda);
    }
}
