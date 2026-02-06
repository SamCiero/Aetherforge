// D:/Aetherforge/src/Aetherforge.Core/SettingsLoader.cs

using System;
using System.IO;
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
            if (settings is null)
                return (null, "settings.yaml deserialized to null");

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

        // -----------------------------
        // ports.*
        // -----------------------------
        if (!TryAbsUri(s.Ports.CoreBindUrl, out var core, out var coreErr))
            return $"ports.core_bind_url invalid: {coreErr}";

        if (!TryAbsUri(s.Ports.OllamaBaseUrl, out var oll, out var ollErr))
            return $"ports.ollama_base_url invalid: {ollErr}";

        // Canonical host requirement: avoid localhost resolution slowness and enforce determinism.
        if (!string.Equals(core!.Host, "127.0.0.1", StringComparison.Ordinal))
            return "ports.core_bind_url must use host 127.0.0.1";

        if (!string.Equals(oll!.Host, "127.0.0.1", StringComparison.Ordinal))
            return "ports.ollama_base_url must use host 127.0.0.1";

        // -----------------------------
        // defaults.*
        // -----------------------------
        var role = (s.Defaults.Role ?? "").Trim().ToLowerInvariant();
        var tier = (s.Defaults.Tier ?? "").Trim().ToLowerInvariant();

        if (role is not ("general" or "coding" or "agent"))
            return $"defaults.role invalid: {s.Defaults.Role}";

        if (tier is not ("fast" or "thinking"))
            return $"defaults.tier invalid: {s.Defaults.Tier}";

        // -----------------------------
        // pins.*  (storage-constrained fallback support)
        // -----------------------------
        var pinsMode = (s.Pins.Mode ?? "").Trim().ToLowerInvariant();
        if (pinsMode is not ("strict" or "fallback"))
            return $"pins.mode invalid: {s.Pins.Mode}";

        var fbRole = (s.Pins.FallbackRole ?? "").Trim().ToLowerInvariant();
        var fbTier = (s.Pins.FallbackTier ?? "").Trim().ToLowerInvariant();

        if (fbRole.Length == 0)
            return "pins.fallback_role is required";

        if (fbTier.Length == 0)
            return "pins.fallback_tier is required";

        if (fbRole is not ("general" or "coding" or "agent"))
            return $"pins.fallback_role invalid: {s.Pins.FallbackRole}";

        if (fbTier is not ("fast" or "thinking"))
            return $"pins.fallback_tier invalid: {s.Pins.FallbackTier}";

        // -----------------------------
        // boundary.*  (allowlist + roots + bridge rules)
        // -----------------------------
        if (s.Boundary.BridgeRules is null || s.Boundary.BridgeRules.Count == 0)
            return "boundary.bridge_rules must contain at least one rule";

        foreach (var br in s.Boundary.BridgeRules)
        {
            var win = (br.WindowsRoot ?? "").Trim();
            var wsl = (br.WslRoot ?? "").Trim();

            if (win.Length == 0)
                return "boundary.bridge_rules.windows_root is required";

            if (wsl.Length == 0)
                return "boundary.bridge_rules.wsl_root is required";

            // Keep this lightweight: just enforce "absolute-ish" shapes.
            if (!win.Contains(":\\", StringComparison.Ordinal))
                return $"boundary.bridge_rules.windows_root must look like an absolute Windows path (bad: {br.WindowsRoot})";

            if (!wsl.StartsWith("/", StringComparison.Ordinal))
                return $"boundary.bridge_rules.wsl_root must be an absolute WSL path (bad: {br.WslRoot})";
        }

        // roots.wsl.{config,exports,logs} required by the settings.yaml refactor
        var rootCfg = (s.Boundary.Roots.Wsl.Config ?? "").Trim();
        var rootExp = (s.Boundary.Roots.Wsl.Exports ?? "").Trim();
        var rootLog = (s.Boundary.Roots.Wsl.Logs ?? "").Trim();

        if (!IsAbsWsl(rootCfg)) return $"boundary.roots.wsl.config must be an absolute WSL path (bad: {s.Boundary.Roots.Wsl.Config})";
        if (!IsAbsWsl(rootExp)) return $"boundary.roots.wsl.exports must be an absolute WSL path (bad: {s.Boundary.Roots.Wsl.Exports})";
        if (!IsAbsWsl(rootLog)) return $"boundary.roots.wsl.logs must be an absolute WSL path (bad: {s.Boundary.Roots.Wsl.Logs})";

        if (IsTriviallyUnsafeRoot(rootCfg)) return $"boundary.roots.wsl.config is too broad (bad: {rootCfg})";
        if (IsTriviallyUnsafeRoot(rootExp)) return $"boundary.roots.wsl.exports is too broad (bad: {rootExp})";
        if (IsTriviallyUnsafeRoot(rootLog)) return $"boundary.roots.wsl.logs is too broad (bad: {rootLog})";

        // allow_write_under_wsl must exist
        if (s.Boundary.AllowWriteUnderWsl is null || s.Boundary.AllowWriteUnderWsl.Count == 0)
            return "boundary.allow_write_under_wsl must contain at least one root";

        // allow_write_under_wsl entries must be absolute and not dangerously broad
        foreach (var rRaw in s.Boundary.AllowWriteUnderWsl)
        {
            var r = (rRaw ?? "").Trim();
            if (r.Length == 0) continue;

            if (!IsAbsWsl(r))
                return $"boundary.allow_write_under_wsl must be absolute WSL paths (bad: {rRaw})";

            if (IsTriviallyUnsafeRoot(r))
                return $"boundary.allow_write_under_wsl contains an unsafe broad root (bad: {rRaw})";
        }

        // Ensure the three required roots are writable (matches the refactored settings.yaml contract)
        if (!ContainsPath(s.Boundary.AllowWriteUnderWsl, rootCfg))
            return $"boundary.allow_write_under_wsl must include boundary.roots.wsl.config ({rootCfg})";

        if (!ContainsPath(s.Boundary.AllowWriteUnderWsl, rootExp))
            return $"boundary.allow_write_under_wsl must include boundary.roots.wsl.exports ({rootExp})";

        if (!ContainsPath(s.Boundary.AllowWriteUnderWsl, rootLog))
            return $"boundary.allow_write_under_wsl must include boundary.roots.wsl.logs ({rootLog})";

        // allow_read_under_wsl (optional) must be absolute if present
        if (s.Boundary.AllowReadUnderWsl is not null)
        {
            foreach (var rRaw in s.Boundary.AllowReadUnderWsl)
            {
                var r = (rRaw ?? "").Trim();
                if (r.Length == 0) continue;

                if (!IsAbsWsl(r))
                    return $"boundary.allow_read_under_wsl must be absolute WSL paths (bad: {rRaw})";

                if (IsTriviallyUnsafeRoot(r))
                    return $"boundary.allow_read_under_wsl contains an unsafe broad root (bad: {rRaw})";
            }
        }

        return null;
    }

    private static bool TryAbsUri(string? raw, out Uri? uri, out string err)
    {
        uri = null;
        err = "missing";

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out uri))
        {
            err = "not an absolute URI";
            return false;
        }

        err = "";
        return true;
    }

    private static bool IsAbsWsl(string p)
        => !string.IsNullOrWhiteSpace(p) && p.StartsWith("/", StringComparison.Ordinal);

    private static bool IsTriviallyUnsafeRoot(string p)
        => string.Equals(p, "/", StringComparison.Ordinal);

    private static bool ContainsPath(System.Collections.Generic.List<string> list, string path)
    {
        foreach (var raw in list)
        {
            var v = (raw ?? "").Trim();
            if (v.Length == 0) continue;
            if (string.Equals(v, path, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
