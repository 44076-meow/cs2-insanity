// ReplicaPaints web panel — single-page UI, no framework. Reads/writes
// the plugin's REST API on the same host:port.
//
// State boundaries:
//   - token in localStorage
//   - catalogs cached once per page load
//   - skin-images map (defindex+paint -> CDN URL) fetched once from
//     ByMykel's CSGO-API and cached in localStorage so subsequent
//     loads don't refetch the ~5 MB JSON
//   - current editor target (steamId + working PlayerLoadout) lives in
//     module-local vars; nothing else mutates global state

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

const state = {
  token: localStorage.getItem("paints_token") || "",
  catalogs: null,        // { weapons, knives, gloves, weapon_labels }
  weaponByDef: new Map(),// defindex -> list of catalog rows
  knifeByDef: new Map(),
  imageByKey: new Map(), // "defindex:paint" -> image URL (populated from /api/catalogs)
  online: [],            // last online roster
  stored: {},            // steamid -> loadout
  currentId: null,
  currentLoadout: null,
  currentLayouts: null,  // { active, layouts: { name -> PlayerLoadout } } for the currently-opened SteamID
};

// -- HTTP helpers ----------------------------------------------------

async function api(path, opts = {}) {
  const headers = { Authorization: `Bearer ${state.token}` };
  if (opts.body && !opts.headers?.["Content-Type"]) headers["Content-Type"] = "application/json";
  const r = await fetch(path, { ...opts, headers: { ...headers, ...(opts.headers || {}) } });
  if (r.status === 401) { kickToLogin("token rejected"); throw new Error("401"); }
  if (!r.ok) {
    let body = "";
    try { body = JSON.stringify(await r.json()); } catch {}
    throw new Error(`${r.status}: ${body}`);
  }
  return r.json();
}

function kickToLogin(msg) {
  state.token = "";
  localStorage.removeItem("paints_token");
  $("#app").classList.add("hidden");
  $("#login").classList.remove("hidden");
  $("#login-err").textContent = msg || "";
}

// -- Login -----------------------------------------------------------

$("#login-btn").addEventListener("click", async () => {
  const t = $("#token-input").value.trim();
  if (!t) return;
  state.token = t;
  $("#login-err").textContent = "";
  try {
    await api("/api/catalogs");
    localStorage.setItem("paints_token", t);
    boot();
  } catch (e) {
    $("#login-err").textContent = "Wrong token.";
  }
});
$("#token-input").addEventListener("keydown", (e) => {
  if (e.key === "Enter") $("#login-btn").click();
});
$("#logout-btn").addEventListener("click", () => kickToLogin(""));

// -- Boot ------------------------------------------------------------

async function boot() {
  $("#login").classList.add("hidden");
  $("#app").classList.remove("hidden");
  try {
    state.catalogs = await api("/api/catalogs");
    indexCatalogs();
    await Promise.all([
      refreshOnline(),
      refreshStored(),
    ]);
    setupTabs();
    renderRoster();
    fillCatalogTab();
    fillBotsTab();
    setStatus("ok", "connected");
    // Auto-refresh online roster every 5 s — cheap, single endpoint.
    setInterval(refreshOnline, 5000);
  } catch (e) {
    setStatus("error", e.message);
    console.error(e);
  }
}

function indexCatalogs() {
  // Build defindex -> entries, plus a (defindex,paint) -> image-url map.
  // The image URLs live inside each catalog entry — earlier versions of
  // this UI hit bymykel.github.io directly, but that 301s to bymykel.com
  // which doesn't send Access-Control-Allow-Origin and blocked all
  // previews. Now the C# importer bakes URLs into the catalog files.
  state.weaponByDef.clear();
  state.imageByKey.clear();
  for (const e of state.catalogs.weapons) {
    if (!state.weaponByDef.has(e.weapon_defindex))
      state.weaponByDef.set(e.weapon_defindex, []);
    state.weaponByDef.get(e.weapon_defindex).push(e);
    if (e.image) state.imageByKey.set(`${e.weapon_defindex}:${e.paint}`, e.image);
  }
  for (const g of state.catalogs.gloves) {
    if (g.image) state.imageByKey.set(`${g.defindex}:${g.paint}`, g.image);
  }
}

function setStatus(kind, text) {
  const pill = $("#status-pill");
  pill.className = "pill " + (kind || "");
  pill.textContent = text;
}

// Token boot logic.
if (state.token) {
  // try silently first
  api("/api/catalogs").then(() => boot()).catch(() => kickToLogin(""));
} else {
  kickToLogin("");
}

// -- Tabs ------------------------------------------------------------

function setupTabs() {
  $$(".tab").forEach((b) => {
    b.addEventListener("click", () => {
      $$(".tab").forEach((x) => x.classList.remove("active"));
      $$(".tab-panel").forEach((x) => x.classList.remove("active"));
      b.classList.add("active");
      $(`#tab-${b.dataset.tab}`).classList.add("active");
      if (b.dataset.tab === "bots") fillBotsTab();
    });
  });
  $("#reload-btn").addEventListener("click", async () => {
    setStatus("warn", "reloading…");
    try {
      await api("/api/reload", { method: "POST" });
      // After a reload we may have new catalogs / new players.json.
      state.catalogs = await api("/api/catalogs");
      indexCatalogs();
      await Promise.all([refreshOnline(), refreshStored()]);
      renderRoster();
      fillCatalogTab();
      setStatus("ok", "reloaded");
    } catch (e) {
      setStatus("error", e.message);
    }
  });
  $("#manual-pick-btn").addEventListener("click", () => {
    const id = $("#manual-steamid").value.trim();
    if (/^\d{17}$/.test(id)) openEditor(id, true);
    else alert("SteamID64 must be a 17-digit number.");
  });
}

// -- Roster (left sidebar) ------------------------------------------

async function refreshOnline() {
  try {
    state.online = await api("/api/online");
    renderRoster();
  } catch (e) {
    setStatus("error", e.message);
  }
}

async function refreshStored() {
  state.stored = await api("/api/players");
}

