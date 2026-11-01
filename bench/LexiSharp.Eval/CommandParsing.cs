using System.Globalization;

namespace LexiSharp.Eval;

/// <summary>Shared scalar parsers for the evaluation verbs, so each verb keeps its own messages.</summary>
internal static class CommandParsing
{
    public static bool TryPositiveInt(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

    public static bool TryNonNegativeInt(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;

    public static bool TryFiniteDouble(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed);

    public static bool TryPositiveDouble(string value, out double parsed) =>
        TryFiniteDouble(value, out parsed) && parsed > 0;

    public static bool TryFraction(string value, out double parsed) =>
        TryFiniteDouble(value, out parsed) && parsed > 0 && parsed < 1;

    public static bool TryUnitInterval(string value, out double parsed) =>
        TryFiniteDouble(value, out parsed) && parsed >= 0 && parsed <= 1;

    public static bool TryTextMode(string value, out ClassificationTextMode mode)
    {
        if (value == "full")
        {
            mode = ClassificationTextMode.SubjectAndBody;
            return true;
        }

        if (value == "subject")
        {
            mode = ClassificationTextMode.SubjectOnly;
            return true;
        }

        mode = default;
        return false;
    }
}
