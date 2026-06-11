using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ReplicaPaints;

// Built-in HTTP server providing a browser UI for editing loadouts and
// inspecting bot loadouts without joining the game.
//
// Architecture:
//   - System.Net.HttpListener on a worker thread (no NuGet, no Kestrel
//     dependency — CSSharp plugins ship with whatever .NET 8 BCL gives
//     us, and HttpListener is enough for a single-admin local panel).
//   - Bearer-token gate on every /api/* request. Token is generated on
//     first run and stored in settings.json; the UI prompts for it once
//     and persists it in localStorage.
//   - Read endpoints (catalogs, players, online roster, bot loadouts)
//     are pure file/in-memory reads — safe from the worker thread.
//   - Write endpoint (PUT /api/players/{steamid}) mutates PlayersStore
//     and saves the JSON; no game-thread crossing required.
//   - The single endpoint that does need the game thread —
//     `Utilities.GetPlayers()` for the online roster — is marshalled
//     via Server.NextFrame + ManualResetEventSlim. Tiny window, but
//     it's the only correct way to touch CSSharp entity APIs.
//
// Static UI files live next to the plugin DLL under `wwwroot/`. CSSharp
// stages them via the build's wwwroot copy step; on the live server
// they end up at plugins/ReplicaPaints/wwwroot/.
public sealed class WebServer : IDisposable
{
    // Settings is accessed via a lambda because the plugin reassigns
    // the instance on every reload. The other dependencies (db, players,
    // resolver, slots) are reloaded in-place inside the plugin, so we
    // can hold their references directly.
    private readonly Func<Settings> _settingsFn;
    private readonly PaintsDatabase  _db;
    private readonly PlayersStore    _players;
    private readonly BotLoadoutResolver _resolver;
    private readonly FakeSlotsReader _slots;
    // Apply-service is reassigned each ReloadData(), so capture via lambda
    // — same pattern as Settings.
    private readonly Func<ApplyService?> _applyFn;
    private readonly Action          _reloadAction;
    private readonly string          _moduleDir;

    // Local helper so the rest of the file stays terse.
    private Settings _settings => _settingsFn();

    private HttpListener? _listener;
    private Thread?       _worker;
    private CancellationTokenSource? _cts;
    private string _wwwroot = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WebServer(
        Func<Settings> settingsFn,
        PaintsDatabase db, PlayersStore players,
        BotLoadoutResolver resolver, FakeSlotsReader slots,
        Func<ApplyService?> applyFn,
        string moduleDir,
        Action reloadAction)
    {
        _settingsFn = settingsFn;
        _db = db;
        _players = players;
        _resolver = resolver;
        _slots = slots;
        _applyFn = applyFn;
        _moduleDir = moduleDir;
        _reloadAction = reloadAction;
    }

    public void Start()
    {
        if (!_settings.WebEnabled)
        {
            Log.Info("WebServer: disabled in settings.json");
            return;
        }
        try
        {
            // CSSharp plugins load through a custom AssemblyLoadContext,
            // and Assembly.GetExecutingAssembly().Location is empty in
            // that path. ModuleDirectory (BasePlugin.ModuleDirectory)
            // is the authoritative way to find the plugin folder.
            // Fall back to AppContext.BaseDirectory just in case.
            var baseDir = _moduleDir;
            if (string.IsNullOrEmpty(baseDir))
            {
                try
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(loc))
                        baseDir = Path.GetDirectoryName(loc) ?? "";
                }
                catch { }
                if (string.IsNullOrEmpty(baseDir))
                    baseDir = AppContext.BaseDirectory;
            }
            _wwwroot = Path.Combine(baseDir, "wwwroot");
            if (!Directory.Exists(_wwwroot))
            {
                Log.Warn($"WebServer: wwwroot missing at {_wwwroot}; UI endpoints will return 404, API still works");
            }

