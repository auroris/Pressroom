using Pressroom.Contracts;

namespace Pressroom.Server.Printing;

public sealed class PrintingSettings
{
    public string SumatraPdfPath { get; set; } = "";
    public Dictionary<string, string> Printers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!OperatingSystem.IsWindows() && (Printers?.Count > 0 || !string.IsNullOrWhiteSpace(SumatraPdfPath)))
            throw new InvalidOperationException("Printing is available on Windows only. Leave Printing:Printers empty and Printing:SumatraPdfPath unset on Linux.");
        if (Printers is null || Printers.Any(pair => !RequestValidator.IsPrinterIdentifier(pair.Key)
            || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 256 || pair.Value.Any(char.IsControl)))
            throw new InvalidOperationException("Printing:Printers must map identifiers of 1–128 ASCII letters, digits, hyphens, or underscores to Windows printer names of 1–256 characters.");
        if (Printers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Printers.Count)
            throw new InvalidOperationException("Printing:Printers identifiers must be unique ignoring case.");
        if (Printers.Count > 0 && (string.IsNullOrWhiteSpace(SumatraPdfPath)
            || !Path.IsPathFullyQualified(SumatraPdfPath) || !SumatraPdfPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Printing:SumatraPdfPath must be an absolute path to SumatraPDF.exe when printers are configured.");
    }
}
