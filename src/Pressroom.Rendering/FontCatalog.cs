using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pressroom.Rendering;

internal sealed partial class FontCatalog
{
    public string Css { get; }
    public IReadOnlyList<PackagedFont> Fonts { get; }

    public FontCatalog(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("The packaged font manifest is missing. Publish the complete Pressroom release.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The packaged font manifest is invalid.");
        if (manifest.Fonts.Length == 0)
            throw new InvalidOperationException("The packaged font manifest has no font faces.");
        var root = Path.GetDirectoryName(manifestPath)! + Path.DirectorySeparatorChar;
        var css = new StringBuilder();
        var faces = new List<PackagedFont>();
        foreach (var font in manifest.Fonts)
        {
            if (!SafeFamily().IsMatch(font.Family) || !SafeWeight().IsMatch(font.Weight) || font.Style is not ("normal" or "italic"))
                throw new InvalidOperationException("A packaged font has invalid CSS metadata.");
            var path = Path.GetFullPath(font.File, root);
            if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("A packaged font path escapes its manifest directory.");
            var bytes = File.ReadAllBytes(path);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(font.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A packaged font failed its release checksum.");
            css.Append("@font-face{font-family:'").Append(font.Family)
                .Append("';font-weight:").Append(font.Weight).Append(";font-style:").Append(font.Style)
                .Append(";font-display:block;src:url(data:font/ttf;base64,").Append(Convert.ToBase64String(bytes))
                .Append(") format('truetype');}");
            faces.Add(new(font.Family, font.Weight, font.Style));
        }
        Css = css.ToString();
        Fonts = faces.AsReadOnly();
    }

    [GeneratedRegex("^[A-Za-z0-9 _-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFamily();
    [GeneratedRegex("^[1-9]00$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeWeight();

    private sealed class Manifest { public FontFace[] Fonts { get; set; } = []; }
    private sealed class FontFace
    {
        public string Family { get; set; } = "";
        public string Weight { get; set; } = "";
        public string Style { get; set; } = "";
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
