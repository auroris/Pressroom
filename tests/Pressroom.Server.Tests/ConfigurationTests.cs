using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;
using Pressroom.Server.Jobs;
using Pressroom.Server.Printing;
using Xunit;

namespace Pressroom.Server.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pressroom-configuration-tests", Guid.NewGuid().ToString("N"));

    public ConfigurationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ApiKeyModeRequiresAtLeastOneConfiguredKey()
    {
        Assert.Equal(AuthMode.ApiKey, new AuthSettings().Mode);
        Assert.False(new ApiKeySettings().AllowJavaScript);
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings()));
        ServerConfiguration.ValidateAuthSettings(new AuthSettings
        {
            ApiKeys = new() { ["caller"] = new() { Secret = TestApplication.Secret } }
        });
    }

    [Fact]
    public void AnonymousModeMustBeExplicitAndDoesNotRequireKeys()
    {
        ServerConfiguration.ValidateAuthSettings(new AuthSettings { Mode = AuthMode.Anonymous });
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings { Mode = (AuthMode)123 }));
    }

    [Fact]
    public void DuplicateSecretsCannotHaveAmbiguousJavaScriptPermissions()
    {
        foreach (var mode in new[] { AuthMode.ApiKey, AuthMode.Anonymous })
        {
            var error = Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings
            {
                Mode = mode,
                ApiKeys = new()
                {
                    ["static"] = new() { Secret = TestApplication.Secret },
                    ["charts"] = new() { Secret = TestApplication.Secret, AllowJavaScript = true }
                }
            }));
            Assert.DoesNotContain(TestApplication.Secret, error.Message);
        }
    }

    [Fact]
    public void NullKeyObjectsFailValidation()
    {
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings
        {
            ApiKeys = new() { ["caller"] = null! }
        }));
    }

    [Theory]
    [InlineData("", "a-valid-length-secret")]
    [InlineData(" ", "a-valid-length-secret")]
    [InlineData("caller", "")]
    [InlineData("caller", "short")]
    [InlineData("caller", "                ")]
    public void InvalidConfiguredKeysFailValidationEvenInAnonymousMode(string identifier, string secret)
    {
        foreach (var mode in new[] { AuthMode.ApiKey, AuthMode.Anonymous })
            Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings
            {
                Mode = mode,
                ApiKeys = new() { [identifier] = new() { Secret = secret } }
            }));
    }

    [Theory]
    [InlineData(129, 32)]
    [InlineData(6, 4097)]
    public void OversizedConfiguredKeysFailValidation(int identifierLength, int secretLength)
    {
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings
        {
            ApiKeys = new() { [new string('i', identifierLength)] = new() { Secret = new string('s', secretLength) } }
        }));
    }

    [Fact]
    public void ExternalJsonOverridesDefaultsAndResolvesRelativeToApplicationDirectory()
    {
        WriteJson("appsettings.json", new
        {
            ConfigurationFile = "pressroom.settings.json",
            Auth = new { Mode = "ApiKey" },
            Render = new { Concurrency = 2, QueueLimit = 8 }
        });
        WriteJson("pressroom.settings.json", new
        {
            Auth = new
            {
                ApiKeys = new Dictionary<string, object>
                {
                    ["caller"] = new { Secret = TestApplication.Secret },
                    ["charts"] = new { Secret = TestApplication.StaticSecret, AllowJavaScript = true }
                }
            },
            Render = new { Concurrency = 3 }
        });
        using var configuration = new ConfigurationManager();
        ApplicationConfiguration.Configure(configuration, _directory);
        Assert.Equal("3", configuration["Render:Concurrency"]);
        Assert.Equal("8", configuration["Render:QueueLimit"]);
        var auth = configuration.GetSection("Auth").Get<AuthSettings>()!;
        Assert.Equal(AuthMode.ApiKey, auth.Mode);
        Assert.Equal(TestApplication.Secret, auth.ApiKeys["caller"].Secret);
        Assert.False(auth.ApiKeys["caller"].AllowJavaScript);
        Assert.True(auth.ApiKeys["charts"].AllowJavaScript);
        ServerConfiguration.ValidateAuthSettings(auth);
    }

    [Fact]
    public void PrintersBindFromAppsettingsAndExternalOverrides()
    {
        WriteJson("appsettings.json", new
        {
            Printing = new { SumatraPdfPath = Path.Combine(_directory, "SumatraPDF.exe"), Printers = new Dictionary<string, string> { ["office"] = "Old Printer" } }
        });
        WriteJson("selected.json", new
        {
            Printing = new { CupsCommandPath = "/usr/bin/lpr", Printers = new Dictionary<string, string> { ["office"] = "Office Printer", ["pdf-test"] = "Microsoft Print to PDF" } }
        });
        using var configuration = new ConfigurationManager();
        ApplicationConfiguration.Configure(configuration, _directory, "selected.json");
        var printing = configuration.GetSection("Printing").Get<PrintingSettings>()!;
        printing.Validate();
        Assert.Equal("/usr/bin/lpr", printing.CupsCommandPath);
        Assert.Equal("Office Printer", printing.Printers["office"]);
        Assert.Equal("Microsoft Print to PDF", printing.Printers["pdf-test"]);
        Assert.Equal(Path.Combine(_directory, "SumatraPDF.exe"), printing.SumatraPdfPath);
    }

    [Fact]
    public void ExplicitFileSelectionOverridesThePathInAppsettings()
    {
        WriteJson("appsettings.json", new { ConfigurationFile = "missing.json" });
        WriteJson("selected.json", new { Auth = new { Mode = "Anonymous" } });
        using var configuration = new ConfigurationManager();
        ApplicationConfiguration.Configure(configuration, _directory, "selected.json");
        Assert.Equal("Anonymous", configuration["Auth:Mode"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitlySelectedMissingFileFails(bool argumentSelection)
    {
        WriteJson("appsettings.json", new { ConfigurationFile = argumentSelection ? "" : "missing.json" });
        using var configuration = new ConfigurationManager();
        Assert.Throws<FileNotFoundException>(() => ApplicationConfiguration.Configure(configuration, _directory,
            argumentSelection ? "missing.json" : null));
    }

    [Fact]
    public void EnvironmentAndCommandLineProvidersCannotOverrideApplicationFiles()
    {
        const string variable = "PRESSROOM_CONFIGURATION_TEST_Auth__Mode";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "Anonymous");
            WriteJson("appsettings.json", new { ConfigurationFile = "pressroom.settings.json", Auth = new { Mode = "ApiKey" } });
            WriteJson("pressroom.settings.json", new { Auth = new { ApiKeys = new Dictionary<string, object> { ["caller"] = new { Secret = TestApplication.Secret } } } });
            using var configuration = new ConfigurationManager();
            configuration.AddEnvironmentVariables("PRESSROOM_CONFIGURATION_TEST_");
            configuration.AddCommandLine(["--Auth:Mode", "Anonymous", "--Auth:ApiKeys:injected:Secret", "an-unwanted-command-line-secret"]);
            Assert.Equal("Anonymous", configuration["Auth:Mode"]);
            ApplicationConfiguration.Configure(configuration, _directory);
            Assert.Equal("ApiKey", configuration["Auth:Mode"]);
            Assert.Null(configuration["Auth:ApiKeys:injected:Secret"]);
            Assert.Equal(TestApplication.Secret, configuration["Auth:ApiKeys:caller:Secret"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Theory]
    [InlineData("Render", "Concurency")]
    [InlineData("Paths", "StateRot")]
    [InlineData("Fonts", "Manifests")]
    [InlineData("Auth", "WindowsEnabled")]
    [InlineData("Printing", "Printer")]
    [InlineData("Jobs", "Worker")]
    public void MisspeltKeysFailBindingInEverySettingsSection(string section, string key)
    {
        using var services = BindSettings(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{section}:{key}"] = "1" }).Build());
        var error = Assert.Throws<InvalidOperationException>(() => Bind(services, section));
        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData("src/Pressroom.Server/appsettings.json")]
    [InlineData("samples/pressroom.settings.example.json")]
    [InlineData("deploy/check.settings.json")]
    public void ShippedSettingsFilesUseOnlyKnownKeys(string relativePath)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Pressroom.slnx")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException("Run the tests from a repository checkout.");
        using var services = BindSettings(new ConfigurationBuilder().AddJsonFile(Path.Combine(root, relativePath)).Build());
        foreach (var section in new[] { "Render", "Paths", "Fonts", "Auth", "Printing", "Jobs" })
            Assert.NotNull(Bind(services, section));
    }

    private static ServiceProvider BindSettings(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        ApplicationConfiguration.AddSettings(services);
        return services.BuildServiceProvider();
    }

    private static object Bind(IServiceProvider services, string section) => section switch
    {
        "Render" => services.GetRequiredService<IOptions<RenderSettings>>().Value,
        "Paths" => services.GetRequiredService<IOptions<PathsSettings>>().Value,
        "Fonts" => services.GetRequiredService<IOptions<FontsSettings>>().Value,
        "Auth" => services.GetRequiredService<IOptions<AuthSettings>>().Value,
        "Printing" => services.GetRequiredService<IOptions<PrintingSettings>>().Value,
        "Jobs" => services.GetRequiredService<IOptions<JobsSettings>>().Value,
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    private void WriteJson(string name, object value) =>
        File.WriteAllText(Path.Combine(_directory, name), JsonSerializer.Serialize(value));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
