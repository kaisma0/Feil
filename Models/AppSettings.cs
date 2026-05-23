using System;
using Feil.Services;

namespace Feil.Models;

public sealed class AppSettings
{
    // ── Download ──
    public string InstallPath { get; set; } = DefaultInstallPathService.GetDefaultInstallPath();

    // ── Startup ──
    public bool LaunchOnStartup    { get; set; }
    public bool StartMinimised     { get; set; }
    public bool AutoResumeOnStart  { get; set; } = true;
    public bool SkipDepotSelection { get; set; } = false;

    // ── Steamstub ──
    public bool AutoApplySteamstub { get; set; } = false;

    // ── Steam Achievements ──
    public uint SteamAccountId     { get; set; }

}
