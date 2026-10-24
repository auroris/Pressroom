using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;
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
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings()));
        ServerConfiguration.ValidateAuthSettings(new AuthSettings
        {
            ApiKeys = new() { ["caller"] = TestApplication.Secret }
        });
    }

    [Fact]
    public void AnonymousModeMustBeExplicitAndDoesNotRequireKeys()
    {
        ServerConfiguration.ValidateAuthSettings(new AuthSettings { Mode = AuthMode.Anonymous });
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings { Mode = (AuthMode)123 }));
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
                ApiKeys = new() { [identifier] = secret }
            }));
    }

    [Theory]
    [InlineData(129, 32)]
    [InlineData(6, 4097)]
    public void OversizedConfiguredKeysFailValidation(int identifierLength, int secretLength)
    {
        Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateAuthSettings(new AuthSettings
        {
            ApiKeys = new() { [new string('i', identifierLength)] = new string('s', secretLength) }
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
            Auth = new { ApiKeys = new Dictionary<string, string> { ["caller"] = TestApplication.Secret } },
            Render = new { Concurrency = 3 }
        });
        using var configuration = new ConfigurationManager();
        ApplicationConfiguration.Configure(configuration, _directory);
        Assert.Equal("3", configuration["Render:Concurrency"]);
        Assert.Equal("8", configuration["Render:QueueLimit"]);
        var auth = configuration.GetSection("Auth").Get<AuthSettings>()!;
        Assert.Equal(AuthMode.ApiKey, auth.Mode);
        Assert.Equal(TestApplication.Secret, auth.ApiKeys["caller"]);
        ServerConfiguration.ValidateAuthSettings(auth);
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
            WriteJson("pressroom.settings.json", new { Auth = new { ApiKeys = new Dictionary<string, string> { ["caller"] = TestApplication.Secret } } });
            using var configuration = new ConfigurationManager();
            configuration.AddEnvironmentVariables("PRESSROOM_CONFIGURATION_TEST_");
            configuration.AddCommandLine(["--Auth:Mode", "Anonymous", "--Auth:ApiKeys:injected", "an-unwanted-command-line-secret"]);
            Assert.Equal("Anonymous", configuration["Auth:Mode"]);
            ApplicationConfiguration.Configure(configuration, _directory);
            Assert.Equal("ApiKey", configuration["Auth:Mode"]);
            Assert.Null(configuration["Auth:ApiKeys:injected"]);
            Assert.Equal(TestApplication.Secret, configuration["Auth:ApiKeys:caller"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    private void WriteJson(string name, object value) =>
        File.WriteAllText(Path.Combine(_directory, name), JsonSerializer.Serialize(value));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
