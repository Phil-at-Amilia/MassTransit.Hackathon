namespace MassTransit.Hackathon;

/// <summary>
/// Configuration parsed from CLI args. Controls which role(s) this process runs.
/// </summary>
public class WorkerOptions
{
    /// <summary>all | consumer | publisher</summary>
    public string Mode { get; set; } = "all";

    /// <summary>linecook | bartender | manager  (only relevant when Mode=consumer)</summary>
    public string Type { get; set; } = "";

    /// <summary>Publish interval in seconds (only relevant when Mode=publisher or all)</summary>
    public int RateSeconds { get; set; } = 5;

    /// <summary>Artificial processing delay in ms added inside Consume() — for crash simulation</summary>
    public int SlowMs { get; set; } = 0;

    /// <summary>Short label prepended to log messages, e.g. "Cook-1"</summary>
    public string Label { get; set; } = "";

    /// <summary>Item type for group-order mode (burger | fries | soda)</summary>
    public string Item { get; set; } = "";

    /// <summary>Number of orders to publish in group-order mode</summary>
    public int Count { get; set; } = 0;

    public static WorkerOptions Parse(string[] args)
    {
        var opts = new WorkerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mode" when i + 1 < args.Length:
                    opts.Mode = args[++i].ToLowerInvariant();
                    break;
                case "--type" when i + 1 < args.Length:
                    opts.Type = args[++i].ToLowerInvariant();
                    break;
                case "--rate" when i + 1 < args.Length:
                    opts.RateSeconds = int.TryParse(args[++i], out var r) ? Math.Max(1, r) : 5;
                    break;
                case "--slow" when i + 1 < args.Length:
                    opts.SlowMs = int.TryParse(args[++i], out var s) ? Math.Max(0, s) : 0;
                    break;
                case "--label" when i + 1 < args.Length:
                    opts.Label = args[++i];
                    break;
                case "--item" when i + 1 < args.Length:
                    opts.Item = args[++i].ToLowerInvariant();
                    break;
                case "--count" when i + 1 < args.Length:
                    opts.Count = int.TryParse(args[++i], out var n) ? Math.Max(0, n) : 0;
                    break;
            }
        }
        return opts;
    }
}