function renderRoster() {
  const online = $("#online-list");
  online.innerHTML = "";
  for (const p of state.online) {
    const li = document.createElement("li");
    const cls = !p.is_bot ? "human" : (p.is_managed_bot ? "bot" : "engine");
    li.className = cls;
    if (p.steamid === state.currentId) li.classList.add("active");
    const display = (p.is_bot && p.pool_name) ? p.pool_name : p.name;
    li.innerHTML = `<span>${escapeHtml(display)}</span><span class="badge">${cls}</span>`;
    li.addEventListener("click", () => openEditor(p.steamid, !p.is_bot, p));
    online.appendChild(li);
  }
  const stored = $("#stored-list");
  stored.innerHTML = "";
  const onlineIds = new Set(state.online.map((p) => p.steamid));
  for (const id of Object.keys(state.stored)) {
    if (onlineIds.has(id)) continue;  // already shown above
    const li = document.createElement("li");
    li.className = "human";
    if (id === state.currentId) li.classList.add("active");
    li.innerHTML = `<span>${id}</span><span class="badge">offline</span>`;
    li.addEventListener("click", () => openEditor(id, true));
    stored.appendChild(li);
  }
}

// -- Editor ----------------------------------------------------------

async function openEditor(steamId, allowEdit, rosterEntry) {
  state.currentId = steamId;
  renderRoster();
  $("#editor-empty").classList.add("hidden");
  $("#editor").classList.remove("hidden");

  if (rosterEntry && rosterEntry.is_bot) {
    // Bot — read-only summary, just show resolved loadout (use the
    // /api/bots endpoint to get the resolver's verdict).
    $("#editor-title").textContent = rosterEntry.pool_name || rosterEntry.name;
    $("#editor-subtitle").textContent = rosterEntry.is_managed_bot
      ? "managed bot (loadout = SHA-256(name), read-only)"
      : "engine bot (not painted)";
    state.currentLoadout = null;
    state.currentLayouts = null;
    $("#editor-delete").disabled = true;
    $("#editor-save").disabled = true;
    setLayoutBarVisible(false);
    // Show resolver output in the weapons list.
    const bots = await api("/api/bots");
    const me = bots.find((b) => b.slot === rosterEntry.slot);
    if (me) renderEditorState(me, /*readonly*/ true);
    return;
  }

  // Human (online or offline manual).
  $("#editor-save").disabled = false;
  $("#editor-delete").disabled = false;
  setLayoutBarVisible(true);

  // Fetch the full layouts wrapper so we can render the layout dropdown
  // and let the user switch between presets. GetOrCreateLayouts on the
  // backend always returns at least the default entry, so this won't 404.
  let layouts;
  try {
    layouts = await api(`/api/players/${steamId}/layouts`);
  } catch (e) {
    layouts = { active: "default", layouts: { default: emptyLoadout() } };
  }
  state.currentLayouts = layouts;
  const activeName = layouts.active || "default";
  const loadout = layouts.layouts?.[activeName] || emptyLoadout();
  state.currentLoadout = loadout;
  if (rosterEntry && !rosterEntry.is_bot) {
    $("#editor-title").textContent = rosterEntry.name;
    $("#editor-subtitle").textContent = `SteamID64 ${steamId}`;
  } else {
    $("#editor-title").textContent = `SteamID64 ${steamId}`;
    $("#editor-subtitle").textContent = "offline / manual";
  }
  renderLayoutBar();
  renderEditorState(loadout, false);
}

function setLayoutBarVisible(yes) {
  $(".layouts-bar").classList.toggle("hidden", !yes);
}

// Repopulate the layout <select> from state.currentLayouts and toggle
// the delete button (default layout is undeletable, so we grey it out
// when default is active).
function renderLayoutBar() {
  const sel = $("#layout-select");
  sel.innerHTML = "";
  const w = state.currentLayouts;
  if (!w || !w.layouts) return;
  const names = Object.keys(w.layouts).sort();
  for (const n of names) {
    const opt = document.createElement("option");
    opt.value = n;
    opt.textContent = n;
    if (n === w.active) opt.selected = true;
    sel.appendChild(opt);
  }
  $("#layout-delete").disabled = (w.active === "default");
}

function emptyLoadout() {
  return {
    weapons: {},
    knives_t: 0, knives_ct: 0,
    gloves_t: null, gloves_ct: null,
    agent_t: 0, agent_ct: 0,
    music_kit: 0,
    pin_t: 0, pin_ct: 0,
  };
}

// Render weapons list + knife / glove slot buttons. Works for both
// editable (human PlayerLoadout) and read-only (bot resolved loadout)
// inputs — readonly mode just hides Pick/Remove buttons.
function renderEditorState(loadout, readonly) {
  // Weapons.
  const wlist = $("#weapons-list");
  wlist.innerHTML = "";
  const w = loadout.weapons || {};
  const defs = Object.keys(w).sort((a, b) => Number(a) - Number(b));
  for (const defStr of defs) {
    const def = Number(defStr);
    wlist.appendChild(weaponRow(def, w[defStr], readonly));
  }
  $("#weapons-add").classList.toggle("hidden", readonly);

  // Knives.
  setSlotLabel("slot-knife-t",  loadout.knives_t || loadout.knife_t  || 0, knifeLabel);
  setSlotLabel("slot-knife-ct", loadout.knives_ct|| loadout.knife_ct || 0, knifeLabel);
  const knifeT  = loadout.knives_t  || loadout.knife_t  || 0;
  const knifeCT = loadout.knives_ct || loadout.knife_ct || 0;
  setPaintLabel("slot-knife-t-paint",  knifeT,  loadout);
  setPaintLabel("slot-knife-ct-paint", knifeCT, loadout);

  // Gloves.
  setGloveLabel("slot-gloves-t",  loadout.gloves_t);
  setGloveLabel("slot-gloves-ct", loadout.gloves_ct);
  if (!readonly) {
    renderGloveAttrs("slot-gloves-t",  loadout.gloves_t,  "gloves_t");
    renderGloveAttrs("slot-gloves-ct", loadout.gloves_ct, "gloves_ct");
  }

  // Agents.
  setAgentLabel("slot-agent-t",  loadout.agent_t  || 0);
  setAgentLabel("slot-agent-ct", loadout.agent_ct || 0);

  // Music kit + pins.
  setSimpleLabel("slot-music-kit", loadout.music_kit || 0, musicKitLabel);
  setSimpleLabel("slot-pin-t",     loadout.pin_t     || 0, pinLabel);
  setSimpleLabel("slot-pin-ct",    loadout.pin_ct    || 0, pinLabel);

  $$(".slot-btn").forEach((b) => {
    b.disabled = readonly;
    b.onclick = readonly ? null : () => onSlotClick(b.dataset.slot);
  });
}

