using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pressroom.Contracts;

public static partial class RequestValidator
{
    public static IReadOnlyList<string> NamedPageSizes { get; } = Array.AsReadOnly(["Letter", "Legal", "Tabloid", "A3", "A4", "A5"]);

    public static bool IsPrinterIdentifier(string value) => value.Length is >= 1 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static Dictionary<string, string[]> Validate(RenderRequest request, int maxTimeoutMs = 60000)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Error(string field, string message) => errors[field] = [message];

        if (string.IsNullOrWhiteSpace(request.Html)) Error("html", "A complete HTML document is required.");
        if (request.Printer is not null && !IsPrinterIdentifier(request.Printer))
            Error("printer", "Use a configured printer identifier of 1–128 ASCII letters, digits, hyphens, or underscores.");
        if (request.Options is not { } options)
        {
            Error("options", "Options cannot be null; omit options to use the defaults.");
            return errors;
        }

        if (options.PageSize is { } page)
        {
            if (page.Name is not null)
            {
                if (!NamedPageSizes.Contains(page.Name, StringComparer.OrdinalIgnoreCase))
                    Error("options.pageSize", "Use Letter, Legal, Tabloid, A3, A4, A5, or custom dimensions.");
                if (page.Width is not null || page.Height is not null)
                    Error("options.pageSize", "Choose a named size or custom dimensions, not both.");
            }
            else
            {
                if (!ValidLength(page.Width, allowZero: false)) Error("options.pageSize.width", "Specify a positive width in px, mm, cm, or in, up to 200 inches.");
                if (!ValidLength(page.Height, allowZero: false)) Error("options.pageSize.height", "Specify a positive height in px, mm, cm, or in, up to 200 inches.");
            }
        }

        if (options.Margins is not { } margins) Error("options.margins", "Margins cannot be null.");
        else
        {
            if (!ValidLength(margins.Top, true)) Error("options.margins.top", "Use a nonnegative length in px, mm, cm, or in, up to 200 inches.");
            if (!ValidLength(margins.Right, true)) Error("options.margins.right", "Use a nonnegative length in px, mm, cm, or in, up to 200 inches.");
            if (!ValidLength(margins.Bottom, true)) Error("options.margins.bottom", "Use a nonnegative length in px, mm, cm, or in, up to 200 inches.");
            if (!ValidLength(margins.Left, true)) Error("options.margins.left", "Use a nonnegative length in px, mm, cm, or in, up to 200 inches.");
            if (!options.PreferCssPageSize && options.PageSize is { } paper && TryPaperInches(paper, out var width, out var height)
                && ValidLength(margins.Top, true) && ValidLength(margins.Bottom, true)
                && ValidLength(margins.Left, true) && ValidLength(margins.Right, true))
            {
                if (options.Landscape) (width, height) = (height, width);
                if (LengthInches(margins.Top) + LengthInches(margins.Bottom) >= height
                    || LengthInches(margins.Left) + LengthInches(margins.Right) >= width)
                    Error("options.margins", "Margins must leave a positive printable area on the selected paper size.");
            }
        }
        if (!double.IsFinite(options.Scale) || options.Scale is < 0.1 or > 2)
            Error("options.scale", "Scale must be between 0.1 and 2.");
        if (options.Media is not ("print" or "screen")) Error("options.media", "Media must be print or screen.");
        if (options.TimeoutMs is { } timeout && (timeout < 100 || timeout > maxTimeoutMs))
            Error("options.timeoutMs", $"Timeout must be between 100 and {maxTimeoutMs} milliseconds.");

