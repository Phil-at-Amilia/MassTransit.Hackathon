namespace MassTransit.Hackathon.Dashboard;

internal static class WorkerExecutable
{
    /// <summary>
    /// Returns <c>(executable, arguments-prefix)</c> for launching a MassTransit.Hackathon worker:
    /// - Prefers the .exe if available (fast, no JIT warm-up per spawn)
    /// - Falls back to `dotnet &lt;dll&gt;`
    ///
    /// Searches for the build output relative to the Dashboard binary location,
    /// then relative to the current working directory (VS Code Debug cwd).
    /// </summary>
    public static (string Exe, string ArgPrefix) Resolve()
    {
        // Candidate roots to search (covers run-from-bin and run-from-project-dir)
        var roots = new[]
        {
            // Running from bin/Debug/net8.0 → up 4 = solution root
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            // Running with `dotnet run` from the Dashboard project dir → up 1 = solution root
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..")),
            // Running from solution root directly
            Directory.GetCurrentDirectory(),
        };

        const string exeRelative = @"MassTransit.Hackathon\bin\Debug\net8.0\MassTransit.Hackathon.exe";
        const string dllRelative = @"MassTransit.Hackathon\bin\Debug\net8.0\MassTransit.Hackathon.dll";

        foreach (var root in roots)
        {
            var exe = Path.Combine(root, exeRelative);
            if (File.Exists(exe))
                return (exe, "");

            var dll = Path.Combine(root, dllRelative);
            if (File.Exists(dll))
                return ("dotnet", $"\"{dll}\" ");
        }

        var searched = string.Join("\n  ", roots.Select(r => Path.Combine(r, exeRelative)));
        throw new FileNotFoundException(
            $"Worker executable not found. Searched:\n  {searched}\n\n" +
            "Run 'dotnet build MassTransit.Hackathon.sln' first.",
            exeRelative);
    }
}