function setSimpleLabel(id, defindex, namer) {
  const btn = $("#" + id);
  btn.textContent = defindex ? namer(defindex) : "—";
}
function musicKitLabel(defindex) {
  const e = (state.catalogs.music_kits || []).find((x) => x.defindex === defindex);
  return e ? e.name : `music #${defindex}`;
}
function pinLabel(defindex) {
  const e = (state.catalogs.pins || []).find((x) => x.defindex === defindex);
  return e ? e.name : `pin #${defindex}`;
}
function stickerLabel(defindex) {
  if (!defindex) return "— empty —";
  const e = (state.catalogs.stickers || []).find((x) => x.defindex === defindex);
  return e ? e.name : `sticker #${defindex}`;
}
function keychainLabel(defindex) {
  if (!defindex) return "— none —";
  const e = (state.catalogs.keychains || []).find((x) => x.defindex === defindex);
  return e ? e.name : `keychain #${defindex}`;
}
function stickerImage(defindex) {
  const e = (state.catalogs.stickers || []).find((x) => x.defindex === defindex);
  return e?.image || "";
}
function keychainImage(defindex) {
  const e = (state.catalogs.keychains || []).find((x) => x.defindex === defindex);
  return e?.image || "";
}

function weaponRow(def, loadout, readonly) {
  const div = document.createElement("div");
  div.className = "slot-row";
  const label = state.catalogs.weapon_labels[def] || `def #${def}`;
  const skinName = skinNameFor(def, loadout.paint) || `paint #${loadout.paint}`;
  const stOn = (loadout.stattrak ?? -1) >= 0;
  const stCount = stOn ? loadout.stattrak : 0;
  div.innerHTML = `
    <span class="weapon-label">${escapeHtml(label)}</span>
    <img class="skin-preview" src="${escapeAttr(imageUrl(def, loadout.paint))}" onerror="this.style.visibility='hidden'" />
    <div class="row-main">
      <div class="skin-name">${escapeHtml(skinName)}</div>
      ${readonly ? "" : `
        <div class="row-attrs">
          <label class="attr">
            <span>Wear</span>
            <input class="attr-wear" type="range" min="0" max="1" step="0.001" value="${loadout.wear ?? 0.01}" />
            <output class="attr-wear-out">${(loadout.wear ?? 0.01).toFixed(3)}</output>
          </label>
          <label class="attr">
            <span>Seed</span>
            <input class="attr-seed" type="number" min="0" max="1000" step="1" value="${loadout.seed ?? 0}" />
          </label>
          <label class="attr attr-st">
            <input class="attr-stattrak" type="checkbox" ${stOn ? "checked" : ""} />
            <span>StatTrak</span>
            <input class="attr-stcount" type="number" min="0" step="1" value="${stCount}" ${stOn ? "" : "disabled"} />
          </label>
          <label class="attr attr-nt">
            <span>Name</span>
            <input class="attr-nametag" type="text" maxlength="20" placeholder="—" value="${escapeAttr(loadout.nametag || "")}" />
          </label>
        </div>
      `}
    </div>
    ${readonly ? "" : `
      <button class="row-change">Change</button>
      <button class="row-stickers" title="Stickers + keychain">🪪</button>
      <button class="row-del" title="remove">×</button>
    `}
  `;
  if (!readonly) {
    const ld = state.currentLoadout.weapons[def];
    const wearInput = $(".attr-wear", div);
    const wearOut   = $(".attr-wear-out", div);
    wearInput.addEventListener("input", () => {
      const v = Number(wearInput.value);
      ld.wear = v;
      wearOut.textContent = v.toFixed(3);
    });
    $(".attr-seed", div).addEventListener("input", (e) => {
      ld.seed = Math.max(0, Math.min(1000, parseInt(e.target.value || "0", 10) || 0));
    });
    const stChk   = $(".attr-stattrak", div);
    const stCntEl = $(".attr-stcount", div);
    stChk.addEventListener("change", () => {
      if (stChk.checked) {
        ld.stattrak = parseInt(stCntEl.value || "0", 10) || 0;
        stCntEl.disabled = false;
      } else {
        ld.stattrak = -1;
        stCntEl.disabled = true;
      }
    });
    stCntEl.addEventListener("input", () => {
      if (stChk.checked) ld.stattrak = Math.max(0, parseInt(stCntEl.value || "0", 10) || 0);
    });
    $(".attr-nametag", div).addEventListener("input", (e) => {
      ld.nametag = e.target.value;
    });
    $(".row-change",   div).addEventListener("click", () => onWeaponPaintPick(def));
    $(".row-stickers", div).addEventListener("click", () => openStickerKeychainModal(def));
    $(".row-del",      div).addEventListener("click", () => {
      delete state.currentLoadout.weapons[def];
      renderEditorState(state.currentLoadout, false);
    });
  }
  return div;
}