        if (string.IsNullOrWhiteSpace(options.Locale) || options.Locale.Length > 64)
            Error("options.locale", "Specify a valid locale such as en-CA.");
        else
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(options.Locale);
                if (culture.Equals(CultureInfo.InvariantCulture)) Error("options.locale", "Specify a non-invariant locale such as en-CA.");
            }
            catch (CultureNotFoundException) { Error("options.locale", "The locale is not recognized."); }
        }
        if (string.IsNullOrWhiteSpace(options.TimeZone) || options.TimeZone.Length > 128)
            Error("options.timeZone", "Specify an IANA time zone such as America/Edmonton.");
        else
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
                if (options.TimeZone != "UTC" && !zone.HasIanaId && !TimeZoneInfo.TryConvertIanaIdToWindowsId(options.TimeZone, out _))
                    Error("options.timeZone", "Use an IANA time zone identifier, such as America/Edmonton.");
            }
            catch (TimeZoneNotFoundException) { Error("options.timeZone", "The time zone is not recognized."); }
            catch (InvalidTimeZoneException) { Error("options.timeZone", "The time zone is invalid."); }
        }
        if (options.Readiness is not { } readiness) Error("options.readiness", "Readiness cannot be null.");
        else
        {
            if (readiness.TimeoutMs < 1 || readiness.TimeoutMs > maxTimeoutMs)
                Error("options.readiness.timeoutMs", $"Readiness timeout must be between 1 and {maxTimeoutMs} milliseconds.");
            if (readiness.DelayMs < 0 || readiness.DelayMs > readiness.TimeoutMs)
                Error("options.readiness.delayMs", "Delay must be nonnegative and fit inside the readiness timeout.");
            if (readiness.Selector is not null && (string.IsNullOrWhiteSpace(readiness.Selector) || readiness.Selector.Length > 4096))
                Error("options.readiness.selector", "Selector must contain between 1 and 4096 characters.");
            if (readiness.Function is not null)
            {
                if (string.IsNullOrWhiteSpace(readiness.Function) || readiness.Function.Length > 16384)
                    Error("options.readiness.function", "Expression must contain between 1 and 16384 characters.");
                if (!options.JavaScript) Error("options.readiness.function", "A readiness expression requires JavaScript to be enabled.");
            }
        }
        return errors;
    }

    public static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "document.pdf";
        // Treat both Windows and POSIX separators identically on every build platform.
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > 120) break;
            builder.Append(Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value)) ? "_" : rune.ToString());
        }
        var result = builder.ToString().Trim(' ', '.');
        if (result.Length == 0) result = "document";
        var stem = result.Split('.')[0];
        if (ReservedFileName().IsMatch(stem)) result = "_" + result;
        return result.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? result : result + ".pdf";
    }

    private static bool ValidLength(string? value, bool allowZero)
    {
        if (value is null || value.Length > 32) return false;
        var match = CssLength().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return false;
        var inches = match.Groups[2].Value switch
        {
            "px" => amount / 96,
            "mm" => amount / 25.4,
            "cm" => amount / 2.54,
            _ => amount
        };
        return double.IsFinite(inches) && inches <= 200 && (allowZero ? inches >= 0 : inches > 0);
    }

    private static double LengthInches(string value)
    {
        var match = CssLength().Match(value);
        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch { "px" => amount / 96, "mm" => amount / 25.4, "cm" => amount / 2.54, _ => amount };
    }

    private static bool TryPaperInches(PageSize page, out double width, out double height)
    {
        (width, height) = page.Name?.ToUpperInvariant() switch
        {
            "LETTER" => (8.5, 11), "LEGAL" => (8.5, 14), "TABLOID" => (11, 17),
            "A3" => (297 / 25.4, 420 / 25.4), "A4" => (210 / 25.4, 297 / 25.4), "A5" => (148 / 25.4, 210 / 25.4),
            _ => (0, 0)
        };
        if (width > 0) return true;
        if (page.Name is not null || !ValidLength(page.Width, false) || !ValidLength(page.Height, false)) return false;
        (width, height) = (LengthInches(page.Width!), LengthInches(page.Height!));
        return true;
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?|\.[0-9]+)(px|mm|cm|in)$", RegexOptions.CultureInvariant)]
    private static partial Regex CssLength();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedFileName();
}
