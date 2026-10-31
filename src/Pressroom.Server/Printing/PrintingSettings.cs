using Pressroom.Contracts;

namespace Pressroom.Server.Printing;

public sealed class PrintingSettings
{
    public string SumatraPdfPath { get; set; } = "";
    public string CupsCommandPath { get; set; } = "/usr/bin/lp";
    public Dictionary<string, string> Printers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Printers is null || Printers.Any(pair => !RequestValidator.IsPrinterIdentifier(pair.Key)
            || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 256 || pair.Value.Any(char.IsControl)))
            throw new InvalidOperationException("Printing:Printers must map identifiers of 1–128 ASCII letters, digits, hyphens, or underscores to printer names of 1–256 characters.");
        if (Printers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Printers.Count)
            throw new InvalidOperationException("Printing:Printers identifiers must be unique ignoring case.");
        if (Printers.Count == 0) return;
        if (OperatingSystem.IsLinux())
        {
            CupsPrintCommand.ValidatePath(CupsCommandPath);
            return;
        }
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Printing is supported on Windows and Linux only.");
        if (string.IsNullOrWhiteSpace(SumatraPdfPath)
            || !Path.IsPathFullyQualified(SumatraPdfPath) || !SumatraPdfPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Printing:SumatraPdfPath must be an absolute path to SumatraPDF.exe when printers are configured.");
    }
}
