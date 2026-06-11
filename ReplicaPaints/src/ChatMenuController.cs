using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;

namespace ReplicaPaints;

// Admin-gated chat menus for picking weapon paints, knives, and gloves.
// Each command opens a paginated ChatMenu rooted at the relevant catalog
// from PaintsDatabase. On selection we mutate the player's stored
// loadout in PlayersStore and persist to disk.
//
// Permission gate defaults to "@css/root" (see Settings.AdminFlag).
// Non-admins get a chat refusal line so it's clear the command exists
// but isn't open to them.
public sealed class ChatMenuController
{
    private readonly BasePlugin     _plugin;
    private readonly Settings       _settings;
    private readonly PaintsDatabase _db;
    private readonly PlayersStore   _players;

    // Per-player paging cursor. ChatMenu doesn't paginate itself, so
    // we rebuild a new ChatMenu when the user picks "Next" / "Prev".
    private readonly Dictionary<int, PageState> _pageBySlot = new();

    public ChatMenuController(
        BasePlugin plugin, Settings settings,
        PaintsDatabase db, PlayersStore players)
    {
        _plugin   = plugin;
        _settings = settings;
        _db       = db;
        _players  = players;
    }

    public void Register()
    {
        _plugin.AddCommand("css_ws",     "ReplicaPaints: pick weapon paint", OnWsCommand);
        _plugin.AddCommand("css_knife",  "ReplicaPaints: pick knife",        OnKnifeCommand);
        _plugin.AddCommand("css_gloves", "ReplicaPaints: pick gloves",       OnGlovesCommand);
    }

    // -- entry points (admin-gated) -------------------------------------

