using System.Globalization;
using System.Text.RegularExpressions;

namespace InfiniteSpartanRender;

// spartan.py prints "ISR_PHASE <name> <seconds>" as each stage ends; the names are a fixed short list (metric labels).
public static class PhaseTiming
{
    private static readonly Regex Line = new(@"^ISR_PHASE ([a-z_]{1,24}) (\d+(\.\d+)?)$", RegexOptions.Compiled);

    public static bool TryParse(string? line, out string phase, out double seconds)
    {
        phase = "";
        seconds = 0;
        if (line is null || Line.Match(line) is not { Success: true } m) return false;
        phase = m.Groups[1].Value;
        seconds = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }
}
