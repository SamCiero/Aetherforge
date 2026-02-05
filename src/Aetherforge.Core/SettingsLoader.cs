// D:/Aetherforge/src/Aetherforge.Core/SettingsLoader.cs

using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aetherforge.Core;

public static class SettingsLoader
{
    public static (SettingsV1? Settings, string? Error) TryLoad(string settingsPath)
    {
        if (!File.Exists(settingsPath))
            return (null, $"settings.yaml missing: {settingsPath}");

        var yaml = File.ReadAllText(settingsPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(yaml))
            return (null, $"settings.yaml is empty: {settingsPath}");

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance) // snake_case <-> PascalCase
                .IgnoreUnmatchedProperties()
                .Build();

            var settings = deserializer.Deserialize<SettingsV1>(yaml);

            var err = SettingsValidator.Validate(settings);
            return err is null ? (settings, null) : (null, err);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public static class SettingsValidator
{
    public static string? Validate(SettingsV1 s)
    {
        if (s.SchemaVersion != 1)
            return $"schema_version must be 1 (got {s.SchemaVersion})";

        if (!TryAbsUri(s.Ports.CoreBindUrl, out var core, out var coreErr))
            return $"ports.core_bind_url invalid: {coreErr}";

        if (!TryAbsUri(s.Ports.OllamaBaseUrl, out var oll, out var ollErr))
            return $"ports.ollama_base_url invalid: {ollErr}";

        // Spec requires 127.0.0.1 canonical (avoid localhost slowness):contentReference[oaicite:3]{index=3}
        if (!string.Equals(core!.Host, "127.0.0.1", StringComparison.Ordinal))
            return "ports.core_bind_url must use host 127.0.0.1";

        if (!string.Equals(oll!.Host, "127.0.0.1", StringComparison.Ordinal))
            return "ports.ollama_base_url must use host 127.0.0.1";

        var role = (s.Defaults.Role ?? "").Trim().ToLowerInvariant();
        var tier = (s.Defaults.Tier ?? "").Trim().ToLowerInvariant();

        if (role is not ("general" or "coding" or "agent"))
            return $"defaults.role invalid: {s.Defaults.Role}";

        if (tier is not ("fast" or "thinking"))
            return $"defaults.tier invalid: {s.Defaults.Tier}";

        // Boundary allowlist must exist (spec + M1 checklist task):contentReference[oaicite:4]{index=4}:contentReference[oaicite:5]{index=5}
        if (s.Boundary.AllowWriteUnderWsl is null || s.Boundary.AllowWriteUnderWsl.Count == 0)
            return "boundary.allow_write_under_wsl must contain at least one root";

        foreach (var r in s.Boundary.AllowWriteUnderWsl)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            if (!r.StartsWith("/", StringComparison.Ordinal))
                return $"boundary.allow_write_under_wsl must be absolute WSL paths (bad: {r})";
        }

        return null;
    }

    private static bool TryAbsUri(string? raw, out Uri? uri, out string err)
    {
        uri = null;
        err = "missing";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)) { err = "not an absolute URI"; return false; }
        err = "";
        return true;
    }
}