    private void OnWsCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!RequireAdmin(player)) return;
        if (player == null) return;
        OpenWeaponList(player);
    }

    private void OnKnifeCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!RequireAdmin(player)) return;
        if (player == null) return;
        OpenKnifeList(player);
    }

    private void OnGlovesCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!RequireAdmin(player)) return;
        if (player == null) return;
        OpenGloveList(player);
    }

    // -- menus -----------------------------------------------------------

    // Top-level: which weapon to skin. We list only weapons that have at
    // least one entry in the catalog, so the user never lands on a dead
    // submenu.
    private void OpenWeaponList(CCSPlayerController player)
    {
        var weapons = _db.WeaponDefindexes
            .Where(d => !IsKnifeDefindex(d))
            .OrderBy(d => d)
            .Select(d => (Defindex: d, Label: WeaponLabel(d)))
            .ToList();
        if (weapons.Count == 0)
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} weapons_paints.json is empty.");
            return;
        }
        OpenPaginated(player, "Pick weapon", weapons.Count,
            (i) => weapons[i].Label,
            (i) => OpenWeaponPaints(player, weapons[i].Defindex));
    }

    private void OpenWeaponPaints(CCSPlayerController player, int defindex)
    {
        var paints = _db.ForWeapon(defindex);
        if (paints.Count == 0)
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} no paints for defindex {defindex}.");
            return;
        }
        OpenPaginated(player, $"Pick paint — {WeaponLabel(defindex)}", paints.Count,
            (i) => paints[i].Name.Length > 0 ? paints[i].Name : $"paint #{paints[i].Paint}",
            (i) =>
            {
                var p = paints[i];
                var pl = _players.GetOrCreate(player.SteamID);
                pl.Weapons[defindex] = new WeaponLoadout
                {
                    Paint    = p.Paint,
                    Seed     = 0,
                    Wear     = 0.01f,
                    StatTrak = -1,
                };
                _players.Save();
                player.PrintToChat($" {ChatColors.Green}[Paints]{ChatColors.Default} {WeaponLabel(defindex)} → {p.Name} (paint {p.Paint}).");
            });
    }

    private void OpenKnifeList(CCSPlayerController player)
    {
        if (_db.Knives.Count == 0)
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} knives.json is empty.");
            return;
        }
        var team = (CsTeam)player.TeamNum;
        if (team is CsTeam.None or CsTeam.Spectator)
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} join a team first.");
            return;
        }
        OpenPaginated(player, $"Pick knife ({team})", _db.Knives.Count,
            (i) => _db.Knives[i].Name,
            (i) =>
            {
                var k = _db.Knives[i];
                var pl = _players.GetOrCreate(player.SteamID);
                if (team == CsTeam.Terrorist) pl.KnifeT  = k.Defindex;
                else                          pl.KnifeCT = k.Defindex;
                _players.Save();
                player.PrintToChat($" {ChatColors.Green}[Paints]{ChatColors.Default} {team} knife → {k.Name}.");
                // Offer to pick a paint right away if the catalog has any
                // entries for this knife defindex.
                if (_db.ForWeapon(k.Defindex).Count > 0)
                    OpenWeaponPaints(player, k.Defindex);
            });
    }

    private void OpenGloveList(CCSPlayerController player)
    {
        if (_db.Gloves.Count == 0)
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} gloves.json is empty.");
            return;
        }
        var team = (CsTeam)player.TeamNum;
        if (team is CsTeam.None or CsTeam.Spectator)
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} join a team first.");
            return;
        }
        OpenPaginated(player, $"Pick gloves ({team})", _db.Gloves.Count,
            (i) => _db.Gloves[i].Name,
            (i) =>
            {
                var g = _db.Gloves[i];
                var pl = _players.GetOrCreate(player.SteamID);
                var loadout = new GloveLoadout
                {
                    Defindex = g.Defindex,
                    Paint    = g.Paint,
                    Seed     = 0,
                    Wear     = 0.05f,
                };
                if (team == CsTeam.Terrorist) pl.GlovesT  = loadout;
                else                          pl.GlovesCT = loadout;
                _players.Save();
                player.PrintToChat($" {ChatColors.Green}[Paints]{ChatColors.Default} {team} gloves → {g.Name}.");
            });
    }

    // -- pagination helper ----------------------------------------------

    private void OpenPaginated(
        CCSPlayerController player,
        string title,
        int total,
        Func<int, string> labelFor,
        Action<int> onPick)
    {
        var state = new PageState { Title = title, Total = total, Page = 0 };
        _pageBySlot[player.Slot] = state;
        DrawPage(player, state, labelFor, onPick);
    }

    private void DrawPage(
        CCSPlayerController player,
        PageState state,
        Func<int, string> labelFor,
        Action<int> onPick)
    {
        int pageSize = Math.Max(1, _settings.MenuPageSize);
        int totalPages = (state.Total + pageSize - 1) / pageSize;
        state.Page = Math.Clamp(state.Page, 0, Math.Max(0, totalPages - 1));
        int start = state.Page * pageSize;
        int end   = Math.Min(state.Total, start + pageSize);

        var menu = new ChatMenu($"{state.Title} ({state.Page + 1}/{Math.Max(1, totalPages)})");
        for (int i = start; i < end; i++)
        {
            int captured = i;
            menu.AddMenuOption(labelFor(i), (_, _) => onPick(captured));
        }
        if (state.Page > 0)
        {
            menu.AddMenuOption("<- Prev", (_, _) =>
            {
                state.Page--;
                DrawPage(player, state, labelFor, onPick);
            });
        }
        if (state.Page < totalPages - 1)
        {
            menu.AddMenuOption("Next ->", (_, _) =>
            {
                state.Page++;
                DrawPage(player, state, labelFor, onPick);
            });
        }
        MenuManager.OpenChatMenu(player, menu);
    }

    private sealed class PageState
    {
        public string Title = "";
        public int    Total;
        public int    Page;
    }

    // -- permission gate -------------------------------------------------

    private bool RequireAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot)
        {
            return false;
        }
        if (!AdminManager.PlayerHasPermissions(player, _settings.AdminFlag))
        {
            player.PrintToChat($" {ChatColors.Red}[Paints]{ChatColors.Default} admin only.");
            return false;
        }
        return true;
    }

    // -- labels ----------------------------------------------------------

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
    };

    private static string WeaponLabel(int defindex)
    {
        return WeaponLabels.TryGetValue(defindex, out var s) ? s : $"weapon #{defindex}";
    }

    private static bool IsKnifeDefindex(int d) => d >= 500 && d < 600;
}
