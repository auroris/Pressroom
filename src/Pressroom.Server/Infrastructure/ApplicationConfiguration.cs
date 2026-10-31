using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Jobs;
using Pressroom.Server.Printing;

namespace Pressroom.Server.Infrastructure;

public static class ApplicationConfiguration
{
    /// <summary>The machine-wide settings file read when neither --config nor ConfigurationFile selects one.</summary>
    public static string DefaultConfigurationFile => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pressroom", "pressroom.settings.json")
        : "/etc/pressroom/pressroom.settings.json";

    public static void Configure(ConfigurationManager configuration, string applicationDirectory, string? configurationFile = null)
    {
        // Use only JSON for application settings. IIS/ASP.NET Core may still use their own
        // process environment for hosting, but it cannot override keys or rendering settings.
        configuration.Sources.Clear();
        configuration.SetBasePath(applicationDirectory);
        configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        // ConfigurationManager loads each source as it is added, so an explicit selection is the
        // only way to keep a host (including the test host) from opening the machine-wide file.
        var selected = configurationFile ?? configuration["ConfigurationFile"];
        var explicitFile = !string.IsNullOrWhiteSpace(selected);
        var external = explicitFile ? Path.GetFullPath(selected!, applicationDirectory) : DefaultConfigurationFile;
        configuration.AddJsonFile(external, optional: !explicitFile, reloadOnChange: false);
    }

    /// <summary>Binds every application settings section, rejecting keys that match no property.</summary>
    public static void AddSettings(IServiceCollection services)
    {
        // A misspelt key (Render:Concurency) would otherwise be ignored silently and leave a
        // default limit or path in force. Every section fails startup and --check instead.
        services.AddOptions<RenderSettings>().BindConfiguration("Render", options => options.ErrorOnUnknownConfiguration = true);
        services.AddOptions<PathsSettings>().BindConfiguration("Paths", options => options.ErrorOnUnknownConfiguration = true);
        services.AddOptions<FontsSettings>().BindConfiguration("Fonts", options => options.ErrorOnUnknownConfiguration = true);
        services.AddOptions<AuthSettings>().BindConfiguration("Auth", options => options.ErrorOnUnknownConfiguration = true);
        services.AddOptions<PrintingSettings>().BindConfiguration("Printing", options => options.ErrorOnUnknownConfiguration = true);
        services.AddOptions<JobsSettings>().BindConfiguration("Jobs", options => options.ErrorOnUnknownConfiguration = true);
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
