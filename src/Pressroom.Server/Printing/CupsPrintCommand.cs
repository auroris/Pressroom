using System.Diagnostics;

namespace Pressroom.Server.Printing;

internal static class CupsPrintCommand
{
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Any(char.IsControl)
            || path[(path.LastIndexOf('/') + 1)..] is not ("lp" or "lpr"))
            throw new InvalidOperationException("Printing:CupsCommandPath must be an absolute Linux path to the CUPS lp or lpr executable.");
    }

    public static void AddArguments(ProcessStartInfo start, string printer, string file)
    {
        ValidatePath(start.FileName);
        // CUPS copies the PDF to its spooler before returning. Never use raw mode:
        // the queue's filters must convert the rendered PDF for the device.
        if (start.FileName.EndsWith("/lpr", StringComparison.Ordinal))
        {
            start.ArgumentList.Add("-P");
            start.ArgumentList.Add(printer);
        }
        else
        {
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(printer);
            start.ArgumentList.Add("--");
        }
        start.ArgumentList.Add(file);
    }
}
