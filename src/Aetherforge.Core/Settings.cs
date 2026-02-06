// D:/Aetherforge/src/Aetherforge.Core/Settings.cs

using System;
using System.Collections.Generic;

namespace Aetherforge.Core;

public sealed record SettingsV1
{
    public int SchemaVersion { get; init; }
    public string? CapturedUtc { get; init; }

    public PortsSettings Ports { get; init; } = new();
    public DefaultsSettings Defaults { get; init; } = new();
    public PinsSettings Pins { get; init; } = new();
    public ProfilesSettings Profiles { get; init; } = new();
    public GenerationSettings Generation { get; init; } = new();
    public AutostartSettings Autostart { get; init; } = new();
    public BoundarySettings Boundary { get; init; } = new();
    public AgentSettings Agent { get; init; } = new();
}

public sealed record PortsSettings
{
    public string CoreBindUrl { get; init; } = "http://127.0.0.1:8484";
    public string OllamaBaseUrl { get; init; } = "http://127.0.0.1:11434";
}

public sealed record DefaultsSettings
{
    public string Role { get; init; } = "general";
    public string Tier { get; init; } = "fast";
}

public sealed record PinsSettings
{
    // strict   = require exact role/tier pin
    // fallback = if missing, resolve to fallback role/tier
    public string Mode { get; init; } = "strict"; // strict|fallback
    public string FallbackRole { get; init; } = "general";
    public string FallbackTier { get; init; } = "fast";
}

public sealed record ProfilesSettings
{
    public string RootWsl { get; init; } = "/mnt/d/Aetherforge/config/profiles";
    public Dictionary<string, string> ByRole { get; init; } = new(); // general|coding|agent -> file name
}

public sealed record GenerationSettings
{
    // role -> tier -> options (values are nullable so settings.yaml can ship "null" placeholders)
    public Dictionary<string, Dictionary<string, OllamaOptions>> ByProfile { get; init; } = new();
}

public sealed record OllamaOptions
{
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public int? NumCtx { get; init; }
    public int? NumPredict { get; init; }
    public double? RepeatPenalty { get; init; }
    public int? Seed { get; init; }
}

public sealed record AutostartSettings
{
    public bool Enabled { get; init; }
    public string? WindowsScheduledTaskName { get; init; }
}

public sealed record BoundarySettings
{
    public List<BridgeRule> BridgeRules { get; init; } = new();
    public BoundaryRoots Roots { get; init; } = new();
    public bool BlockReparsePoints { get; init; } = true;

    public List<string> AllowWriteUnderWsl { get; init; } = new();
    public List<string> AllowReadUnderWsl { get; init; } = new();
}

public sealed record BridgeRule
{
    public string WindowsRoot { get; init; } = "";
    public string WslRoot { get; init; } = "";
}

public sealed record BoundaryRoots
{
    public BoundaryRootsWsl Wsl { get; init; } = new();
}

public sealed record BoundaryRootsWsl
{
    public string Config { get; init; } = "/mnt/d/Aetherforge/config";
    public string Exports { get; init; } = "/mnt/d/Aetherforge/exports";
    public string Logs { get; init; } = "/mnt/d/Aetherforge/logs";
}

public sealed record AgentSettings
{
    public bool Enabled { get; init; }
    public bool RequirePlanApproval { get; init; } = true;
    public List<string> AllowTools { get; init; } = new();
}
