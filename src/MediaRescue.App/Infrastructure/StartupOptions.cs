namespace MediaRescue.App.Infrastructure;

/// <summary>Command line: --demo, --demo-state=&lt;state&gt;, --demo-files=N, --demo-duplicate, --theme=light|dark|system (demo only), --smoke.</summary>
public sealed record StartupOptions(bool Demo, string? DemoState, bool Smoke, int DemoFiles, bool DemoDuplicate, string? Theme = null)
{
    public static readonly string[] DemoStates = { "nodrive", "nodisc", "ready", "copying", "partial", "converting", "converted", "diagnostics", "history", "settings", "batch" };

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        bool demo = false, smoke = false, dup = false;
        string? state = null, theme = null;
        int files = 0;
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) demo = true;
            else if (a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)) smoke = true;
            else if (a.Equals("--demo-duplicate", StringComparison.OrdinalIgnoreCase)) { dup = true; demo = true; }
            else if (a.StartsWith("--demo-state=", StringComparison.OrdinalIgnoreCase))
            {
                state = a["--demo-state=".Length..].ToLowerInvariant();
                demo = true;
            }
            else if (a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a["--theme=".Length..].ToLowerInvariant();
                theme = v switch { "light" => "Light", "dark" => "Dark", "system" => "System", _ => null };
            }
            else if (a.StartsWith("--demo-files=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(a["--demo-files=".Length..], out var n) && n > 0)
            {
                files = Math.Min(n, 200_000);
                demo = true;
            }
        }
        if (state != null && !DemoStates.Contains(state)) state = "ready";
        return new StartupOptions(demo, state, smoke, files, dup, theme);
    }
}