function setSlotLabel(id, value, fmt) {
  const btn = $("#" + id);
  btn.textContent = value ? fmt(value) : "—";
}
function setPaintLabel(id, knifeDef, loadout) {
  const btn = $("#" + id);
  if (!knifeDef) { btn.textContent = "paint —"; return; }
  const paint = (loadout.weapons || {})[knifeDef];
  btn.textContent = paint ? `paint: ${skinNameFor(knifeDef, paint.paint) || ("#" + paint.paint)}` : "paint —";
}
function setGloveLabel(id, g) {
  const btn = $("#" + id);
  if (!g || !g.defindex) { btn.textContent = "—"; return; }
  btn.textContent = gloveLabel(g.defindex, g.paint);
}
function setAgentLabel(id, defindex) {
  const btn = $("#" + id);
  btn.textContent = defindex ? agentLabel(defindex) : "—";
}
function agentLabel(defindex) {
  const list = state.catalogs.agents || [];
  const e = list.find((a) => a.defindex === defindex);
  return e ? e.name : `agent #${defindex}`;
}

// Render wear + seed controls under a glove slot. Called from
// renderEditorState whenever a glove is set on the loadout.
function renderGloveAttrs(containerSlotId, glove, slotName) {
  const btn = $("#" + containerSlotId);
  let attrs = btn.nextElementSibling;
  // Reset previous attrs panel if any.
  if (attrs && attrs.classList.contains("glove-attrs")) attrs.remove();
  if (!glove || !glove.defindex) return;
  attrs = document.createElement("div");
  attrs.className = "glove-attrs";
  attrs.innerHTML = `
    <img class="skin-preview" src="${escapeAttr(imageUrl(glove.defindex, glove.paint))}" onerror="this.style.visibility='hidden'" />
    <div class="row-attrs">
      <label class="attr">
        <span>Wear</span>
        <input class="attr-wear" type="range" min="0" max="1" step="0.001" value="${glove.wear ?? 0.05}" />
        <output class="attr-wear-out">${(glove.wear ?? 0.05).toFixed(3)}</output>
      </label>
      <label class="attr">
        <span>Seed</span>
        <input class="attr-seed" type="number" min="0" max="1000" step="1" value="${glove.seed ?? 0}" />
      </label>
    </div>
  `;
  btn.insertAdjacentElement("afterend", attrs);
  const wear = $(".attr-wear", attrs);
  const out  = $(".attr-wear-out", attrs);
  wear.addEventListener("input", () => {
    const v = Number(wear.value);
    state.currentLoadout[slotName].wear = v;
    out.textContent = v.toFixed(3);
  });
  $(".attr-seed", attrs).addEventListener("input", (e) => {
    state.currentLoadout[slotName].seed = Math.max(0, Math.min(1000, parseInt(e.target.value || "0", 10) || 0));
  });
}

function knifeLabel(def) {
  const e = state.catalogs.knives.find((k) => k.defindex === def);
  return e ? e.name : `def #${def}`;
}
function gloveLabel(def, paint) {
  const e = state.catalogs.gloves.find((g) => g.defindex === def && g.paint === paint);
  return e ? e.name : `def #${def} / paint #${paint || "?"}`;
}
function skinNameFor(def, paint) {
  const list = state.weaponByDef.get(def);
  if (!list) return null;
  const e = list.find((x) => x.paint === paint);
  return e ? e.name : null;
}

// -- Slot click dispatch --------------------------------------------

function onSlotClick(slot) {
  if (slot === "knife_t" || slot === "knife_ct")
    return openKnifePicker(slot);
  if (slot === "knife_t_paint" || slot === "knife_ct_paint")
    return onKnifePaintPick(slot);
  if (slot === "gloves_t" || slot === "gloves_ct")
    return openGlovePicker(slot);
  if (slot === "agent_t" || slot === "agent_ct")
    return openAgentPicker(slot);
  if (slot === "music_kit")
    return openMusicKitPicker();
  if (slot === "pin_t" || slot === "pin_ct")
    return openPinPicker(slot);
}

function openMusicKitPicker() {
  const all = state.catalogs.music_kits || [];
  const choices = all.map((m) => ({
    key:   String(m.defindex),
    label: m.name,
    image: m.image,
  }));
  openPicker("Pick a music kit", [
    { key: "0", label: "— default (no music kit) —" },
    ...choices,
  ], (defStr) => {
    state.currentLoadout.music_kit = Number(defStr);
    renderEditorState(state.currentLoadout, false);
  });
}

function openPinPicker(slot) {
  const all = state.catalogs.pins || [];
  const choices = all.map((p) => ({
    key:   String(p.defindex),
    label: p.name,
    image: p.image,
  }));
  const sideLabel = slot === "pin_t" ? "T" : "CT";
  openPicker(`Pick ${sideLabel} pin`, [
    { key: "0", label: "— no pin —" },
    ...choices,
  ], (defStr) => {
    const v = Number(defStr);
    if (slot === "pin_t")  state.currentLoadout.pin_t  = v;
    else                    state.currentLoadout.pin_ct = v;
    renderEditorState(state.currentLoadout, false);
  });
}

// Sticker / keychain picker for a specific weapon — opens a dedicated
// modal with 4 sticker-slot rows + 1 keychain row. Each row pops the
// regular picker for selecting the sticker / charm catalog entry.
function openStickerKeychainModal(def) {
  const loadout = state.currentLoadout.weapons[def];
  if (!loadout) return;
  // Make sure stickers array exists at the right shape.
  if (!Array.isArray(loadout.stickers) || loadout.stickers.length !== 4) {
    loadout.stickers = [0, 0, 0, 0];
  }
  // Render a mini-DOM inline using the picker shell — but with 5 custom
  // rows instead of the grid. We piggyback off openPicker semantics by
  // hijacking the modal; simpler is to build an inline overlay.
  const modal = $("#sticker-modal");
  if (!modal) buildStickerModalShell();
  populateStickerModal(def);
  $("#sticker-modal").classList.remove("hidden");
}

function buildStickerModalShell() {
  const node = document.createElement("div");
  node.id = "sticker-modal";
  node.className = "modal hidden";
  node.innerHTML = `
    <div class="modal-card">
      <header>
        <h3 id="sticker-modal-title">Stickers + keychain</h3>
        <button id="sticker-modal-close">×</button>
      </header>
      <div class="sticker-list" id="sticker-modal-list"></div>
    </div>`;
  document.body.appendChild(node);
  $("#sticker-modal-close").addEventListener("click", () => $("#sticker-modal").classList.add("hidden"));
  node.addEventListener("click", (e) => { if (e.target === node) node.classList.add("hidden"); });
}

