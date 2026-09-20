namespace MirBot
{
    /// <summary>
    /// The built-in status page, used when no status.html sits beside the executable.
    ///
    /// Kept as a string rather than an embedded resource so there is one less csproj concern; it is
    /// served verbatim. Edit status.html on disk to iterate without a rebuild.
    /// </summary>
    public static class StatusPage
    {
        public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>MirBot</title>
<style>
  :root {
    --bg:#14161a; --card:#1d2027; --line:#2b2f38; --text:#e6e8ec; --dim:#9aa1ad;
    --good:#4ec9a5; --warn:#e2b341; --bad:#e2685f; --accent:#6aa9ff;
  }
  * { box-sizing:border-box; }
  body { margin:0; padding:16px; background:var(--bg); color:var(--text);
         font:14px/1.45 ui-sans-serif,system-ui,"Segoe UI",sans-serif; }
  h1 { font-size:15px; font-weight:600; margin:0 0 14px; color:var(--dim); }
  h1 span { color:var(--text); }
  .bot { background:var(--card); border:1px solid var(--line); border-radius:10px;
         padding:14px; margin-bottom:14px; }
  .top { display:flex; align-items:baseline; gap:10px; flex-wrap:wrap; }
  .name { font-size:17px; font-weight:600; }
  .state { font-size:12px; padding:2px 8px; border-radius:99px; background:#262b34; color:var(--dim); }
  .state.Playing { background:rgba(78,201,165,.15); color:var(--good); }
  .state.Faulted, .state.Banned { background:rgba(226,104,95,.15); color:var(--bad); }
  .dead { background:var(--bad); color:#fff; padding:2px 8px; border-radius:99px; font-size:12px; }
  .action { margin:10px 0 12px; font-size:15px; }
  .action .detail { color:var(--dim); font-size:13px; }
  .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px 18px; }
  .k { color:var(--dim); font-size:11px; text-transform:uppercase; letter-spacing:.04em; }
  .v { font-variant-numeric:tabular-nums; }
  .bar { height:6px; background:#262b34; border-radius:99px; overflow:hidden; margin-top:4px; }
  .bar i { display:block; height:100%; }
  .hp i { background:var(--good); } .mp i { background:var(--accent); } .xp i { background:var(--warn); }
  table { width:100%; border-collapse:collapse; margin-top:12px; font-size:13px; }
  th { text-align:left; color:var(--dim); font-weight:500; font-size:11px;
       text-transform:uppercase; letter-spacing:.04em; padding:4px 8px 4px 0; }
  td { padding:3px 8px 3px 0; border-top:1px solid var(--line); font-variant-numeric:tabular-nums; }
  .broken { color:var(--bad); font-weight:600; } .worn { color:var(--warn); }
  .cols { display:grid; grid-template-columns:1fr 1fr; gap:18px; }
  @media (max-width:760px){ .cols{ grid-template-columns:1fr; } }
  .hist { max-height:260px; overflow:auto; }
  .count { color:var(--accent); }
  button { background:#262b34; color:var(--text); border:1px solid var(--line);
           border-radius:6px; padding:5px 12px; font-size:13px; cursor:pointer; margin-right:6px; }
  button:hover { border-color:var(--accent); }
  .err { color:var(--bad); font-size:13px; margin-top:8px; }
  footer { color:var(--dim); font-size:12px; margin-top:6px; }
</style>
</head>
<body>
<h1>MirBot — <span id="host"></span></h1>
<div id="bots"></div>
<footer id="foot"></footer>
<script>
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const pct = (a,b) => b > 0 ? Math.min(100, Math.round(a/b*100)) : 0;

async function send(id, action, query) {
  try {
    await fetch(`/api/bots/${encodeURIComponent(id)}/${action}` + (query ? "?" + query : ""),
                { method:"POST", headers:{ "X-MirBot":"1" } });
  } catch (e) { /* the poll will show the result */ }
  refresh();
}

// Destinations are fetched once: the map graph is built at startup and never changes.
let mapList = null;
const chosenMap = {};

async function loadMaps() {
  if (mapList) return mapList;
  try {
    const r = await fetch("/api/maps", { cache:"no-store" });
    mapList = await r.json();
  } catch (e) { mapList = []; }
  return mapList;
}

// Re-filling the <select> every second would fight the user's own selection, so each one is
// populated once and then left alone.
async function fillMaps() {
  const maps = await loadMaps();

  for (const sel of document.querySelectorAll("select[id^='map-']")) {
    if (sel.dataset.filled) continue;
    sel.dataset.filled = "1";
    sel.innerHTML = maps.map(m => `<option value="${esc(m.name)}">${esc(m.name)}</option>`).join("");

    // Restore what was chosen before the card was rebuilt.
    const id = sel.id.substring(4);
    if (chosenMap[id]) sel.value = chosenMap[id];

    sel.addEventListener("change", () => { chosenMap[id] = sel.value; });
  }
}

function goMap(id) {
  const sel = document.getElementById("map-" + id);
  if (!sel || !sel.value) return;
  send(id, "travel", "map=" + encodeURIComponent(sel.value));
}

function bar(cls, value, max) {
  return `<div class="bar ${cls}"><i style="width:${pct(value,max)}%"></i></div>`;
}

function render(h) {
  document.getElementById("host").textContent =
    `${h.server} · db ${h.databaseVersion} · ${h.bots.length} bot(s)`;
  document.getElementById("foot").textContent =
    `updated ${new Date(h.generatedAt).toLocaleTimeString()} · host up ${h.uptimeSeconds}s ` +
    `· ${h.magicCount} skills, ${h.monsterCount} monsters, ${h.vendorPages} trading pages`;

  document.getElementById("bots").innerHTML = h.bots.map(b => {
    const xp = b.experiencePercent === null
      ? (b.atMaxLevel ? "max level" : "—")
      : `${b.experiencePercent}%`;

    const equip = b.equipment.map(e => `<tr>
        <td>${esc(e.slot)}</td><td>${esc(e.name)}</td>
        <td class="${e.broken ? "broken" : e.worn ? "worn" : ""}">
          ${e.durability}/${e.maxDurability}${e.broken ? " BROKEN" : e.worn ? " worn" : ""}</td>
      </tr>`).join("");

    const bag = b.inventory.map(i => `<tr>
        <td>${i.slot}</td><td>${esc(i.name)}</td><td>${i.count > 1 ? "x"+i.count : ""}</td>
      </tr>`).join("");

    const hist = b.history.slice().reverse().map(e => `<tr>
        <td>${esc(e.action)} ${e.count > 1 ? `<span class="count">x${e.count}</span>` : ""}</td>
        <td>${esc(e.subject)}</td>
        <td>${new Date(e.lastAt).toLocaleTimeString()}</td>
      </tr>`).join("");

    return `<div class="bot">
      <div class="top">
        <span class="name">${esc(b.characterName || b.id)}</span>
        <span class="state ${esc(b.state)}">${esc(b.state)}</span>
        ${b.dead ? '<span class="dead">DEAD</span>' : ""}
        <span style="margin-left:auto">
          <button onclick="send('${esc(b.id)}','start')">Start</button>
          <button onclick="send('${esc(b.id)}','stop')">Stop</button>
          <button onclick="send('${esc(b.id)}','towntrip')">Town trip</button>
          <button onclick="send('${esc(b.id)}','travel')">Travel</button>
          <select id="map-${esc(b.id)}"></select>
          <button onclick="goMap('${esc(b.id)}')">Go</button>
          <button onclick="send('${esc(b.id)}','revive')">Revive</button>
        </span>
      </div>

      <div class="action"><b>${esc(b.currentAction) || "—"}</b>
        ${b.currentSubject ? " · " + esc(b.currentSubject) : ""}
        <div class="detail">${esc(b.currentDetail)}</div></div>

      ${b.exitReason ? `<div class="err">${esc(b.exitReason)}</div>` : ""}
      ${b.lastError ? `<div class="err">${esc(b.lastError)}</div>` : ""}

      <div class="grid">
        <div><div class="k">Level</div><div class="v">${b.level} ${esc(b.class)}</div></div>
        <div><div class="k">HP</div><div class="v">${b.health}/${b.maxHealth}</div>
             ${bar("hp", b.health, b.maxHealth)}</div>
        <div><div class="k">MP</div><div class="v">${b.mana}/${b.maxMana}</div>
             ${bar("mp", b.mana, b.maxMana)}</div>
        <div><div class="k">Experience</div><div class="v">${xp}</div>
             ${bar("xp", b.experiencePercent ?? 0, 100)}</div>
        <div><div class="k">Gold</div><div class="v">${Number(b.gold).toLocaleString()}</div></div>
        <div><div class="k">Location</div>
             <div class="v">${esc(b.mapName) || ("map " + b.mapIndex)} · ${b.x},${b.y}${b.inSafeZone ? " · safe" : ""}</div>
             <div class="detail">map ${b.mapIndex}</div></div>
        <div><div class="k">Bag</div><div class="v">${b.bagWeight}/${b.maxBagWeight} (${b.bagPercent}%)</div></div>
        <div><div class="k">Town trip</div><div class="v">${esc(b.tripPhase) || "—"}</div>
             <div class="detail">${esc(b.tripStatus)}</div></div>
        <div><div class="k">Counters</div>
             <div class="v">${b.decisions} dec · ${b.resyncs} resync · ${b.detours} detour
             ${b.droppedPackets ? ` · <span class="broken">${b.droppedPackets} dropped</span>` : ""}</div>
             <div class="detail">${b.casts} cast · ${b.fightsAbandoned} given up ·
             ${b.dangerAvoided} avoided · ${b.learnedBlockedCells} cells learned</div></div>
        <div><div class="k">Bank</div><div class="v">${esc(b.bankStatus) || "—"}</div></div>
      </div>

      <div class="cols">
        <div><table><tr><th>Slot</th><th>Equipped</th><th>Durability</th></tr>${equip}</table></div>
        <div><table><tr><th>#</th><th>Bag</th><th></th></tr>${bag}</table></div>
      </div>

      <div class="hist"><table><tr><th>Action</th><th>Subject</th><th>When</th></tr>${hist}</table></div>
    </div>`;
  }).join("");
}

async function refresh() {
  // The whole card is rebuilt from innerHTML each poll, which destroys and recreates every
  // <select> - so an open dropdown closed itself about once a second and was unusable. While
  // one has focus the refresh is skipped entirely; the numbers can wait a moment.
  const a = document.activeElement;
  if (a && (a.tagName === "SELECT" || a.dataset?.holdRefresh)) return;

  try {
    const r = await fetch("/api/status", { cache:"no-store" });
    render(await r.json());
    fillMaps();
  } catch (e) {
    document.getElementById("foot").textContent = "host unreachable — " + e;
  }
}

refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
""";
    }
}