            _listener = new HttpListener();
            var prefix = $"http://{_settings.WebBind}:{_settings.WebPort}/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _cts = new CancellationTokenSource();
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Paints-Web" };
            _worker.Start();

            Log.Info($"WebServer: listening on {prefix} (token: {_settings.WebToken[..Math.Min(6, _settings.WebToken.Length)]}…)");
        }
        catch (HttpListenerException ex)
        {
            // EACCES on Linux when port is privileged, or already in use.
            Log.Error($"WebServer start failed ({ex.Message}); panel unavailable until next restart");
            _listener = null;
        }
        catch (Exception ex)
        {
            Log.Error($"WebServer start failed: {ex.Message}");
            _listener = null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel();    } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close();} catch { }
        try { _worker?.Join(500);} catch { }
        _listener = null;
        _worker = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    // -- worker loop -----------------------------------------------------

    private void WorkerLoop()
    {
        var ct = _cts!.Token;
        while (!ct.IsCancellationRequested && _listener!.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) { break; }   // listener stopped
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.BgWarn($"WebServer GetContext: {ex.Message}");
                continue;
            }

            try { HandleRequest(ctx); }
            catch (Exception ex)
            {
                Log.BgWarn($"WebServer handler crashed: {ex.Message}");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";

        // CORS — only matters if someone opens the UI from a different
        // origin. Allow same-origin defaults; explicit allow for /api so
        // a dev can hit it from a local file:// preview if desired.
        resp.Headers["Access-Control-Allow-Origin"]  = "*";
        resp.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, PUT, POST, DELETE, OPTIONS";
        if (req.HttpMethod == "OPTIONS") { resp.StatusCode = 204; resp.Close(); return; }

        // API namespace — gated by token. Anything else is static.
        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!CheckAuth(req)) { WriteJson(resp, 401, new { error = "unauthorized" }); return; }
            RouteApi(ctx, path);
            return;
        }
        ServeStatic(ctx, path);
    }

    // -- auth ------------------------------------------------------------

    private bool CheckAuth(HttpListenerRequest req)
    {
        // Authorization: Bearer <token>  OR  ?token=… (used by the
        // login page during initial probe).
        var hdr = req.Headers["Authorization"];
        if (hdr != null && hdr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var given = hdr.AsSpan(7).Trim().ToString();
            return ConstTimeEquals(given, _settings.WebToken);
        }
        var qt = req.QueryString["token"];
        if (!string.IsNullOrEmpty(qt))
            return ConstTimeEquals(qt, _settings.WebToken);
        return false;
    }

    private static bool ConstTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // -- routing ---------------------------------------------------------

    private void RouteApi(HttpListenerContext ctx, string path)
    {
        var m = ctx.Request.HttpMethod;
        if (path == "/api/catalogs"     && m == "GET")  { HandleCatalogs(ctx); return; }
        if (path == "/api/players"      && m == "GET")  { HandlePlayersList(ctx); return; }
        if (path.StartsWith("/api/players/", StringComparison.Ordinal))
        {
            var rest = path.Substring("/api/players/".Length);
            // Layouts sub-routes:
            //   /api/players/{id}/layouts             GET, POST
            //   /api/players/{id}/layouts/{name}      DELETE
            //   /api/players/{id}/layouts/{name}/activate  POST
            var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "layouts")
            {
                HandleLayouts(ctx, parts, m);
                return;
            }
            if (m == "GET" || m == "PUT" || m == "DELETE")
            {
                HandlePlayerById(ctx, rest, m);
                return;
            }
        }
        if (path == "/api/online"  && m == "GET")  { HandleOnline(ctx); return; }
        if (path == "/api/bots"    && m == "GET")  { HandleBots(ctx); return; }
        if (path == "/api/reload"  && m == "POST") { HandleReload(ctx); return; }
        if (path == "/api/inspect" && m == "POST") { HandleInspect(ctx); return; }
        WriteJson(ctx.Response, 404, new { error = "no such endpoint" });
    }

    /// <summary>Layout management — list / create / activate / delete
    /// the named loadout collections under one SteamID. The "default"
    /// layout is auto-created and can't be deleted; switching to a
    /// non-existent layout returns 404.</summary>
    private void HandleLayouts(HttpListenerContext ctx, string[] parts, string method)
    {
        // parts[0] = steamid, parts[1] = "layouts", parts[2]? = layout name, parts[3]? = "activate"
        if (!ulong.TryParse(parts[0], out var steamId))
        {
            WriteJson(ctx.Response, 400, new { error = "bad SteamID64" });
            return;
        }

        // GET /api/players/{id}/layouts — list everything for this SteamID
        if (parts.Length == 2 && method == "GET")
        {
            var w = _players.GetOrCreateLayouts(steamId);
            WriteJson(ctx.Response, 200, w);
            return;
        }

        // POST /api/players/{id}/layouts — create or overwrite a named layout
        // Body: { "name": "...", "loadout": {...}, "activate": true }
        if (parts.Length == 2 && method == "POST")
        {
            using var body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var raw = body.ReadToEnd();
            try
            {
                var req = JsonSerializer.Deserialize<LayoutCreateRequest>(raw, JsonOpts);
                if (req == null || string.IsNullOrWhiteSpace(req.Name) || req.Loadout == null)
                {
                    WriteJson(ctx.Response, 400, new { error = "expected {name, loadout, activate?}" });
                    return;
                }
                _players.SetLayout(steamId, req.Name.Trim(), req.Loadout, req.Activate);
                _players.Save();
                WriteJson(ctx.Response, 200, _players.GetOrCreateLayouts(steamId));
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 400, new { error = $"parse: {ex.Message}" });
            }
            return;
        }

        // /api/players/{id}/layouts/{name}/activate  (POST)
        if (parts.Length == 4 && parts[3] == "activate" && method == "POST")
        {
            var name = Uri.UnescapeDataString(parts[2]);
            if (!_players.ActivateLayout(steamId, name))
            {
                WriteJson(ctx.Response, 404, new { error = $"no such layout '{name}'" });
                return;
            }
            _players.Save();
            WriteJson(ctx.Response, 200, _players.GetOrCreateLayouts(steamId));
            return;
        }

        // DELETE /api/players/{id}/layouts/{name}
        if (parts.Length == 3 && method == "DELETE")
        {
            var name = Uri.UnescapeDataString(parts[2]);
            if (name == PlayerLayouts.DefaultName)
            {
                WriteJson(ctx.Response, 400, new { error = "the default layout cannot be deleted" });
                return;
            }
            if (!_players.RemoveLayout(steamId, name))
            {
                WriteJson(ctx.Response, 404, new { error = $"no such layout '{name}'" });
                return;
            }
            _players.Save();
            WriteJson(ctx.Response, 200, _players.GetOrCreateLayouts(steamId));
            return;
        }

        WriteJson(ctx.Response, 405, new { error = "method not allowed for this layouts route" });
    }

    private sealed class LayoutCreateRequest
    {
        [JsonPropertyName("name")]     public string?        Name     { get; set; }
        [JsonPropertyName("loadout")]  public PlayerLoadout? Loadout  { get; set; }
        [JsonPropertyName("activate")] public bool           Activate { get; set; }
    }

    // -- API handlers ----------------------------------------------------

    private void HandleCatalogs(HttpListenerContext ctx)
    {
        WriteJson(ctx.Response, 200, new
        {
            weapons    = _db.Weapons,
            knives     = _db.Knives,
            gloves     = _db.Gloves,
            agents     = _db.Agents,
            music_kits = _db.MusicKits,
            pins       = _db.Pins,
            stickers   = _db.Stickers,
            keychains  = _db.Keychains,
            weapon_labels = WeaponLabels,
        });
    }

    private void HandlePlayersList(HttpListenerContext ctx)
    {
        WriteJson(ctx.Response, 200, _players.Snapshot());
    }

    private void HandlePlayerById(HttpListenerContext ctx, string idStr, string method)
    {
        if (!ulong.TryParse(idStr, out var steamId))
        {
            WriteJson(ctx.Response, 400, new { error = "bad SteamID64" });
            return;
        }
        if (method == "GET")
        {
            var pl = _players.TryGet(steamId);
            if (pl == null) { WriteJson(ctx.Response, 404, new { error = "not found" }); return; }
            WriteJson(ctx.Response, 200, pl);
            return;
        }
        if (method == "DELETE")
        {
            _players.Remove(steamId);
            _players.Save();
            WriteJson(ctx.Response, 200, new { ok = true });
            return;
        }
        // PUT: full-loadout replace.
        using var body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var raw = body.ReadToEnd();
        try
        {
            var pl = JsonSerializer.Deserialize<PlayerLoadout>(raw, JsonOpts);
            if (pl == null) { WriteJson(ctx.Response, 400, new { error = "empty body" }); return; }
            _players.Put(steamId, pl);
            _players.Save();
            WriteJson(ctx.Response, 200, pl);
        }
        catch (Exception ex)
        {
            WriteJson(ctx.Response, 400, new { error = $"parse: {ex.Message}" });
        }
    }

    private void HandleOnline(HttpListenerContext ctx)
    {
        // Touching Utilities.GetPlayers from the worker thread is unsafe
        // — entity handles can be torn out from under us. Hop onto the
        // game thread, snapshot, then hop back.
        var done = new ManualResetEventSlim(false);
        List<object> snapshot = new();
        Server.NextFrame(() =>
        {
            try
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.IsHLTV) continue;
                    // p.IsBot lies for Replica-managed slots — Hider flips
                    // m_bFakePlayer to zero so the engine reports them as
                    // humans. The only honest source is the pool managed
                    // byte; treat IsManaged as the authoritative "this is
                    // one of ours" check.
                    bool isManaged = _slots.IsManaged(p.Slot);
                    bool isBot = p.IsBot || isManaged;
                    snapshot.Add(new
                    {
                        slot      = (int)p.Slot,
                        name      = p.PlayerName,
                        steamid   = p.SteamID.ToString(),
                        team      = (int)p.TeamNum,
                        is_bot    = isBot,
                        is_managed_bot = isManaged,
                        pool_name = _slots.ReadName(p.Slot),
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"online snapshot: {ex.Message}"); }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            WriteJson(ctx.Response, 504, new { error = "game-thread snapshot timed out" });
            return;
        }
        WriteJson(ctx.Response, 200, snapshot);
    }

    private void HandleBots(HttpListenerContext ctx)
    {
        // For each currently-managed slot, dump (pool name, resolved
        // loadout). Useful for the "Bots" tab so the admin can see what
        // skin each persona is going to roll.
        var done = new ManualResetEventSlim(false);
        List<object> bots = new();
        Server.NextFrame(() =>
        {
            try
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid) continue;
                    // Same caveat as /api/online — IsBot is unreliable
                    // for Hider-flipped slots. The pool's managed byte is
                    // the only honest source.
                    if (!_slots.IsManaged(p.Slot)) continue;
                    var name = _slots.ReadName(p.Slot);
                    if (string.IsNullOrEmpty(name)) name = p.PlayerName;
                    var resolved = _resolver.Resolve(name);
                    bots.Add(new
                    {
                        slot     = (int)p.Slot,
                        name     = name,
                        persona  = name,
                        weapons  = resolved.Weapons,
                        knife_t  = resolved.KnifeT,
                        knife_ct = resolved.KnifeCT,
                        gloves_t = resolved.GlovesT,
                        gloves_ct= resolved.GlovesCT,
                        agent_t  = resolved.AgentT,
                        agent_ct = resolved.AgentCT,
                        music_kit = resolved.MusicKit,
                        pin_t    = resolved.PinT,
                        pin_ct   = resolved.PinCT,
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"bots snapshot: {ex.Message}"); }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            WriteJson(ctx.Response, 504, new { error = "game-thread snapshot timed out" });
            return;
        }
        WriteJson(ctx.Response, 200, bots);
    }

    /// <summary>Web-panel "Inspect in-game" — applies the trial paint to
    /// the requesting player's live weapon and fires the inspect anim.
    /// Body: { "steamid": "76561...", "weapon_def": 7, "paint": 1100,
    ///         "seed": 0, "wear": 0.01, "stattrak": -1, "nametag": "" }
    /// </summary>
    private void HandleInspect(HttpListenerContext ctx)
    {
        using var body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var raw = body.ReadToEnd();
        InspectRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<InspectRequest>(raw, JsonOpts);
        }
        catch (Exception ex)
        {
            WriteJson(ctx.Response, 400, new { error = $"parse: {ex.Message}" });
            return;
        }
        if (req == null || string.IsNullOrEmpty(req.SteamId)
            || !ulong.TryParse(req.SteamId, out var steamId) || req.WeaponDef <= 0)
        {
            WriteJson(ctx.Response, 400, new { error = "expected {steamid, weapon_def, paint, seed?, wear?}" });
            return;
        }
        var apply = _applyFn();
        if (apply == null)
        {
            WriteJson(ctx.Response, 503, new { error = "apply service not ready" });
            return;
        }

        // Hop to game thread — schema writes are main-thread-only.
        InspectResult result = InspectResult.NotAlive;
        CCSPlayerController? target = null;
        var done = new ManualResetEventSlim(false);
        Exception? err = null;
        Server.NextFrame(() =>
        {
            try
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid) continue;
                    if (p.SteamID != steamId) continue;
                    target = p;
                    break;
                }
                if (target == null) { result = InspectResult.NotAlive; return; }
                var candidate = new WeaponLoadout
                {
                    Paint    = req.Paint,
                    Seed     = req.Seed,
                    Wear     = req.Wear,
                    StatTrak = req.StatTrak,
                    Nametag  = req.Nametag ?? "",
                };
                result = apply.Inspect(target, req.WeaponDef, candidate);
            }
            catch (Exception ex) { err = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(3)))
        {
            WriteJson(ctx.Response, 504, new { error = "inspect timed out" });
            return;
        }
        if (err != null)
        {
            WriteJson(ctx.Response, 500, new { error = err.Message });
            return;
        }
        switch (result)
        {
            case InspectResult.Applied:
                WriteJson(ctx.Response, 200, new { ok = true });
                return;
            case InspectResult.NotHolding:
                WriteJson(ctx.Response, 409, new { error = "Switch to that weapon first" });
                return;
            case InspectResult.NotAlive:
                WriteJson(ctx.Response, 409, new { error = "You need to be alive on the server" });
                return;
            default:
                WriteJson(ctx.Response, 400, new { error = "no such defindex" });
                return;
        }
    }

    private sealed class InspectRequest
    {
        [JsonPropertyName("steamid")]    public string? SteamId   { get; set; }
        [JsonPropertyName("weapon_def")] public int     WeaponDef { get; set; }
        [JsonPropertyName("paint")]      public int     Paint     { get; set; }
        [JsonPropertyName("seed")]       public int     Seed      { get; set; }
        [JsonPropertyName("wear")]       public float   Wear      { get; set; } = 0.01f;
        [JsonPropertyName("stattrak")]   public int     StatTrak  { get; set; } = -1;
        [JsonPropertyName("nametag")]    public string? Nametag   { get; set; }
    }

    private void HandleReload(HttpListenerContext ctx)
    {
        // The reload thunk re-runs LoadEverything(), which re-registers
        // console commands and re-arms event handlers — those calls are
        // game-thread-only ("Invoked on a non-main thread" otherwise).
        // Hop onto the game thread, wait for completion, then respond.
        var done = new ManualResetEventSlim(false);
        Exception? err = null;
        Server.NextFrame(() =>
        {
            try { _reloadAction(); }
            catch (Exception ex) { err = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            WriteJson(ctx.Response, 504, new { error = "reload timed out" });
            return;
        }
        if (err != null)
        {
            WriteJson(ctx.Response, 500, new { error = err.Message });
            return;
        }
        WriteJson(ctx.Response, 200, new { ok = true });
    }

    // -- static ----------------------------------------------------------

    private void ServeStatic(HttpListenerContext ctx, string path)
    {
        if (string.IsNullOrEmpty(_wwwroot)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        var rel = path == "/" ? "index.html" : path.TrimStart('/');
        // Block path traversal — naive but enough for a single-file
        // wwwroot tree.
        if (rel.Contains("..", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 400; ctx.Response.Close(); return;
        }
        var full = Path.Combine(_wwwroot, rel);
        if (!File.Exists(full))
        {
            // SPA fallback — if the path looks like a route (no
            // extension), serve index.html so client-side routing wins.
            if (!Path.HasExtension(rel))
            {
                full = Path.Combine(_wwwroot, "index.html");
                if (!File.Exists(full)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            }
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        }
        var bytes = File.ReadAllBytes(full);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = ContentTypeFor(full);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static string ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".js"   => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg"  => "image/svg+xml",
            ".png"  => "image/png",
            ".ico"  => "image/x-icon",
            _       => "application/octet-stream",
        };
    }

    private static void WriteJson(HttpListenerResponse resp, int code, object body)
    {
        resp.StatusCode = code;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    // -- labels — duplicated from ChatMenuController so the UI can label
    // weapon defindexes without an extra round-trip to the server.
    private static readonly Dictionary<int, string> WeaponLabels = new()
    {
        {  1, "Desert Eagle"   }, {  2, "Dual Berettas"  }, {  3, "Five-SeveN"  },
        {  4, "Glock-18"       }, {  7, "AK-47"          }, {  8, "AUG"         },
        {  9, "AWP"            }, { 10, "FAMAS"          }, { 11, "G3SG1"       },
        { 13, "Galil AR"       }, { 14, "M249"           }, { 16, "M4A4"        },
        { 17, "MAC-10"         }, { 19, "P90"            }, { 23, "MP5-SD"      },
        { 24, "UMP-45"         }, { 25, "XM1014"         }, { 26, "PP-Bizon"    },
        { 27, "MAG-7"          }, { 28, "Negev"          }, { 29, "Sawed-Off"   },
        { 30, "Tec-9"          }, { 31, "Zeus x27"       }, { 32, "P2000"       },
        { 33, "MP7"            }, { 34, "MP9"            }, { 35, "Nova"        },
        { 36, "P250"           }, { 38, "SCAR-20"        }, { 39, "SG 553"      },
        { 40, "SSG 08"         }, { 60, "M4A1-S"         }, { 61, "USP-S"       },
        { 63, "CZ75-Auto"      }, { 64, "R8 Revolver"    },
        { 500, "Bayonet"       }, { 503, "Classic Knife" }, { 505, "Flip Knife"      },
        { 506, "Gut Knife"     }, { 507, "Karambit"      }, { 508, "M9 Bayonet"      },
        { 509, "Huntsman Knife"}, { 512, "Falchion Knife"}, { 514, "Bowie Knife"     },
        { 515, "Butterfly"     }, { 516, "Shadow Daggers"}, { 517, "Paracord"        },
        { 518, "Survival Knife"}, { 519, "Ursus Knife"   }, { 520, "Navaja Knife"    },
        { 521, "Nomad Knife"   }, { 522, "Stiletto"      }, { 523, "Talon Knife"     },
        { 525, "Skeleton Knife"}, { 526, "Kukri Knife"   },
    };
}