function populateStickerModal(def) {
  const loadout = state.currentLoadout.weapons[def];
  const wlabel = state.catalogs.weapon_labels[def] || `def #${def}`;
  $("#sticker-modal-title").textContent = `Stickers + keychain — ${wlabel}`;
  const list = $("#sticker-modal-list");
  list.innerHTML = "";
  // 4 sticker rows.
  for (let slot = 0; slot < 4; slot++) {
    const def_st = loadout.stickers[slot] || 0;
    const row = document.createElement("div");
    row.className = "sticker-row";
    row.innerHTML = `
      <img src="${escapeAttr(stickerImage(def_st))}" onerror="this.style.visibility='hidden'" />
      <div class="lbl">Slot ${slot}</div>
      <div class="name">${escapeHtml(stickerLabel(def_st))}</div>
      <button class="ghost pick">Pick</button>
      <button class="ghost del" ${def_st ? "" : "disabled"}>×</button>
    `;
    $(".pick", row).addEventListener("click", () => pickSticker(def, slot));
    $(".del",  row).addEventListener("click", () => {
      loadout.stickers[slot] = 0;
      populateStickerModal(def);
      renderEditorState(state.currentLoadout, false);
    });
    list.appendChild(row);
  }
  // Keychain row.
  const kc = loadout.keychain || 0;
  const kcRow = document.createElement("div");
  kcRow.className = "sticker-row keychain";
  kcRow.innerHTML = `
    <img src="${escapeAttr(keychainImage(kc))}" onerror="this.style.visibility='hidden'" />
    <div class="lbl">Charm</div>
    <div class="name">${escapeHtml(keychainLabel(kc))}</div>
    <button class="ghost pick">Pick</button>
    <button class="ghost del" ${kc ? "" : "disabled"}>×</button>
  `;
  $(".pick", kcRow).addEventListener("click", () => pickKeychain(def));
  $(".del",  kcRow).addEventListener("click", () => {
    loadout.keychain = 0;
    populateStickerModal(def);
    renderEditorState(state.currentLoadout, false);
  });
  list.appendChild(kcRow);
}

function pickSticker(def, slot) {
  const choices = (state.catalogs.stickers || []).map((s) => ({
    key:   String(s.defindex),
    label: s.name,
    image: s.image,
  }));
  $("#sticker-modal").classList.add("hidden");
  openPicker(`Sticker slot ${slot}`, choices, (defStr) => {
    const loadout = state.currentLoadout.weapons[def];
    if (!Array.isArray(loadout.stickers) || loadout.stickers.length !== 4) {
      loadout.stickers = [0, 0, 0, 0];
    }
    loadout.stickers[slot] = Number(defStr);
    renderEditorState(state.currentLoadout, false);
    openStickerKeychainModal(def);  // re-open the sticker modal
  });
}

function pickKeychain(def) {
  const choices = (state.catalogs.keychains || []).map((k) => ({
    key:   String(k.defindex),
    label: k.name,
    image: k.image,
  }));
  $("#sticker-modal").classList.add("hidden");
  openPicker("Pick a keychain", choices, (defStr) => {
    state.currentLoadout.weapons[def].keychain = Number(defStr);
    renderEditorState(state.currentLoadout, false);
    openStickerKeychainModal(def);
  });
}

$("#weapons-add").addEventListener("click", () => {
  // Pick a weapon first, then a paint.
  const choices = [];
  for (const [def, list] of state.weaponByDef) {
    if (def >= 500 && def < 600) continue;  // knives — handled separately
    choices.push({
      key: String(def),
      label: state.catalogs.weapon_labels[def] || `def #${def}`,
      sublabel: `${list.length} paints`,
    });
  }
  choices.sort((a, b) => a.label.localeCompare(b.label));
  openPicker("Pick a weapon", choices, (def) => onWeaponPaintPick(Number(def)));
});

function onWeaponPaintPick(def) {
  const list = state.weaponByDef.get(def) || [];
  const choices = list.map((e) => ({
    key: String(e.paint),
    label: e.name,
    image: imageUrl(def, e.paint),
  }));
  openPicker(
    `Pick paint for ${state.catalogs.weapon_labels[def] || ("def #" + def)}`,
    choices,
    (paintStr) => {
      const paint = Number(paintStr);
      // Preserve existing seed/wear/stattrak/nametag if the user is
      // re-choosing a paint on a weapon that already has them — losing
      // a 1337 StatTrak count to a paint swap would suck.
      const prev = state.currentLoadout.weapons[def] || {};
      state.currentLoadout.weapons[def] = {
        paint,
        seed: prev.seed ?? 0,
        wear: prev.wear ?? 0.01,
        stattrak: prev.stattrak ?? -1,
        nametag: prev.nametag ?? "",
      };
      renderEditorState(state.currentLoadout, false);
    },
    // Inspect callback: temporarily slap this paint onto the user's
    // live weapon and fire +lookatweapon. Inherits seed/wear/stattrak
    // from the current loadout entry so we preview the actual final
    // appearance (a high-StatTrak Vulcan looks different from a fresh
    // one because of the StatTrak overlay).
    async (paintStr) => {
      const paint = Number(paintStr);
      const prev = state.currentLoadout.weapons[def] || {};
      await api("/api/inspect", {
        method: "POST",
        body: JSON.stringify({
          steamid:    state.currentId,
          weapon_def: def,
          paint,
          seed:     prev.seed ?? 0,
          wear:     prev.wear ?? 0.01,
          stattrak: prev.stattrak ?? -1,
          nametag:  prev.nametag ?? "",
        }),
      });
    }
  );
}

function onKnifePaintPick(slot) {
  // Paint button is greyed out when no knife is picked; otherwise we
  // delegate to the weapon-paint picker for that knife's defindex.
  const ld = state.currentLoadout;
  const def = slot === "knife_t_paint" ? (ld.knives_t || 0) : (ld.knives_ct || 0);
  if (!def) { alert("Pick a knife first."); return; }
  onWeaponPaintPick(def);
}

