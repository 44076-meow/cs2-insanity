using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;

namespace ReplicaPaints;

// Plugin-wide settings, loaded from configs/settings.json. Distinct from
// per-player loadouts (those live in data/players.json) and from the
// paintkit catalogs (configs/weapons_paints.json etc). On first run the
// file is created with built-in defaults.
public sealed class Settings
{
    [JsonPropertyName("enable_weapons")]
    public bool EnableWeapons { get; set; } = true;

    // DEFAULT OFF (#11): knife skins go through AcceptInput("ChangeSubclass"),
    // a weapon class/model swap that rebuilds the viewmodel animation graph —
    // the engine use-after-free crashes walking it (libanimationsystem
    // +0x60cdf1). CONFIRMED: 29min/21rounds crash-free with this off while
    // weapon/glove/sticker skins stayed on; crashed every ~10min with it on.
    // Re-enabling needs a SetModel/ChangeSubclass-free knife-skin path.
    [JsonPropertyName("enable_knives")]
    public bool EnableKnives { get; set; } = false;

    [JsonPropertyName("enable_gloves")]
    public bool EnableGloves { get; set; } = true;

    /// <summary>Apply custom agent (player) models via pawn.SetModel. DEFAULT
    /// OFF — SetModel rebuilds the animation graph and the engine crashes
    /// walking it (use-after-free, #11; 18+ identical minidumps). Weapon/knife/
    /// glove/sticker skins are independent of this. Leave off unless a
    /// SetModel-free agent path is implemented.</summary>
    [JsonPropertyName("enable_agents")]
    public bool EnableAgents { get; set; } = false;

    /// <summary>True = apply skins to Replica-managed bots too.
    /// False = only real human players.</summary>
    [JsonPropertyName("apply_to_revive_bots")]
    public bool ApplyToReviveBots { get; set; } = true;

    /// <summary>Admin flag required to use !ws / !knife / !gloves.
    /// Defaults to "@css/root"; CSSharp permission syntax — see
    /// configs/admins.json on the live server.</summary>
    [JsonPropertyName("admin_flag")]
    public string AdminFlag { get; set; } = "@css/root";

    /// <summary>info | debug | warn | error.</summary>
    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "info";

    /// <summary>Pool mmap path; only override if Replica uses a non-default
    /// location.</summary>
    [JsonPropertyName("pool_path")]
    public string PoolPath { get; set; } = FakeSlotsReader.DefaultPath;

    /// <summary>Chat menu page size — how many entries to show before the
    /// "Next / Prev" footer. CSSharp ChatMenu hard-limits ~9 items per
    /// page anyway.</summary>
    [JsonPropertyName("menu_page_size")]
    public int MenuPageSize { get; set; } = 8;

    /// <summary>Web panel — built-in HTTP server with a browser UI for
    /// editing loadouts without joining the game. See README.</summary>
    [JsonPropertyName("web_enabled")]
    public bool WebEnabled { get; set; } = true;

    /// <summary>IP to bind. "127.0.0.1" = local only (safe default).
    /// Change to "0.0.0.0" only if you also want remote access — the
    /// bearer token gate is mandatory either way.</summary>
    [JsonPropertyName("web_bind")]
    public string WebBind { get; set; } = "127.0.0.1";

    [JsonPropertyName("web_port")]
    public int WebPort { get; set; } = 27018;

    /// <summary>Bearer token required on every API call. Auto-generated
    /// on first run if left empty. Login screen of the web UI asks for
    /// this token once and stashes it in browser localStorage.</summary>
    [JsonPropertyName("web_token")]
    public string WebToken { get; set; } = "";

    // -- IO --------------------------------------------------------------

    public static string DefaultPath =>
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp",
                     "configs", "plugins", "ReplicaPaints", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                var fresh = new Settings();
                fresh.EnsureWebToken();
                Save(fresh, path);
                Log.Info($"settings.json not found — wrote defaults to {path}");
                return fresh;
            }
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
            if (s == null) throw new InvalidOperationException("deserialize returned null");
            // Backfill an empty token so a hand-edited settings.json still
            // gets one and the user doesn't have to invent a string.
            if (s.EnsureWebToken()) Save(s, path);
            return s;
        }
        catch (Exception ex)
        {
            Log.Error($"settings.json load failed ({ex.Message}); using defaults in-memory only");
            return new Settings();
        }
    }

    /// <summary>Generate a random URL-safe token if WebToken is empty.
    /// Returns true if a new token was assigned (so the caller can
    /// persist).</summary>
    public bool EnsureWebToken()
    {
        if (!string.IsNullOrWhiteSpace(WebToken)) return false;
        Span<byte> buf = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        WebToken = Convert.ToBase64String(buf).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return true;
    }

    public static void Save(Settings s, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error($"settings.json save failed: {ex.Message}");
        }
    }
}
