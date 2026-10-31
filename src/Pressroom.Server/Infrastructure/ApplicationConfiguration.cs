namespace Pressroom.Server.Infrastructure;

public static class ApplicationConfiguration
{
    public static void Configure(ConfigurationManager configuration, string applicationDirectory, string? configurationFile = null)
    {
        // Use only JSON for application settings. IIS/ASP.NET Core may still use their own
        // process environment for hosting, but it cannot override keys or rendering settings.
        configuration.Sources.Clear();
        configuration.SetBasePath(applicationDirectory);
        configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        var selected = configurationFile ?? configuration["ConfigurationFile"];
        var explicitFile = !string.IsNullOrWhiteSpace(selected);
        var external = explicitFile
            ? Path.GetFullPath(selected!, applicationDirectory)
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pressroom", "pressroom.settings.json")
                : "/etc/pressroom/pressroom.settings.json";
        configuration.AddJsonFile(external, optional: !explicitFile, reloadOnChange: false);
    }

    internal static string? GetConfigurationFile(string[] args)
    {
        string? selected = null;
        for (var index = 0; index < args.Length; index++)
        {
            string value;
            if (args[index] == "--config")
            {
                if (index + 1 == args.Length)
                    throw new ArgumentException("Specify --config with a configuration file path.");
                value = args[++index];
            }
            else if (args[index].StartsWith("--config=", StringComparison.Ordinal))
                value = args[index]["--config=".Length..];
            else
                continue; // Framework hosting switches are handled by the web host, not application settings.

            if (selected is not null || string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Specify --config exactly once with a configuration file path.");
            selected = value;
        }
        return selected;
    }
}