function openKnifePicker(slot) {
  const choices = state.catalogs.knives.map((k) => ({
    key: String(k.defindex),
    label: k.name,
  }));
  openPicker(`Pick ${slot === "knife_t" ? "T" : "CT"} knife`, [
    { key: "0", label: "— default (no swap) —" },
    ...choices,
  ], (def) => {
    const v = Number(def);
    if (slot === "knife_t")  state.currentLoadout.knives_t  = v;
    else                     state.currentLoadout.knives_ct = v;
    renderEditorState(state.currentLoadout, false);
  });
}

function openGlovePicker(slot) {
  const choices = state.catalogs.gloves.map((g) => ({
    key: `${g.defindex}:${g.paint}`,
    label: g.name,
    image: imageUrl(g.defindex, g.paint),
  }));
  openPicker(`Pick ${slot === "gloves_t" ? "T" : "CT"} gloves`, [
    { key: "0:0", label: "— default —" },
    ...choices,
  ], (key) => {
    const [d, p] = key.split(":").map(Number);
    const value = d ? { defindex: d, paint: p, seed: 0, wear: 0.05 } : null;
    if (slot === "gloves_t")  state.currentLoadout.gloves_t  = value;
    else                       state.currentLoadout.gloves_ct = value;
    renderEditorState(state.currentLoadout, false);
  });
}

function openAgentPicker(slot) {
  // Agents are team-locked — filter the catalog to the matching side so
  // we don't offer a CT agent for the T slot (the backend would reject
  // the team-mismatch at apply time anyway, but the UI shouldn't show it).
  const wantTeam = slot === "agent_t" ? "T" : "CT";
  const all = state.catalogs.agents || [];
  const choices = all
    .filter((a) => a.team === wantTeam)
    .map((a) => ({
      key:   String(a.defindex),
      label: a.name,
      image: a.image,
    }));
  openPicker(`Pick ${wantTeam} agent`, [
    { key: "0", label: "— default model —" },
    ...choices,
  ], (defStr) => {
    const def = Number(defStr);
    if (slot === "agent_t")  state.currentLoadout.agent_t  = def;
    else                      state.currentLoadout.agent_ct = def;
    renderEditorState(state.currentLoadout, false);
  });
}

// -- Picker modal ----------------------------------------------------

let pickerOnPick = null;
let pickerChoices = [];

function openPicker(title, choices, onPick, inspect) {
  $("#picker-title").textContent = title;
  $("#picker-search").value = "";
  pickerOnPick = onPick;
  pickerChoices = choices;
  // Inspect is optional — only wired by callers that pick a (def, paint)
  // for a weapon or knife. Glove / agent / knife-defindex pickers don't
  // pass it because the in-game inspect anim only makes sense for the
  // weapon you're holding.
  pickerInspect = inspect || null;
  renderPicker("");
  $("#picker").classList.remove("hidden");
  $("#picker-search").focus();
}

$("#picker-close").addEventListener("click", () => $("#picker").classList.add("hidden"));
$("#picker").addEventListener("click", (e) => {
  if (e.target.id === "picker") $("#picker").classList.add("hidden");
});
$("#picker-search").addEventListener("input", (e) => renderPicker(e.target.value));

// Optional "Inspect in-game" callback set by the caller of openPicker.
// When non-null, each tile gets an Inspect button that triggers an
// in-game preview without committing the choice — letting the user
// flip on a paint to see how it looks before clicking Save.
let pickerInspect = null;

function renderPicker(filter) {
  const grid = $("#picker-grid");
  grid.innerHTML = "";
  const f = filter.toLowerCase();
  const filtered = pickerChoices.filter((c) =>
    !f || c.label.toLowerCase().includes(f) || (c.sublabel || "").toLowerCase().includes(f)
  );
  for (const c of filtered) {
    const tile = document.createElement("div");
    tile.className = "picker-tile";
    tile.innerHTML = `
      ${c.image ? `<img src="${escapeAttr(c.image)}" onerror="this.style.visibility='hidden'" />` : ""}
      <div class="name">${escapeHtml(c.label)}</div>
      ${c.sublabel ? `<div class="meta">${escapeHtml(c.sublabel)}</div>` : ""}
      <div class="actions">
        <button class="pick-btn">Pick</button>
        ${pickerInspect ? `<button class="inspect-btn" title="Try this paint on your live weapon">👁 Inspect</button>` : ""}
      </div>
    `;
    // Clicking the tile (outside the buttons) commits the pick — same as
    // the old behaviour. Buttons stop propagation so they don't fire two
    // actions at once.
    tile.addEventListener("click", () => {
      $("#picker").classList.add("hidden");
      pickerOnPick(c.key);
    });
    $(".pick-btn", tile).addEventListener("click", (e) => {
      e.stopPropagation();
      $("#picker").classList.add("hidden");
      pickerOnPick(c.key);
    });
    const inspBtn = $(".inspect-btn", tile);
    if (inspBtn) {
      inspBtn.addEventListener("click", async (e) => {
        e.stopPropagation();
        try {
          await pickerInspect(c.key);
          inspBtn.textContent = "✓ playing";
          setTimeout(() => { inspBtn.textContent = "👁 Inspect"; }, 3500);
        } catch (err) {
          inspBtn.textContent = "× " + (err.message || "fail");
          setTimeout(() => { inspBtn.textContent = "👁 Inspect"; }, 3500);
        }
      });
    }
    grid.appendChild(tile);
  }
  if (filtered.length === 0) {
    grid.innerHTML = `<div class="none-row" style="grid-column:1/-1">no matches</div>`;
  }
}

// -- Save / delete ---------------------------------------------------

$("#editor-save").addEventListener("click", async () => {
  if (!state.currentId || !state.currentLoadout) return;
  try {
    await api(`/api/players/${state.currentId}`, {
      method: "PUT",
      body: JSON.stringify(state.currentLoadout),
    });
    // Also trigger plugin reload so cached state in ApplyService picks
    // up the new players.json on the very next spawn / weapon entity.
    await api("/api/reload", { method: "POST" });
    await refreshStored();
    flash("saved & reloaded");
  } catch (e) {
    flash("save failed: " + e.message, true);
  }
});

// Randomize seeds — walk every weapon + the two glove slots and roll a
// fresh seed in [0, 999]. Seed 0 means "default paint phase", which is
// the same for every skin and looks boring; randomized seeds give every
// weapon a unique pattern shift. We don't auto-save — user can keep
// rolling until they're happy and then hit Save.
$("#randomize-seeds").addEventListener("click", () => {
  if (!state.currentLoadout) return;
  const roll = () => Math.floor(Math.random() * 1000);
  let touched = 0;
  for (const def of Object.keys(state.currentLoadout.weapons || {})) {
    state.currentLoadout.weapons[def].seed = roll();
    touched++;
  }
  if (state.currentLoadout.gloves_t)  { state.currentLoadout.gloves_t.seed  = roll(); touched++; }
  if (state.currentLoadout.gloves_ct) { state.currentLoadout.gloves_ct.seed = roll(); touched++; }
  renderEditorState(state.currentLoadout, false);
  flash(`rolled ${touched} fresh seed${touched === 1 ? "" : "s"} — Save to apply`);
});

$("#editor-delete").addEventListener("click", async () => {
  if (!state.currentId) return;
  if (!confirm(`Delete loadout for ${state.currentId}?`)) return;
  try {
    await api(`/api/players/${state.currentId}`, { method: "DELETE" });
    await api("/api/reload", { method: "POST" });
    await refreshStored();
    state.currentLoadout = emptyLoadout();
    state.currentLayouts = null;
    renderEditorState(state.currentLoadout, false);
    renderRoster();
    flash("deleted");
  } catch (e) {
    flash("delete failed: " + e.message, true);
  }
});

// -- Layouts: switch / new / rename / delete -------------------------

// Switching the dropdown: stash the in-progress edits to the previously
// active layout (so unsaved tweaks don't vanish during a switch), tell
// the backend to flip Active, then load the now-active layout.
$("#layout-select").addEventListener("change", async (e) => {
  if (!state.currentId || !state.currentLayouts) return;
  const newName = e.target.value;
  const oldName = state.currentLayouts.active;
  if (newName === oldName) return;
  try {
    // Stash unsaved edits to the old layout locally only — committing
    // them would require a separate save flow per layout. The user can
    // explicitly Save before switching if they want persistence.
    if (state.currentLoadout) {
      state.currentLayouts.layouts[oldName] = state.currentLoadout;
    }
    // Tell the backend to activate the new layout.
    state.currentLayouts = await api(
      `/api/players/${state.currentId}/layouts/${encodeURIComponent(newName)}/activate`,
      { method: "POST" }
    );
    await api("/api/reload", { method: "POST" });
    const loaded = state.currentLayouts.layouts?.[newName] || emptyLoadout();
    state.currentLoadout = loaded;
    renderLayoutBar();
    renderEditorState(loaded, false);
    flash(`activated '${newName}'`);
  } catch (err) {
    flash("switch failed: " + err.message, true);
    renderLayoutBar();  // revert dropdown to actual state
  }
});

$("#layout-new").addEventListener("click", async () => {
  if (!state.currentId || !state.currentLoadout) return;
  const name = prompt("Name the new layout (a–z, 0–9, '-', '_'):", "")?.trim();
  if (!name) return;
  if (!/^[A-Za-z0-9_\-]{1,32}$/.test(name)) {
    flash("invalid name — only letters, digits, '_', '-' (≤32 chars)", true);
    return;
  }
  if (state.currentLayouts?.layouts?.[name]) {
    if (!confirm(`'${name}' already exists. Overwrite with current state?`)) return;
  }
  try {
    state.currentLayouts = await api(`/api/players/${state.currentId}/layouts`, {
      method: "POST",
      body: JSON.stringify({
        name,
        loadout: state.currentLoadout,
        activate: true,
      }),
    });
    await api("/api/reload", { method: "POST" });
    state.currentLoadout = state.currentLayouts.layouts[name];
    renderLayoutBar();
    renderEditorState(state.currentLoadout, false);
    flash(`saved + activated '${name}'`);
  } catch (e) {
    flash("new failed: " + e.message, true);
  }
});

$("#layout-rename").addEventListener("click", async () => {
  if (!state.currentId || !state.currentLayouts) return;
  const oldName = state.currentLayouts.active;
  const newName = prompt(`Rename '${oldName}' to:`, oldName)?.trim();
  if (!newName || newName === oldName) return;
  if (!/^[A-Za-z0-9_\-]{1,32}$/.test(newName)) {
    flash("invalid name — only letters, digits, '_', '-' (≤32 chars)", true);
    return;
  }
  if (state.currentLayouts.layouts?.[newName]) {
    flash(`'${newName}' already exists`, true);
    return;
  }
  // Rename = create new + delete old. Order matters: create-and-activate
  // first so we don't end up with the old as active when delete fails.
  try {
    state.currentLayouts = await api(`/api/players/${state.currentId}/layouts`, {
      method: "POST",
      body: JSON.stringify({
        name: newName,
        loadout: state.currentLoadout,
        activate: true,
      }),
    });
    if (oldName !== "default") {
      state.currentLayouts = await api(
        `/api/players/${state.currentId}/layouts/${encodeURIComponent(oldName)}`,
        { method: "DELETE" }
      );
    }
    await api("/api/reload", { method: "POST" });
    state.currentLoadout = state.currentLayouts.layouts[newName];
    renderLayoutBar();
    renderEditorState(state.currentLoadout, false);
    flash(oldName === "default"
      ? `renamed (kept 'default' as well — it's the immortal slot)`
      : `renamed '${oldName}' -> '${newName}'`);
  } catch (e) {
    flash("rename failed: " + e.message, true);
  }
});

$("#layout-delete").addEventListener("click", async () => {
  if (!state.currentId || !state.currentLayouts) return;
  const name = state.currentLayouts.active;
  if (name === "default") return;  // belt — button is also disabled
  if (!confirm(`Delete layout '${name}'? You'll fall back to 'default'.`)) return;
  try {
    state.currentLayouts = await api(
      `/api/players/${state.currentId}/layouts/${encodeURIComponent(name)}`,
      { method: "DELETE" }
    );
    await api("/api/reload", { method: "POST" });
    state.currentLoadout = state.currentLayouts.layouts[state.currentLayouts.active] || emptyLoadout();
    renderLayoutBar();
    renderEditorState(state.currentLoadout, false);
    flash(`deleted '${name}'`);
  } catch (e) {
    flash("delete failed: " + e.message, true);
  }
});

function flash(msg, err) {
  const el = $("#save-flash");
  el.textContent = msg;
  el.className = "flash show" + (err ? " err" : "");
  setTimeout(() => el.classList.remove("show"), 2200);
}

// -- Catalog tab -----------------------------------------------------

function fillCatalogTab() {
  const sel = $("#catalog-weapon");
  sel.innerHTML = `<option value="">All weapons</option>`;
  for (const def of Array.from(state.weaponByDef.keys()).sort((a, b) => a - b)) {
    if (def >= 500 && def < 600) continue;  // knives shown in their own section below
    const opt = document.createElement("option");
    opt.value = def; opt.textContent = state.catalogs.weapon_labels[def] || `def #${def}`;
    sel.appendChild(opt);
  }
  $("#catalog-search").oninput = () => renderCatalog();
  sel.onchange = () => renderCatalog();
  renderCatalog();
}

function renderCatalog() {
  const grid = $("#catalog-grid");
  const f = $("#catalog-search").value.trim().toLowerCase();
  const w = $("#catalog-weapon").value;
  grid.innerHTML = "";
  const rows = state.catalogs.weapons.filter((e) => {
    if (w && String(e.weapon_defindex) !== w) return false;
    if (f && !e.name.toLowerCase().includes(f)) return false;
    return true;
  });
  for (const e of rows.slice(0, 600)) {
    const tile = document.createElement("div");
    tile.className = "catalog-tile";
    tile.innerHTML = `
      <img src="${escapeAttr(imageUrl(e.weapon_defindex, e.paint))}" onerror="this.style.visibility='hidden'" />
      <div class="name">${escapeHtml(e.name)}</div>
      <div class="meta">def ${e.weapon_defindex} · paint ${e.paint}</div>
    `;
    grid.appendChild(tile);
  }
  if (rows.length > 600) {
    const more = document.createElement("div");
    more.className = "muted";
    more.style.gridColumn = "1/-1";
    more.textContent = `…and ${rows.length - 600} more — narrow the filter`;
    grid.appendChild(more);
  }
}

// -- Bots tab --------------------------------------------------------

async function fillBotsTab() {
  const grid = $("#bots-grid");
  grid.innerHTML = `<div class="muted">loading…</div>`;
  try {
    const bots = await api("/api/bots");
    grid.innerHTML = "";
    if (bots.length === 0) {
      grid.innerHTML = `<div class="muted">No managed bots in the current fleet.</div>`;
      return;
    }
    for (const b of bots) {
      const card = document.createElement("div");
      card.className = "bot-card";
      const weaponRows = Object.keys(b.weapons || {})
        .sort((a, b) => Number(a) - Number(b))
        .slice(0, 6)
        .map((def) => {
          const w = b.weapons[def];
          const label = state.catalogs.weapon_labels[def] || `def #${def}`;
          return `<div class="bot-line">
            <img src="${escapeAttr(imageUrl(def, w.paint))}" onerror="this.style.visibility='hidden'" />
            <span class="b-w">${escapeHtml(label)}</span>
            <span class="b-n">${escapeHtml(skinNameFor(Number(def), w.paint) || ("paint #" + w.paint))}</span>
          </div>`;
        })
        .join("");
      const knifeBlock = `<div class="bot-line"><span class="b-w">Knife T</span><span class="b-n">${escapeHtml(knifeLabel(b.knife_t))}</span></div>
                          <div class="bot-line"><span class="b-w">Knife CT</span><span class="b-n">${escapeHtml(knifeLabel(b.knife_ct))}</span></div>`;
      const gloveBlock = `<div class="bot-line"><span class="b-w">Gloves T</span><span class="b-n">${escapeHtml(b.gloves_t ? gloveLabel(b.gloves_t.defindex, b.gloves_t.paint) : "—")}</span></div>
                          <div class="bot-line"><span class="b-w">Gloves CT</span><span class="b-n">${escapeHtml(b.gloves_ct ? gloveLabel(b.gloves_ct.defindex, b.gloves_ct.paint) : "—")}</span></div>`;
      const agentBlock = `<div class="bot-line"><span class="b-w">Agent T</span><span class="b-n">${escapeHtml(b.agent_t ? agentLabel(b.agent_t) : "—")}</span></div>
                          <div class="bot-line"><span class="b-w">Agent CT</span><span class="b-n">${escapeHtml(b.agent_ct ? agentLabel(b.agent_ct) : "—")}</span></div>`;
      card.innerHTML = `<h3>${escapeHtml(b.name)}</h3><div class="muted" style="font-size:11px;margin-bottom:6px">slot ${b.slot}</div>${weaponRows}${knifeBlock}${gloveBlock}${agentBlock}`;
      grid.appendChild(card);
    }
  } catch (e) {
    grid.innerHTML = `<div class="flash err show">${escapeHtml(e.message)}</div>`;
  }
}

// -- Skin images -----------------------------------------------------
//
// Image URLs come straight from the catalog payload — indexCatalogs()
// folds them into state.imageByKey at boot. We used to fetch them
// from bymykel.github.io directly, but that endpoint 301s to a
// CORS-restricted domain and browsers blocked the request.

function imageUrl(def, paint) {
  return state.imageByKey.get(`${def}:${paint}`) || "";
}

// -- Helpers ---------------------------------------------------------

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}
function escapeAttr(s) { return escapeHtml(s); }
