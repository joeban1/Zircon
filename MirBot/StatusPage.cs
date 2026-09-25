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
<meta name="viewport" content="width=device-width,initial-scale=1">
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
         padding:12px 14px; margin-bottom:10px; }
  .top { display:flex; align-items:baseline; gap:10px; flex-wrap:wrap; }
  .name { font-size:17px; font-weight:600; }
  .state { font-size:12px; padding:2px 8px; border-radius:99px; background:#262b34; color:var(--dim); }
  .state.Playing { background:rgba(78,201,165,.15); color:var(--good); }
  .state.Faulted, .state.Banned { background:rgba(226,104,95,.15); color:var(--bad); }
  .dead { background:var(--bad); color:#fff; padding:2px 8px; border-radius:99px; font-size:12px; }
  .action { margin:8px 0 10px; font-size:14px; }
  .action .detail { color:var(--dim); font-size:13px; }
  .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(112px,180px));
          gap:7px 14px; align-content:start; }
  .grid .wide { grid-column:span 3; }
  @media (max-width:620px){ .grid .wide { grid-column:span 2; } }
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
  select { background:#262b34; color:var(--text); border:1px solid var(--line);
           border-radius:6px; padding:5px 8px; font-size:13px; margin-right:6px; }
  .err { color:var(--bad); font-size:13px; margin-top:8px; }
  footer { color:var(--dim); font-size:12px; margin-top:6px; }
  .hide { display:none; }

  /* --- page tabs --- */
  .tabs { display:flex; gap:4px; margin:0 0 14px; border-bottom:1px solid var(--line); }
  .tabs button { background:none; border:none; border-bottom:2px solid transparent;
                 border-radius:0; color:var(--dim); padding:7px 14px; margin:0; font-size:13px; }
  .tabs button.on { color:var(--text); border-bottom-color:var(--accent); }
  .tabs button:hover { border-color:transparent; border-bottom-color:var(--line); color:var(--text); }
  .tabs button.on:hover { border-bottom-color:var(--accent); }

  /* --- compact card --- */
  .body { display:grid; grid-template-columns:1fr 320px; gap:14px; align-items:start; }
  @media (max-width:620px){ .body { grid-template-columns:1fr; } }

  /* Everything on show at once, side by side.
     Only one bot is expanded now, so there is room to stop hiding things behind buttons - and a
     pane you have to click for is a pane you forget to look at. Each one scrolls on its own so a
     forty-item bag cannot stretch the card past the others. */
  /* An explicit column count, NOT auto-fit.
     auto-fit only collapses tracks that nothing occupies, and the full-width "Why not?" row
     spans every track by definition - so on a wide screen it created nine columns, put the four
     tables in the first four at their 215px minimum, and left the right half of the card empty. */
  .panes { display:grid; grid-template-columns:repeat(2,minmax(0,1fr));
           gap:8px 16px; margin-top:10px; border-top:1px solid var(--line); padding-top:8px; }
  @media (min-width:1000px){ .panes { grid-template-columns:repeat(4,minmax(0,1fr)); } }
  @media (max-width:620px) { .panes { grid-template-columns:minmax(0,1fr); } }
  .pane { min-width:0; }
  .pane > h4 { margin:0; color:var(--dim); font-size:11px; font-weight:500;
               text-transform:uppercase; letter-spacing:.04em; }
  .pane .scroll { max-height:380px; overflow:auto; }
  .pane table { margin-top:4px; }
  .pane td, .pane th { font-size:12px; }
  .whypane { grid-column:1/-1; }
  .whypane .row { margin-top:4px; font-size:13px; }
  .whypane .row b { color:var(--dim); font-weight:500; }

  /* --- item cells ---
     Coloured tiles keyed on item type, not artwork. The client's sprites live in a container
     format nothing here can decode yet, so the grid is built to work without them: every cell
     already carries its image index, and a tile becomes an icon the day the icons exist. */
  .cells { display:grid; grid-template-columns:repeat(auto-fill,minmax(38px,1fr)); gap:3px;
           margin-top:4px; }
  .cell { position:relative; aspect-ratio:1; border:1px solid var(--line); border-radius:5px;
          background:#171a20; display:flex; align-items:center; justify-content:center;
          font-size:9px; line-height:1.05; text-align:center; overflow:hidden; padding:2px;
          color:var(--dim); cursor:default; }
  .cell.full { color:var(--text); }
  .cell.broken { border-color:var(--bad); }
  .cell.worn { border-color:var(--warn); }
  .cell .n { position:absolute; right:2px; bottom:1px; font-size:9px; color:var(--accent);
             background:rgba(20,22,26,.85); border-radius:3px; padding:0 2px; }
  .cell .lbl { position:absolute; inset:0; display:flex; align-items:center;
               justify-content:center; padding:2px; }
  /* The initials are the FALLBACK, not a backdrop. Item art is mostly transparent, so leaving the
     text underneath showed it through the gaps. The cell only stops drawing it once the image has
     actually loaded - so a missing icon still reads, and a present one is clean. */
  .cell.art .lbl { display:none; }
  .cell img { position:absolute; inset:2px; width:calc(100% - 4px); height:calc(100% - 4px);
              object-fit:contain; image-rendering:pixelated; }

  /* item type families, so a bag is readable at a glance */
  .t-weapon   { background:#2a2130; } .t-armour  { background:#1d2733; }
  .t-jewel    { background:#2b2733; } .t-potion  { background:#1f2b26; }
  .t-scroll   { background:#2c2a20; } .t-book    { background:#232a33; }
  .t-ore      { background:#26231e; } .t-part    { background:#2a2024; }
  .t-reagent  { background:#1e2a2a; }

  /* Two columns, filled ROW BY ROW - EQUIP_ORDER is written as left/right pairs, so ordinary row
     flow puts body-and-hands down the left and jewellery down the right. Column flow would take
     the first six entries as one column and scramble the pairing. */
  .slots { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:4px 10px;
           margin-top:4px; }
  @media (max-width:620px){ .slots { grid-template-columns:minmax(0,1fr); } }
  .slot.empty { opacity:.45; }
  .slot.empty .cell { border-style:dashed; }
  .slot { display:flex; align-items:center; gap:5px; min-width:0; }
  .slot .cell { width:32px; flex:0 0 32px; }
  .slot .who { min-width:0; }
  .slot .who .s { color:var(--dim); font-size:10px; text-transform:uppercase;
                  letter-spacing:.03em; }
  .slot .who .i { font-size:11px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }

  /* --- tooltip --- */
  #tip { position:fixed; z-index:50; pointer-events:none; max-width:280px;
         background:#11131a; border:1px solid var(--line); border-radius:8px; padding:9px 11px;
         font-size:12px; box-shadow:0 8px 24px rgba(0,0,0,.55); display:none; }
  #tip .tn { font-weight:600; font-size:13px; }
  #tip .tt { color:var(--dim); font-size:11px; margin-bottom:5px; }
  #tip .st { display:flex; justify-content:space-between; gap:12px; }
  #tip .st b { font-weight:500; color:var(--dim); }
  #tip .add { color:var(--good); }
  #tip .req { color:var(--warn); margin-top:4px; }
  #tip .desc { color:var(--dim); margin-top:5px; font-style:italic; }
  #tip hr { border:none; border-top:1px solid var(--line); margin:5px 0; }

  /* --- the summary strip --- */
  /* FOUR PER ROW, wrapping to as many rows as there are bots.
     auto-fit with a 215px minimum fitted as many cards per row as the window allowed, which
     was fine at four bots and squeezed eight onto one line on a wide monitor - every card
     narrower than its own text. Capping the track count at four keeps each card readable and
     puts bots 5-8 on a second row. */
  .strip { display:grid; grid-template-columns:repeat(4,minmax(0,1fr));
           gap:8px; margin-bottom:12px; }
  @media (max-width:1100px){ .strip { grid-template-columns:repeat(2,minmax(0,1fr)); } }
  @media (max-width:560px) { .strip { grid-template-columns:minmax(0,1fr); } }
  .mini { background:var(--card); border:1px solid var(--line); border-radius:10px;
          padding:9px 11px; cursor:pointer; }
  .mini:hover { border-color:#3a4150; }
  .mini.on { border-color:var(--accent); background:#20242c; cursor:default; }
  .mini .row1 { display:flex; align-items:center; gap:7px; }
  .mini .nm { font-weight:600; font-size:14px; }
  .mini .lv { color:var(--dim); font-size:12px; margin-left:auto;
              font-variant-numeric:tabular-nums; }
  .mini .act { font-size:12px; margin-top:5px; white-space:nowrap; overflow:hidden;
               text-overflow:ellipsis; }
  .mini .act b { font-weight:600; }
  .mini .sub { color:var(--dim); font-size:11px; margin-top:2px; display:flex; gap:6px;
               align-items:baseline; }
  /* The map/kill text is the part allowed to be cut short; gold never is. */
  .mini .sub .where { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; min-width:0; }
  .mini .sub .gp { margin-left:auto; flex:none; color:var(--text);
                   font-variant-numeric:tabular-nums; }
  .mini .bars { display:grid; grid-template-columns:1fr 1fr 1fr; gap:5px; margin-top:6px; }
  .mini .bars span { color:var(--dim); font-size:10px; font-variant-numeric:tabular-nums; }
  .mini .bar { margin-top:2px; }

  .dot { width:10px; height:10px; border-radius:50%; display:inline-block; flex:0 0 auto; }
  .dot.green { background:var(--good); }
  .dot.yellow { background:var(--warn); }
  .dot.red { background:var(--bad); box-shadow:0 0 0 3px rgba(226,104,95,.2); }
  .dot.grey { background:#4a515e; }
  .since { color:var(--dim); font-size:12px; font-variant-numeric:tabular-nums; }


  #alerts { margin-bottom:14px; }
  .alert { background:rgba(226,104,95,.12); border:1px solid rgba(226,104,95,.35);
           color:var(--bad); border-radius:8px; padding:8px 12px; margin-bottom:6px;
           font-size:13px; }
  .alert.warn { background:rgba(226,179,65,.12); border-color:rgba(226,179,65,.35);
                color:var(--warn); }

  .spark { display:block; width:100%; height:20px; margin-top:3px; }
  .spark path { fill:none; stroke:var(--warn); stroke-width:1.5; }
  .spark .fill { fill:rgba(226,179,65,.12); stroke:none; }

  details.mem { background:var(--card); border:1px solid var(--line); border-radius:10px;
                padding:12px 14px; margin-bottom:14px; }
  details.mem summary { cursor:pointer; font-weight:600; }
  details.mem table { margin-top:10px; }
  details.mem td.num, details.mem th.num { text-align:right; font-variant-numeric:tabular-nums; }
  tr.lethal td { color:var(--bad); }
  tr.here td { background:rgba(106,169,255,.08); }
  .tag { font-size:10px; padding:1px 6px; border-radius:99px; background:#262b34;
         color:var(--dim); margin-left:6px; }
  .tag.bad { background:rgba(226,104,95,.18); color:var(--bad); }

  .controls { display:flex; gap:8px; flex-wrap:wrap; align-items:center; margin-top:10px; }
  .controls input[type=search] { background:#262b34; color:var(--text);
        border:1px solid var(--line); border-radius:6px; padding:5px 10px; font-size:13px;
        min-width:190px; }
  .controls label { color:var(--dim); font-size:12px; display:flex; align-items:center; gap:5px; }
  .controls .n { color:var(--dim); font-size:12px; margin-left:auto; }
  th.sortable { cursor:pointer; user-select:none; white-space:nowrap; }
  th.sortable:hover { color:var(--text); }
  th.sortable .arrow { color:var(--accent); }

  .map { margin-top:0; }
  /* Capped, not just full-width. Stacked on a narrow screen a square map takes the whole column
     and becomes the tallest thing on the card - which is the opposite of the point. */
  /* Big enough to actually read the cave. Still capped, because these maps are square and an
     uncapped one stacks to the full column width on a phone and becomes the tallest thing here. */
  .map canvas { width:100%; max-width:320px; display:block; border:1px solid var(--line);
                border-radius:6px; background:#0f1115; image-rendering:pixelated; }
  .map .legend { color:var(--dim); font-size:11px; margin-top:4px; }
  .map .legend b { color:var(--accent); font-weight:600; }
  .map .legend i { color:var(--warn); font-style:normal; font-weight:600; }

  details.cfg { margin-top:12px; border-top:1px solid var(--line); padding-top:8px; }
  details.cfg summary { cursor:pointer; color:var(--dim); font-size:11px;
                        text-transform:uppercase; letter-spacing:.04em; }
  .cfggroup { margin-top:10px; }
  .cfggroup > b { color:var(--dim); font-size:11px; text-transform:uppercase;
                  letter-spacing:.04em; font-weight:500; }
  .cfgrow { display:flex; align-items:center; gap:8px; margin-top:5px; font-size:13px; }
  .cfgrow .name { min-width:200px; }
  .cfgrow .note { color:var(--dim); font-size:12px; flex:1; }
  .cfgrow input[type=number] { width:110px; background:#262b34; color:var(--text);
        border:1px solid var(--line); border-radius:6px; padding:4px 8px; font-size:13px;
        font-variant-numeric:tabular-nums; }
  /* --- notifications tab --- */
  .ncard { background:var(--card); border:1px solid var(--line); border-radius:10px;
           padding:12px 14px; margin-bottom:12px; }
  .nrow { display:flex; align-items:center; gap:14px; padding:10px 0; border-top:1px solid var(--line); }
  .nrow:first-child { border-top:none; }
  .nrow .nl { flex:1; min-width:0; }
  .nrow .nl b { display:block; font-weight:600; }
  .nrow .nl span { color:var(--dim); font-size:12px; }
  .nrow .mixed { color:var(--warn); font-size:11px; margin-left:6px; }
  .nrow input[type=number] { width:70px; background:#262b34; color:var(--text);
           border:1px solid var(--line); border-radius:6px; padding:4px 6px; }
  .switch { position:relative; width:44px; height:24px; flex:none; display:inline-block; }
  .switch input { opacity:0; width:0; height:0; position:absolute; }
  .switch .sl { position:absolute; inset:0; background:#3a404c; border-radius:12px;
           transition:background .15s; cursor:pointer; }
  .switch .sl:before { content:""; position:absolute; width:18px; height:18px; left:3px; top:3px;
           background:#fff; border-radius:50%; transition:transform .15s; }
  .switch input:checked + .sl { background:var(--good); }
  .switch input:checked + .sl:before { transform:translateX(20px); }
  .switch input:focus-visible + .sl { outline:2px solid var(--accent); outline-offset:2px; }
  .switch input:disabled + .sl { opacity:.5; cursor:wait; }
  .out-sent { color:var(--good); } .out-failed { color:var(--bad); } .out-held { color:var(--dim); }
  .cfgrow input.dirty { border-color:var(--warn); }
  .cfgrow .reach { font-size:10px; color:var(--dim); }
  .cfgresult { margin-top:8px; font-size:12px; color:var(--good); }
</style>
</head>
<body>
<h1>MirBot — <span id="host"></span></h1>
<div class="tabs">
  <button id="tab-bots" class="on">Bots</button>
  <button id="tab-info">Info</button>
  <button id="tab-settings">Settings</button>
  <button id="tab-notify">Notifications</button>
</div>
<div id="page-bots">
<div id="alerts"></div>
<div id="strip" class="strip"></div>
<div id="botdetail"></div>
</div>
<div id="page-info" class="hide">
<details class="mem" id="maptripmem"><summary>Map selections and trips</summary>
  <div class="controls">
    <input type="search" id="maptripq" placeholder="search map, reason, character" autocomplete="off">
    <label>Character <select id="maptripchar"><option value="">all characters</option></select></label>
    <label>Destination <select id="maptripmap"><option value="">all maps</option></select></label>
    <label>Status <select id="maptripstatus">
      <option value="">any</option><option value="active">active</option>
      <option value="left">left</option><option value="never">never arrived</option></select></label>
    <span class="n" id="maptripcount"></span></div>
  <div class="detail">Kills are positive XP awards on the selected map (a proxy; item or quest XP can also count).</div>
  <div id="maptriptable"></div></details>
<details class="mem" id="huntmem"><summary>Hunting memory</summary>
  <div class="detail">Scores use the baseline 25% death penalty; an individual bot's loss-watch travel choice may use a higher penalty.</div>
  <div class="controls">
    <input type="search" id="huntq" placeholder="search map or class" autocomplete="off">
    <select id="huntclass"><option value="">all classes</option></select>
    <label><input type="checkbox" id="huntmine"> only where bots are</label>
    <label><input type="checkbox" id="huntlethal"> hide lethal</label>
    <label><input type="checkbox" id="huntmeasured"> measured only</label>
    <span class="n" id="huntcount"></span>
  </div>
  <div id="hunttable"></div></details>
<details class="mem" id="questmem"><summary>Quest log</summary>
  <div class="controls">
    <input type="search" id="questq" placeholder="search quest, rewards, map, character" autocomplete="off">
    <label>Bot <select id="questchar"><option value="">all bots</option></select></label>
    <label>Quest <select id="questname"><option value="">all quests</option></select></label>
    <label>Event <select id="questevent"><option value="">any</option>
      <option value="accepted">accepted</option><option value="completed">completed</option>
      <option value="journey">journey</option></select></label>
    <span class="n" id="questcount"></span></div>
  <div class="detail">From the server's own quest updates: accepted when a quest appears in the log, completed when it is handed in. A journey row is a bot setting off to kill a quest boss.</div>
  <div id="questtable"></div></details>
<details class="mem" id="bossmem"><summary>Boss kills</summary>
  <div class="controls">
    <input type="search" id="bossq" placeholder="search boss, map, loot, character" autocomplete="off">
    <label>Character <select id="bosschar"><option value="">all characters</option></select></label>
    <label>Boss <select id="bossname"><option value="">all bosses</option></select></label>
    <label>Map <select id="bossmap"><option value="">all maps</option></select></label>
    <label>Kind <select id="bosskind"><option value="">any</option>
      <option value="mini-boss">mini-boss</option><option value="boss">boss</option></select></label>
    <span class="n" id="bosscount"></span></div>
  <div class="detail">A boss counts when it dies within 30s of this bot hitting it. Dropped is everything new on the ground within 8 tiles two seconds after it died (other kills nearby in the same moment can add to it): taken, left behind, or gone (someone else, or expired). Picked up is what reached the bag in the two minutes after.</div>
  <div id="bosstable"></div></details>
<details class="mem" id="deathmem"><summary>Recent deaths</summary>
  <div class="controls">
    <input type="search" id="deathq" placeholder="search map, killer, character" autocomplete="off">
    <label>Character <select id="deathchar"><option value="">all characters</option></select></label>
    <label>Map <select id="deathmap"><option value="">all maps</option></select></label>
    <label>Killer <select id="deathkiller"><option value="">all killers</option></select></label>
    <span class="n" id="deathcount"></span></div>
  <div id="deathtable"></div></details>
<details class="mem" id="levelmem"><summary>Level ups</summary>
  <div class="controls"><label>Character <select id="levelchar"><option value="">all characters</option></select></label>
    <span class="detail">Times shown in your local timezone; ≈ means a historical estimate.</span></div>
  <div id="leveltable"></div></details>
<details class="mem" id="upgrademem"><summary>Upgrades</summary>
  <div class="controls"><label>Character <select id="upgradechar"><option value="">all characters</option></select></label>
    <span class="detail">Confirmed equipment replacements only; older equip requests have no server verdict in the retained log.</span></div>
  <div id="upgradetable"></div></details>
<details class="mem" id="skillmem"><summary>Skills</summary>
  <div class="controls"><label>Character <select id="skillchar"><option value="">all characters</option></select></label>
    <span class="detail">Book-learning attempts and outcomes. Older dated outcomes are backfilled; undated logs are excluded.</span></div>
  <div id="skilltable"></div></details>
<details class="mem" id="lootmem"><summary>Drop lookup</summary>
  <div class="controls">
    <input type="search" id="lootq" placeholder="item name, e.g. Fire Wall" autocomplete="off">
    <button id="lootgo">Search</button>
    <span class="detail" id="lootnote">Searches the bot log and its rotations.</span>
  </div>
  <div id="loottable"></div></details>
</div>

<div id="page-settings" class="hide">
  <div class="mem">
    <b>Settings apply to every bot.</b>
    <div class="detail" style="margin-top:4px">Each bot applies the change on its own thread and
      writes its own ini, so a value survives a restart. A setting marked <i>mixed</i> means the
      bots do not currently agree - setting it again brings them back into line.</div>
    <div class="cfgresult" id="cfgresult"></div>
    <div id="cfgform"></div>
  </div>
</div>

<div id="tip"></div>
<footer id="foot"></footer>
<div id="page-notify" class="hide">
  <div class="ncard">
    <b>Phone notifications</b> <span class="detail" id="notifystate"></span>
    <div class="detail" style="margin-top:4px">Sent to your phone through Home Assistant. Each
      switch applies to every bot and is saved to each bot's ini, so it survives a restart.</div>
    <div id="notifyswitches" style="margin-top:6px"></div>
    <div class="controls"><button id="notifytest">Send test notification</button>
      <span class="n" id="notifyresult"></span></div>
  </div>
  <div class="ncard">
    <b>Recent notifications</b> <span class="detail">newest first, last 60 since the host started</span>
    <div id="notifyrecent" style="margin-top:6px"></div>
  </div>
</div>
<script>
"use strict";

// ---------------------------------------------------------------------------------------------
// Cards are BUILT ONCE and PATCHED thereafter.
//
// The previous version reassigned the whole #bots innerHTML on every poll, once a second. That
// destroys and recreates every element underneath it, which was already costing real usability -
// an open <select> closed itself about once a second, and the workaround was to skip the refresh
// entirely while one had focus, so the numbers stopped updating whenever you tried to use a
// control. It also rules out anything that owns state of its own: an input mid-edit, an expanded
// <details>, a hovered tooltip, a <canvas> with a drawing on it.
//
// So each bot gets a DOM subtree once, a bag of references into it, and per-field updates. The
// only wholesale rewrites left are the four tables, and those are gated on a content signature so
// an unchanged table is not touched either.
// ---------------------------------------------------------------------------------------------

const esc = s => String(s ?? "").replace(/[&<>"]/g, c =>
  ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;" }[c]));

const pct = (v, max) => max > 0 ? Math.max(0, Math.min(100, Math.round(v * 100 / max))) : 0;

const stripEl = document.getElementById("strip");
const cards = new Map();          // bot id -> full card, built on first selection
const minis = new Map();          // bot id -> summary card, always present

// --- controls ---------------------------------------------------------------------------------

async function send(id, action, query) {
  try {
    await fetch(`/api/bots/${encodeURIComponent(id)}/${action}` + (query ? "?" + query : ""),
                { method:"POST", headers:{ "X-MirBot":"1" } });
  } catch (e) { /* the poll will show the result */ }
  refresh();
}

function goMap(id) {
  const sel = cards.get(id)?.el.map;
  if (sel && sel.value) send(id, "travel", "map=" + encodeURIComponent(sel.value));
}

// The map list never changes, so it is fetched once and every new card is filled from it. A card
// is only built once now, so there is no need to re-fill or to remember the chosen value.
let mapList = null;

async function loadMaps() {
  if (mapList) return mapList;
  try {
    const r = await fetch("/api/maps", { cache:"no-store" });
    mapList = await r.json();
  } catch (e) { mapList = []; }
  return mapList;
}

async function fillMap(sel) {
  const maps = await loadMaps();
  if (!maps.length || sel.options.length) return;
  sel.appendChild(new Option("auto", ""));
  for (const m of maps) sel.appendChild(new Option(m.name, m.name));
}

// --- card construction ------------------------------------------------------------------------

const CARD_HTML = `
  <div class="top">
    <span class="dot" data-r="dot"></span>
    <span class="name" data-r="name"></span>
    <span class="state" data-r="state"></span>
    <span class="since" data-r="since"></span>
    <span class="dead hide" data-r="dead">DEAD</span>
    <span style="margin-left:auto">
      <button data-a="start">Start</button>
      <button data-a="stop">Stop</button>
      <button data-a="towntrip">Town trip</button>
      <button data-a="forcerepair">Repair</button>
      <button data-a="nexttarget" title="Skip the current target and pick another nearby">Next target</button>
      <button data-a="quests" title="Go to the quest NPC's map now and do the quests there">Do quests</button>
      <button data-a="travel">Travel</button>
      <select data-r="map"></select>
      <button data-a="go">Go</button>
      <button data-a="revive">Revive</button>
    </span>
  </div>

  <div class="action"><b data-r="action"></b><span data-r="subject"></span>
    <div class="detail" data-r="detail"></div></div>

  <div class="err hide" data-r="exit"></div>
  <div class="err hide" data-r="error"></div>

  <div class="body">
    <div class="grid">
      <div><div class="k">Level</div><div class="v" data-r="level"></div></div>
      <div><div class="k">HP</div><div class="v" data-r="hp"></div>
           <div class="bar hp"><i data-r="hpbar"></i></div></div>
      <div><div class="k">MP</div><div class="v" data-r="mp"></div>
           <div class="bar mp"><i data-r="mpbar"></i></div></div>
      <div><div class="k">Experience</div><div class="v" data-r="xp"></div>
           <div class="bar xp"><i data-r="xpbar"></i></div>
           <div class="detail" data-r="xppace"></div>
           <div class="detail" data-r="xpeta"></div></div>
      <div><div class="k">Gold</div><div class="v" data-r="gold"></div>
           <svg class="spark" data-r="spark" preserveAspectRatio="none" viewBox="0 0 100 26">
             <path class="fill" data-r="sparkfill"></path><path data-r="sparkline"></path></svg></div>
      <div><div class="k">Hunt Gold</div><div class="v" data-r="huntgold"></div>
           <div class="detail" data-r="storestatus"></div>
           <div class="detail" data-r="famestatus"></div></div>
      <div><div class="k">Bag</div><div class="v" data-r="bag"></div></div>
      <div><div class="k">Location</div><div class="v" data-r="loc"></div>
           <div class="detail" data-r="locdetail"></div></div>
      <div class="wide"><div class="k">Farming destination (last choice)</div>
           <div class="v" data-r="farmdest"></div>
           <div class="detail" data-r="farmreason"></div></div>
      <div><div class="k">Coverage</div><div class="v" data-r="coverage"></div>
           <div class="detail" data-r="coverdetail"></div></div>
      <div><div class="k">Town trip</div><div class="v" data-r="trip"></div>
           <div class="detail" data-r="tripdetail"></div></div>
      <div><div class="k">Bank</div><div class="v" data-r="bank"></div></div>
      <div class="wide"><div class="k">Counters</div><div class="v" data-r="counters"></div>
           <div class="detail" data-r="counters2"></div></div>
    </div>

    <div class="map"><canvas data-r="canvas" width="10" height="10"></canvas>
      <div class="legend" data-r="legend"></div></div>
  </div>

  <div class="panes">
    <div class="pane whypane"><h4>Why not?</h4><div data-r="why"></div></div>

    <div class="pane"><h4 data-r="equiphead">Equipped</h4>
      <div class="scroll slots" data-r="equip"></div></div>

    <div class="pane"><h4 data-r="baghead">Bag</h4>
      <div class="scroll cells" data-r="baglist"></div></div>

    <div class="pane"><h4 data-r="storehead">Storage</h4>
      <div class="scroll cells" data-r="storelist"></div></div>

    <div class="pane"><h4 data-r="skillshead">Skills</h4>
      <div class="scroll"><table><tr><th>Skill</th><th class="num">Lvl</th><th>Progress</th></tr>
        <tbody data-r="skills"></tbody></table></div></div>

    <div class="pane"><h4 data-r="petshead">Pets</h4>
      <div class="scroll"><table><tr><th>Pet</th><th>HP</th><th>Away</th></tr>
        <tbody data-r="pets"></tbody></table></div></div>

    <div class="pane"><h4 data-r="buffshead">Buffs</h4>
      <div class="scroll"><table><tr><th>Buff</th><th>Time left</th></tr>
        <tbody data-r="buffs"></tbody></table></div></div>

    <div class="pane"><h4 data-r="questshead">Quests</h4>
      <div class="scroll"><table><tr><th>Quest</th><th>Progress</th></tr>
        <tbody data-r="quests"></tbody></table></div></div>


    <div class="pane"><h4>History</h4>
      <div class="scroll"><table><tr><th>Action</th><th>Subject</th><th>When</th></tr>
        <tbody data-r="hist"></tbody></table></div></div>
  </div>`;

// --- the summary strip ------------------------------------------------------------------------
//
// One bot is shown in full and the rest are one-line summaries. Four full cards meant scrolling
// past three of them to read the fourth, and in practice you are looking at one bot at a time -
// the other three only need to answer "is anything wrong over there?", which a dot, a couple of
// bars and the current action do perfectly well.
//
// Only the selected bot's full card is patched each tick, so the map, the sparkline and the
// tables cost nothing for the bots you are not looking at.

const MINI_HTML = `
  <div class="row1">
    <span class="dot" data-r="dot"></span>
    <span class="nm" data-r="name"></span>
    <span class="state" data-r="state"></span>
    <span class="lv" data-r="lv"></span>
  </div>
  <div class="act"><b data-r="action"></b><span data-r="subject"></span></div>
  <div class="sub"><span class="where" data-r="sub"></span><span class="gp" data-r="gold"></span></div>
  <div class="bars">
    <div><span data-r="hp"></span><div class="bar hp"><i data-r="hpbar"></i></div></div>
    <div><span data-r="mp"></span><div class="bar mp"><i data-r="mpbar"></i></div></div>
    <div><span data-r="xp"></span><div class="bar xp"><i data-r="xpbar"></i></div></div>
  </div>`;

function makeMini(id) {
  const root = document.createElement("div");
  root.className = "mini";
  root.innerHTML = MINI_HTML;

  const el = {};
  for (const node of root.querySelectorAll("[data-r]")) el[node.dataset.r] = node;

  root.addEventListener("click", () => select(id));
  return { root, el };
}

function patchMini(mini, b) {
  const el = mini.el;

  const dot = dotFor(b);
  if (el.dot.dataset.cls !== dot) {
    el.dot.className = "dot " + dot;
    el.dot.dataset.cls = dot;
  }

  text(el.name, b.characterName || b.id);
  text(el.state, b.state);
  if (el.state.dataset.cls !== b.state) {
    el.state.className = "state " + b.state;
    el.state.dataset.cls = b.state;
  }

  text(el.lv, b.level ? `L${b.level} ${b.class}` : "");
  text(el.action, b.currentAction || "—");
  text(el.subject, b.currentSubject ? " · " + b.currentSubject : "");

  const ever = b.secondsSinceGain !== null && b.secondsSinceGain !== undefined;

  text(el.sub,
    (b.mapName || ("map " + b.mapIndex)) + " · " +
    (b.state !== "Playing" ? b.state
      : ever ? `kill ${ago(b.secondsSinceGain)} ago`
      : `no kill · ${ago(b.secondsInGame)}`));

  text(el.gold, b.gold === null || b.gold === undefined
    ? "" : String(b.gold).replace(/\B(?=(\d{3})+(?!\d))/g, ",") + "g");

  text(el.hp, `HP ${b.healthPercent}%`);
  el.hpbar.style.width = pct(b.health, b.maxHealth) + "%";
  text(el.mp, `MP ${b.manaPercent}%`);
  el.mpbar.style.width = pct(b.mana, b.maxMana) + "%";
  text(el.xp, b.experiencePercent === null
    ? (b.atMaxLevel ? "XP max" : "XP —")
    : `XP ${b.experiencePercent}%`);
  el.xpbar.style.width = (b.experiencePercent ?? 0) + "%";
}

// Which bot fills the screen. Remembered, so a reload or a deploy does not throw you back to the
// first bot every time.
let selected = null;

try { selected = localStorage.getItem("mirbot.selected"); } catch (e) { }

function select(id) {
  if (selected === id) return;

  selected = id;
  try { localStorage.setItem("mirbot.selected", id); } catch (e) { }

  const detail = document.getElementById("botdetail");
  while (detail.firstChild) detail.firstChild.remove();

  for (const [botId, mini] of minis) mini.root.classList.toggle("on", botId === id);

  const card = cards.get(id);
  if (card) detail.appendChild(card.root);
}

function makeCard(id) {

  const root = document.createElement("div");
  root.className = "bot";
  root.innerHTML = CARD_HTML;

  const el = {};
  for (const node of root.querySelectorAll("[data-r]")) el[node.dataset.r] = node;

  for (const btn of root.querySelectorAll("[data-a]")) {
    const action = btn.dataset.a;
    btn.addEventListener("click", () =>
      action === "go" ? goMap(id) : send(id, action));
  }

  fillMap(el.map);
  return { root, el, sig: {} };
}

// --- patching ---------------------------------------------------------------------------------

/** Assign only when different: touching textContent needlessly still costs a layout pass. */
function text(node, value) {
  const v = value ?? "";
  if (node.textContent !== v) node.textContent = v;
}

function show(node, on) { node.classList.toggle("hide", !on); }

/** Rewrite a table body only when its contents actually changed. */
function rows(card, key, node, html) {
  if (card.sig[key] === html) return;
  card.sig[key] = html;
  node.innerHTML = html;
}

// Ten minutes with nothing earned. The threshold the whole dot exists for.
const STALE_SECONDS = 600;

function ago(seconds) {
  if (seconds === null || seconds === undefined) return "—";
  if (seconds < 60) return seconds + "s";
  const m = Math.floor(seconds / 60), s = seconds % 60;
  if (m < 60) return `${m}m${String(s).padStart(2, "0")}s`;
  return `${Math.floor(m / 60)}h${String(m % 60).padStart(2, "0")}m`;
}

// Red outranks yellow outranks green, deliberately.
//
// A bot looping through town for an hour is the exact failure this is meant to catch, and if
// "busy in town" could mask it the light would be green on the one bot that needs looking at.
// Grey covers both "not playing" and "has not earned anything yet", which are the two states
// where there is genuinely nothing to judge.
function idleSeconds(b) {
  // Before the first kill the clock runs from the moment the character entered the world, not
  // from nothing. A bot fifteen minutes in with nothing to show for it is in the same trouble as
  // one that stopped killing fifteen minutes ago, and a dash would have said nothing about the
  // very bot most worth looking at.
  const since = b.secondsSinceGain;
  return (since === null || since === undefined) ? b.secondsInGame : since;
}

function dotFor(b) {
  if (b.state !== "Playing") return "grey";

  const idle = idleSeconds(b);
  if (idle === null || idle === undefined) return "grey";
  if (idle > STALE_SECONDS) return "red";
  if (b.activity === "town" || b.activity === "travel" || b.activity === "quest") return "yellow";
  return "green";
}

const WHY = [
  ["moneyDiagnostic",   "Money"],
  ["sellDiagnostic",    "Selling"],
  ["weightDiagnostic",  "Weight"],
  ["supplyDiagnostic",  "Supplies"],
  ["repairDiagnostic",  "Repair"],
  ["reagentDiagnostic", "Reagents"],
  ["bookDiagnostic",    "Books"],
  ["gearDiagnostic",    "Gear"],
  ["bankStatus",        "Bank"]
];

function patch(card, b) {
  const el = card.el;

  const dot = dotFor(b);
  if (el.dot.dataset.cls !== dot) {
    el.dot.className = "dot " + dot;
    el.dot.dataset.cls = dot;
  }

  // Honest about which clock is being shown: claiming a "last kill" before there has been one
  // would be a lie told by the very widget meant to catch bots that are not killing anything.
  const ever = b.secondsSinceGain !== null && b.secondsSinceGain !== undefined;

  text(el.since, b.state !== "Playing" ? ""
    : ever ? `last kill: ${ago(b.secondsSinceGain)} ago`
    : `no kill yet · ${ago(b.secondsInGame)} in game`);

  rows(card, "why", el.why, WHY
    .filter(([k]) => b[k])
    .map(([k, label]) => `<div class="row"><b>${label}:</b> ${esc(b[k])}</div>`)
    .join("") || '<div class="row detail">nothing to report</div>');

  text(el.name, b.characterName || b.id);
  text(el.state, b.state);
  if (el.state.dataset.cls !== b.state) {
    el.state.className = "state " + b.state;
    el.state.dataset.cls = b.state;
  }
  show(el.dead, b.dead);

  text(el.action, b.currentAction || "—");
  text(el.subject, b.currentSubject ? " · " + b.currentSubject : "");
  text(el.detail, b.currentDetail);

  show(el.exit, !!b.exitReason);  text(el.exit, b.exitReason);
  show(el.error, !!b.lastError);  text(el.error, b.lastError);

  text(el.level, `${b.level} ${b.class}`);
  text(el.hp, `${b.health}/${b.maxHealth}`);
  el.hpbar.style.width = pct(b.health, b.maxHealth) + "%";
  text(el.mp, `${b.mana}/${b.maxMana}`);
  el.mpbar.style.width = pct(b.mana, b.maxMana) + "%";

  text(el.xp, b.experiencePercent === null
    ? (b.atMaxLevel ? "max level" : "—")
    : `${b.experiencePercent}%`);
  el.xpbar.style.width = (b.experiencePercent ?? 0) + "%";
  const coverage = b.xpCoverageSeconds || 0;
  text(el.xppace, b.xpRatePerHour == null
    ? `XP/hour: warming up (${Math.floor(coverage / 60)}/20m)`
    : `XP/hour (last ${Math.round(coverage / 60)}m of 2h): ${group(b.xpRatePerHour)}`);
  const eta = b.estimatedNextLevelSeconds;
  const etaText = b.atMaxLevel ? "max level" :
    b.experiencePercent === null ? "—" :
    b.xpRatePerHour == null ? "warming up" :
    Number(b.xpRatePerHour) <= 0 ? "not progressing" :
    eta == null ? "—" :
    eta >= 86400 ? `${Math.floor(eta / 86400)}d ${Math.floor(eta % 86400 / 3600)}h` :
    eta >= 3600 ? `${Math.floor(eta / 3600)}h ${Math.ceil(eta % 3600 / 60)}m` :
    `${Math.ceil(eta / 60)}m`;
  text(el.xpeta, `Est. next level: ${etaText}`);

  // Gold arrives as a STRING because it can exceed 2^53 - see the comment on BotStatus. Grouping
  // is done on the digits themselves rather than via Number(), which would quietly round it.
  text(el.gold, String(b.gold).replace(/\B(?=(\d{3})+(?!\d))/g, ","));

  // HUNT GOLD - the game store currency, and what the store step is buying or saving for.
  text(el.huntgold, String(b.huntGold ?? 0).replace(/\B(?=(\d{3})+(?!\d))/g, ","));
  text(el.storestatus, b.storeStatusText ? "Store: " + b.storeStatusText : "");
  text(el.famestatus, b.fameStatusText ? "Fame: " + b.fameStatusText : "");

  drawSpark(card, goldSeries.get(b.id));
  drawMap(card, b);

  text(el.loc, `${b.mapName || ("map " + b.mapIndex)} · ${b.x},${b.y}` +
               (b.inSafeZone ? " · safe" : ""));
  text(el.locdetail, "map " + b.mapIndex);
  text(el.farmdest, b.farmingDestinationName || "—");
  text(el.farmreason, b.farmingDestinationReason ||
    "No hunting-ground choice recorded since this host started.");
  text(el.coverage, b.explorationTotalSectors
    ? `${b.explorationVisitedSectors}/${b.explorationTotalSectors} sectors`
    : "—");

  const coverageMode = b.explorationMode || "none";
  let coverageTarget = "";
  if (coverageMode !== "none") {
    if (!b.explorationTargetLastVisitedUtc) coverageTarget = " · target unseen";
    else {
      const seconds = Math.max(0,
        Math.floor((Date.now() - Date.parse(b.explorationTargetLastVisitedUtc)) / 1000));
      coverageTarget = ` · target last visited ${ago(seconds)} ago`;
    }
  }
  text(el.coverdetail, coverageMode === "none" ? "no exploration target"
                                                : coverageMode + coverageTarget);
  text(el.bag, `${b.bagWeight}/${b.maxBagWeight} (${b.bagPercent}%)`);
  text(el.trip, b.tripPhase || "—");
  text(el.tripdetail, b.tripStatus);

  text(el.counters, `${b.decisions} dec · ${b.resyncs} resync · ${b.detours} detour` +
                    (b.droppedPackets ? ` · ${b.droppedPackets} dropped` : ""));
  // Abbreviated on purpose: spelled out, this wrapped to five lines and made the whole card
  // taller than the map beside it.
  text(el.counters2,
    `${b.casts} cast · ${b.fightsAbandoned} abandoned · ${b.dangerAvoided} avoided · ` +
    `${b.learnedBlockedCells} cells · ${b.doorwaysCleared} doors`);
  text(el.bank, b.bankStatus || "—");

  const storage = b.storage || [];
  text(el.equiphead, `Equipped (${b.equipment.length})`);
  text(el.baghead, `Bag (${b.inventory.length}/48)`);
  text(el.storehead, `Storage (${storage.length})`);

  renderSlots(card, el.equip, b.equipment);
  renderCells(card, "bag", el.baglist, b.inventory, 48);
  renderCells(card, "store", el.storelist, storage, 0);

  // SKILLS. Two levels are shown and they are not the same thing: `Lvl` is the SKILL's own
  // level (1-3, trained on its own experience track), and the greyed-out rows are spells the
  // character has learnt that the BOT cannot use. That second case is not hypothetical - Wizzler
  // learnt Fire Wall and has never cast it, because ground-targeted spells need a location in the
  // packet and the cast path only sends a target. A skill list that hid that would be lying.
  const skills = b.skills || [];
  const unusable = skills.filter(s => s.use === "unused").length;

  text(el.skillshead, skills.length
    ? `Skills (${skills.length}${unusable ? `, ${unusable} unused` : ""})`
    : "Skills");

  rows(card, "skills", el.skills, skills.length
    ? skills.map(s => `<tr title="${esc(s.castable
          ? s.school + (s.needLevel ? ` - next level at character level ${s.needLevel}` : "")
          : s.why)}" style="${s.use === "unused" ? "opacity:.45" : ""}">
        <td>${esc(s.name)}${s.use === "active" ? ""
              : ` <span class="count">${s.use}</span>`}</td>
        <td class="num">${s.level}</td>
        <td>${s.nextExperience > 0
              ? `<span class="count">${group(s.experience)}/${group(s.nextExperience)}${
                  s.level >= 3 ? " pages" : ""}</span>`
              : `<span class="count">max</span>`}</td>
      </tr>`).join("")
    : `<tr><td colspan="3" style="color:var(--dim)">none learnt</td></tr>`);

  // PETS. The heading carries the standing order, because a pet doing nothing is far more often
  // the mode than the pet: PetMode.Move and PetMode.None make the server null its target outright
  // (MonsterObject.ProcessAI), so "Pets - Move" explains an idle skeleton at a glance.
  const pets = b.pets || [];
  text(el.petshead, pets.length
    ? `Pets (${pets.length})${b.petMode ? " - " + b.petMode : ""}`
    : `Pets${b.petMode ? " - " + b.petMode : ""}`);

  rows(card, "pets", el.pets, pets.length
    ? pets.map(p => `<tr>
        <td>${esc(p.name)}${p.level ? ` <span class="count">L${p.level}</span>` : ""}</td>
        <td class="${p.maxHealth > 0 && p.healthPercent <= 33 ? "bad" : ""}">${
          // UNKNOWN IS NOT ZERO. A monster's health only reaches us via
          // S.DataObjectHealthMana, which the server does not always send for a pet - and
          // rendering a missing value as "0%" reads as a pet about to die.
          p.maxHealth > 0 ? p.healthPercent + "%" : "?"}</td>
        <td>${p.distance}</td>
      </tr>`).join("")
    : `<tr><td colspan="3" style="color:var(--dim)">none summoned</td></tr>`);

  // BUFFS - what the server says is running now. A timed item buff pauses in a safe zone, so
  // "paused" is normal in town, not a stuck timer.
  const buffs = b.buffs || [];
  text(el.buffshead, buffs.length ? `Buffs (${buffs.length})` : "Buffs");
  rows(card, "buffs", el.buffs, buffs.length
    ? buffs.map(f => `<tr title="${esc((f.stats || []).map(x => x.name + " +" + x.amount).join(", "))}">
        <td>${esc(f.name)}</td>
        <td>${f.permanent ? `<span class="count">permanent</span>`
          : (f.paused ? `<span class="count">paused in town</span> ` : "") + buffLeft(f.remainingSeconds)}</td>
      </tr>`).join("")
    : `<tr><td colspan="2" style="color:var(--dim)">none</td></tr>`);

  // QUESTS - the character's log, with the errand's current step in the heading.
  const quests = b.quests || [];
  text(el.questshead, "Quests" + (b.questStatusText ? " - " + b.questStatusText : ""));
  rows(card, "quests", el.quests, quests.length
    ? quests.map(q => `<tr style="${q.completed ? "opacity:.5" : ""}">
        <td>${esc(q.name)}${q.daily ? ` <span class="count">daily</span>` : ""}</td>
        <td>${q.completed ? `<span class="count">done</span>`
          : q.readyToHandIn ? `<span class="count">ready to hand in</span>` : esc(q.progress)}</td>
      </tr>`).join("")
    : `<tr><td colspan="2" style="color:var(--dim)">none</td></tr>`);

  rows(card, "hist", el.hist, b.history.slice().reverse().map(e => `<tr>
      <td>${esc(e.action)} ${e.count > 1 ? `<span class="count">x${e.count}</span>` : ""}</td>
      <td>${esc(e.subject)}</td>
      <td>${new Date(e.lastAt).toLocaleTimeString()}</td>
    </tr>`).join(""));
}

// --- gold history -----------------------------------------------------------------------------
//
// Fetched on its own slow cadence, not on the 1Hz status poll. The file only gains a line every
// five minutes, so re-reading it every second would be four pointless file scans a second on the
// single HTTP thread that also has to serve the status.

const goldSeries = new Map();
let goldFetchedAt = 0;

async function loadGold(ids) {
  const now = Date.now();
  if (now - goldFetchedAt < 30000) return;
  goldFetchedAt = now;

  for (const id of ids) {
    try {
      const r = await fetch("/api/gold?bot=" + encodeURIComponent(id), { cache:"no-store" });
      goldSeries.set(id, await r.json());
    } catch (e) { /* the chart is optional */ }
  }
}

function drawSpark(card, points) {
  const line = card.el.sparkline, fill = card.el.sparkfill;
  if (!line) return;

  if (!points || points.length < 2) {
    if (card.sig.spark !== "") {
      card.sig.spark = "";
      line.removeAttribute("d");
      fill.removeAttribute("d");
    }
    return;
  }

  // Number() is safe HERE and only here: these become pixel coordinates, and a gold value would
  // have to pass nine quadrillion before the rounding moved the line by one pixel.
  const vals = points.map(pt => Number(pt.gold));
  const lo = Math.min(...vals), hi = Math.max(...vals), span = (hi - lo) || 1;
  const step = 100 / (vals.length - 1);

  const d = vals.map((v, i) =>
    (i ? "L" : "M") + (i * step).toFixed(1) + "," +
    (24 - ((v - lo) / span) * 22).toFixed(1)).join("");

  if (card.sig.spark === d) return;
  card.sig.spark = d;

  line.setAttribute("d", d);
  fill.setAttribute("d", d + "L100,26 L0,26 Z");
}

// --- the map ----------------------------------------------------------------------------------
//
// The server sends one bit per cell, base64, row-major. That is drawn to an offscreen canvas ONCE
// per map and then blitted, because the largest map here is 1360x1500 - two million cells, which
// is fine to rasterise occasionally and hopeless to redraw at 1Hz.
//
// Only the two markers move, so each poll copies the cached bitmap and draws two dots on top.

const mapCache = new Map();     // "index:version" -> offscreen canvas
const mapPending = new Set();

async function mapBitmap(index) {
  for (const [key, value] of mapCache) if (key.startsWith(index + ":")) return value;

  if (mapPending.has(index)) return null;
  mapPending.add(index);

  try {
    // &doors=1 keeps a browser from serving an hour-cached payload from before doors existed.
    const m = await (await fetch("/api/map?index=" + index + "&doors=1")).json();
    const off = document.createElement("canvas");
    off.width = m.width; off.height = m.height;
    off.doors = m.doors || [];

    const ctx = off.getContext("2d");
    const img = ctx.createImageData(m.width, m.height);

    // atob gives a binary string; one bit per cell, in the same row-major order the server packed.
    const bytes = atob(m.mask);

    for (let i = 0, n = m.width * m.height; i < n; i++) {
      const walkable = (bytes.charCodeAt(i >> 3) >> (i & 7)) & 1;
      const o = i * 4;

      // Walkable is the lighter of the two: the eye should pick out the floor, not the rock.
      img.data[o] = img.data[o + 1] = img.data[o + 2] = walkable ? 58 : 22;
      img.data[o + 3] = 255;
    }

    ctx.putImageData(img, 0, 0);
    mapCache.set(index + ":" + m.version, off);
    return off;
  } catch (e) {
    return null;
  } finally {
    mapPending.delete(index);
  }
}

async function drawMap(card, b) {
  const canvas = card.el.canvas;
  if (!canvas) return;

  if (!b.mapIndex) { text(card.el.legend, ""); return; }

  const off = await mapBitmap(b.mapIndex);

  if (!off) {
    text(card.el.legend, "no grid for this map");
    return;
  }

  if (canvas.width !== off.width || canvas.height !== off.height) {
    canvas.width = off.width;
    canvas.height = off.height;
  }

  const ctx = canvas.getContext("2d");
  ctx.drawImage(off, 0, 0);

  // Markers scale with the map so they stay visible on a 1360-wide cave and do not swamp a
  // 200-wide town.
  const r = Math.max(2, Math.round(off.width / 160));

  // The route the bot is actually walking (its own A* path), coloured by purpose.
  const route = b.route || [];
  const routeColour = ROUTE_COLOURS[b.routeKind] || "#e2b341";
  if (route.length >= 2) {
    ctx.strokeStyle = "#000";
    ctx.lineJoin = "round";
    ctx.lineCap = "round";
    const trace = () => {
      ctx.beginPath();
      ctx.moveTo(b.x + 0.5, b.y + 0.5);
      for (let i = 0; i + 1 < route.length; i += 2) ctx.lineTo(route[i] + 0.5, route[i + 1] + 0.5);
      ctx.stroke();
    };
    ctx.lineWidth = Math.max(2, r);          // dark casing so the line reads on light floor
    trace();
    ctx.strokeStyle = routeColour;
    ctx.lineWidth = Math.max(1, r / 2);
    trace();
  }

  // Doors: exits to other maps, labelled with where they lead.
  const doors = off.doors || [];
  if (doors.length) {
    const font = Math.max(9, r * 4);
    ctx.font = `${font}px sans-serif`;
    ctx.textBaseline = "middle";
    ctx.lineJoin = "round";
    for (const d of doors) {
      ctx.fillStyle = "#c77dff";
      ctx.strokeStyle = "#000";
      ctx.lineWidth = Math.max(1, r / 2);
      ctx.fillRect(d.x - r, d.y - r, r * 2, r * 2);
      ctx.strokeRect(d.x - r, d.y - r, r * 2, r * 2);

      // Keep labels on the canvas near the right and bottom edges.
      const label = d.to;
      const w = ctx.measureText(label).width;
      const lx = d.x + r * 2 + w > off.width ? d.x - r * 2 - w : d.x + r * 2;
      const ly = Math.min(Math.max(d.y, font), off.height - font);
      ctx.lineWidth = Math.max(2, font / 4);
      ctx.strokeText(label, lx, ly);
      ctx.fillStyle = "#e9d5ff";
      ctx.fillText(label, lx, ly);
    }
  }

  if (b.destX !== null && b.destX !== undefined) {
    ctx.strokeStyle = route.length >= 2 ? routeColour : "#e2b341";
    ctx.lineWidth = Math.max(1, r / 2);
    ctx.beginPath();
    ctx.moveTo(b.destX - r, b.destY - r); ctx.lineTo(b.destX + r, b.destY + r);
    ctx.moveTo(b.destX + r, b.destY - r); ctx.lineTo(b.destX - r, b.destY + r);
    ctx.stroke();
  }

  ctx.fillStyle = "#6aa9ff";
  ctx.beginPath();
  ctx.arc(b.x, b.y, r, 0, Math.PI * 2);
  ctx.fill();

  const doorNote = doors.length ? ` · ${doors.length} door${doors.length === 1 ? "" : "s"}` : "";
  const routeNote = route.length >= 2
    ? ` · route: ${ROUTE_NAMES[b.routeKind] || b.routeKind} (${route.length / 2} pts)` : "";
  text(card.el.legend,
    ((b.destX !== null && b.destX !== undefined)
      ? `${off.width}x${off.height} · you ${b.x},${b.y} · heading ${b.destX},${b.destY}`
      : `${off.width}x${off.height} · you ${b.x},${b.y}`) + routeNote + doorNote);
}

const ROUTE_COLOURS = {
  travel: "#f0abfc", town: "#e2b341", target: "#f87171",
  roam: "#6ee7b7", loot: "#93c5fd", move: "#e5e7eb"
};
const ROUTE_NAMES = {
  travel: "to exit", town: "town errand", target: "to monster",
  roam: "roaming", loot: "to loot", move: "moving"
};

// --- memory views -----------------------------------------------------------------------------
// --- memory views -----------------------------------------------------------------------------
//
// Only fetched while the panel is open, and then slowly. These read files and copy lists; there is
// no reason to pay for it when nobody is looking at them.

const money = n => Math.round(n).toLocaleString();
const group = v => String(v).replace(/\B(?=(\d{3})+(?!\d))/g, ",");

// The rows are fetched slowly and kept, so sorting, searching and filtering are all local and
// instant. State lives here rather than in the DOM because the table body is rewritten whenever
// anything changes - the controls themselves sit OUTSIDE it and are built once, so a half-typed
// search term is never destroyed.

let huntRows = [];
let huntBots = [];

const huntState = { key: "score", dir: -1, q: "", cls: "", mine: false, hideLethal: false,
                    measuredOnly: false };

const HUNT_COLS = [
  { key: "mapName",        label: "Map",     num: false },
  { key: "class",          label: "Class",   num: false },
  { key: "levelBand",      label: "Band",    num: true  },
  { key: "averagePerHour", label: "Avg/h",   num: true  },
  { key: "bestPerHour",    label: "Best/h",  num: true  },
  { key: "hoursSampled",   label: "Hours",   num: true  },
  { key: "deaths",         label: "Deaths",  num: true  },
  { key: "score",          label: "Score",   num: true  }
];

function huntFiltered() {
  const here = new Set(huntBots.map(b => b.class + "|" + b.mapIndex));
  const q = huntState.q.trim().toLowerCase();

  let rows = huntRows.filter(r => {
    if (huntState.hideLethal && r.lethal) return false;
    if (huntState.measuredOnly && !(r.averagePerHour > 0)) return false;
    if (huntState.cls && r.class !== huntState.cls) return false;
    if (huntState.mine && !here.has(r.class + "|" + r.mapIndex)) return false;
    if (q && !(r.mapName + " " + r.class).toLowerCase().includes(q)) return false;
    return true;
  });

  const key = huntState.key, dir = huntState.dir;

  rows.sort((a, b) => {
    const x = a[key], y = b[key];
    const c = typeof x === "string" ? x.localeCompare(y) : (x - y);
    return c * dir;
  });

  return { rows, here };
}

function renderHunting() {
  const { rows, here } = huntFiltered();

  document.getElementById("huntcount").textContent =
    rows.length === huntRows.length
      ? `${huntRows.length} record(s)`
      : `${rows.length} of ${huntRows.length} record(s)`;

  const head = HUNT_COLS.map(c => {
    const active = huntState.key === c.key;
    const arrow = active ? `<span class="arrow">${huntState.dir < 0 ? "\u25be" : "\u25b4"}</span>` : "";
    return `<th class="sortable${c.num ? " num" : ""}" data-sort="${c.key}">${c.label} ${arrow}</th>`;
  }).join("");

  document.getElementById("hunttable").innerHTML = "<table><tr>" + head + "</tr>" +
    rows.map(r => {
      const cls = r.lethal ? "lethal" : here.has(r.class + "|" + r.mapIndex) ? "here" : "";
      return "<tr class='" + cls + "'><td>" + esc(r.mapName) +
        (r.lethal ? "<span class='tag bad'>lethal</span>" : "") + "</td>" +
        "<td>" + esc(r.class) + "</td><td class='num'>" + r.levelBand + "</td>" +
        "<td class='num'>" + money(r.averagePerHour) + "</td>" +
        "<td class='num'>" + money(r.bestPerHour) + "</td>" +
        "<td class='num'>" + r.hoursSampled.toFixed(2) + "</td>" +
        "<td class='num'>" + r.deaths + "</td>" +
        "<td class='num'>" + money(r.score) + "</td></tr>";
    }).join("") +
    (rows.length ? "" : "<tr><td colspan='8' class='detail'>nothing matches</td></tr>") +
    "</table>";

  for (const th of document.querySelectorAll("#hunttable th.sortable")) {
    th.addEventListener("click", () => {
      const key = th.dataset.sort;

      // Same column flips direction; a new column starts descending for numbers and ascending
      // for names, which is what you almost always want first.
      if (huntState.key === key) huntState.dir = -huntState.dir;
      else {
        huntState.key = key;
        huntState.dir = HUNT_COLS.find(c => c.key === key).num ? -1 : 1;
      }

      renderHunting();
    });
  }
}

function huntClasses() {
  const sel = document.getElementById("huntclass");
  const classes = [...new Set(huntRows.map(r => r.class))].filter(Boolean).sort();
  const sig = classes.join(",");

  if (sel.dataset.sig === sig) return;
  sel.dataset.sig = sig;

  const chosen = sel.value;
  sel.innerHTML = "<option value=''>all classes</option>" +
    classes.map(c => `<option value="${esc(c)}">${esc(c)}</option>`).join("");
  sel.value = chosen;
}

async function loadHunting(bots) {
  huntBots = bots;

  try { huntRows = await (await fetch("/api/hunting", { cache:"no-store" })).json(); }
  catch (e) { return; }

  huntClasses();
  renderHunting();
}

document.getElementById("huntq").addEventListener("input", e => {
  huntState.q = e.target.value; renderHunting();
});
document.getElementById("huntclass").addEventListener("change", e => {
  huntState.cls = e.target.value; renderHunting();
});
for (const [id, field] of [["huntmine","mine"], ["huntlethal","hideLethal"],
                           ["huntmeasured","measuredOnly"]]) {
  document.getElementById(id).addEventListener("change", e => {
    huntState[field] = e.target.checked; renderHunting();
  });
}

let deathRows = [];

// Rebuild a filter dropdown from the rows, keeping whatever was chosen.
function fillFilter(selectId, allLabel, pairs) {
  const sel = document.getElementById(selectId), chosen = sel.value;
  const unique = [...new Map(pairs.filter(([k]) => k)).entries()]
    .sort((a, b) => String(a[1]).localeCompare(String(b[1])));
  const html = `<option value="">${esc(allLabel)}</option>` +
    unique.map(([k, label]) => `<option value="${esc(k)}">${esc(label)}</option>`).join("");
  // Rewriting the options closes a dropdown the user has open, so only when they changed.
  if (sel.dataset.options === html) return;
  sel.dataset.options = html;
  sel.innerHTML = html;
  sel.value = unique.some(([k]) => k === chosen) ? chosen : "";
}

function matchesQuery(q, fields) {
  if (!q) return true;
  const hay = fields.map(f => String(f ?? "")).join(" ").toLowerCase();
  return q.toLowerCase().split(/\s+/).filter(Boolean).every(w => hay.includes(w));
}

async function loadDeaths() {
  try { deathRows = await (await fetch("/api/deaths?take=500", { cache:"no-store" })).json(); }
  catch (e) { return; }

  fillFilter("deathchar", "all characters", deathRows.map(d => [levelKey(d), d.character || d.bot]));
  fillFilter("deathmap", "all maps", deathRows.map(d => [d.mapName, d.mapName]));
  fillFilter("deathkiller", "all killers", deathRows.map(d => [d.killer, d.killer]));
  renderDeaths();
}

function renderDeaths() {
  const q = document.getElementById("deathq").value.trim();
  const who = document.getElementById("deathchar").value;
  const map = document.getElementById("deathmap").value;
  const killer = document.getElementById("deathkiller").value;

  const rows = deathRows.filter(d =>
    (!who || levelKey(d) === who) && (!map || d.mapName === map) &&
    (!killer || d.killer === killer) &&
    matchesQuery(q, [d.character, d.bot, d.class, d.mapName, d.killer, d.level]));

  document.getElementById("deathcount").textContent =
    `${rows.length} of ${deathRows.length} death${deathRows.length === 1 ? "" : "s"}`;

  const box = document.getElementById("deathtable");

  if (!rows.length) {
    box.innerHTML = `<div class='detail'>${deathRows.length ? "no deaths match" : "none recorded yet"}</div>`;
    return;
  }

  box.innerHTML = "<table><tr><th>When</th><th>Who</th><th class='num'>Lvl</th><th>Map</th>" +
    "<th>Killed by</th><th class='num'>Gold held</th><th>Where</th></tr>" +
    rows.map(d => "<tr><td>" + new Date(d.utc).toLocaleString() + "</td>" +
      "<td>" + esc(d.character || d.bot) + "</td><td class='num'>" + d.level + "</td>" +
      "<td>" + esc(d.mapName) + "</td><td>" + esc(d.killer) + "</td>" +
      "<td class='num'>" + group(d.gold) + "</td>" +
      "<td>" + d.x + "," + d.y + "</td></tr>").join("") + "</table>";
}

document.getElementById("deathq").addEventListener("input", renderDeaths);

let questRows = [];

async function loadQuestLog() {
  try { questRows = await (await fetch("/api/quest-log?take=2000", { cache:"no-store" })).json(); }
  catch (e) { return; }
  fillFilter("questchar", "all bots", questRows.map(r => [levelKey(r), r.character || r.bot]));
  fillFilter("questname", "all quests", questRows.map(r => [r.quest, r.quest]));
  renderQuestLog();
}

function renderQuestLog() {
  const q = document.getElementById("questq").value.trim();
  const who = document.getElementById("questchar").value;
  const quest = document.getElementById("questname").value;
  const evt = document.getElementById("questevent").value;
  const rows = questRows.filter(r =>
    (!who || levelKey(r) === who) && (!quest || r.quest === quest) && (!evt || r.event === evt) &&
    matchesQuery(q, [r.quest, r.rewards, r.map, r.character, r.bot, r.class, r.event]));
  document.getElementById("questcount").textContent =
    `${rows.length} of ${questRows.length} entr${questRows.length === 1 ? "y" : "ies"}`;
  const box = document.getElementById("questtable");
  if (!rows.length) {
    box.innerHTML = `<div class='detail'>${questRows.length ? "no entries match" : "none recorded yet"}</div>`;
    return;
  }
  box.innerHTML = "<table><tr><th>When</th><th>Bot</th><th class='num'>Lvl</th><th>Quest</th>" +
    "<th>Event</th><th>Map</th><th>Rewards / note</th></tr>" +
    rows.map(r => "<tr><td>" + new Date(r.utc).toLocaleString() + "</td>" +
      "<td>" + esc(r.character || r.bot) + "</td><td class='num'>" + r.level + "</td>" +
      "<td>" + esc(r.quest) + "</td><td>" + esc(r.event) + "</td>" +
      "<td>" + esc(r.map) + "</td><td>" + (r.rewards ? esc(r.rewards) : "—") + "</td></tr>").join("") +
    "</table>";
}

document.getElementById("questq").addEventListener("input", renderQuestLog);
for (const id of ["questchar", "questname", "questevent"])
  document.getElementById(id).addEventListener("change", renderQuestLog);

let bossRows = [];

async function loadBossKills() {
  try { bossRows = await (await fetch("/api/boss-kills?take=1000", { cache:"no-store" })).json(); }
  catch (e) { return; }
  fillFilter("bosschar", "all characters", bossRows.map(r => [levelKey(r), r.character || r.bot]));
  fillFilter("bossname", "all bosses", bossRows.map(r => [r.monster, r.monster]));
  fillFilter("bossmap", "all maps", bossRows.map(r => [r.map, r.map]));
  renderBossKills();
}

function renderBossKills() {
  const q = document.getElementById("bossq").value.trim();
  const who = document.getElementById("bosschar").value;
  const boss = document.getElementById("bossname").value;
  const map = document.getElementById("bossmap").value;
  const kind = document.getElementById("bosskind").value;
  const rows = bossRows.filter(r =>
    (!who || levelKey(r) === who) && (!boss || r.monster === boss) &&
    (!map || r.map === map) && (!kind || r.kind === kind) &&
    matchesQuery(q, [r.monster, r.map, r.character, r.bot, r.class, (r.loot || []).join(" "),
      (r.dropped || []).map(d => d.name).join(" ")]));
  document.getElementById("bosscount").textContent =
    `${rows.length} of ${bossRows.length} kill${bossRows.length === 1 ? "" : "s"}`;
  const box = document.getElementById("bosstable");
  if (!rows.length) {
    box.innerHTML = `<div class='detail'>${bossRows.length ? "no kills match" : "none recorded yet"}</div>`;
    return;
  }
  box.innerHTML = "<table><tr><th>When</th><th>Boss</th><th>Kind</th><th>Who</th>" +
    "<th class='num'>Lvl</th><th>Map</th><th>Where</th><th>Dropped</th><th>Picked up</th></tr>" +
    rows.map(r => "<tr><td>" + new Date(r.utc).toLocaleString() + "</td>" +
      "<td>" + esc(r.monster) + "</td><td>" + esc(r.kind) + "</td>" +
      "<td>" + esc(r.character || r.bot) + "</td><td class='num'>" + r.level + "</td>" +
      "<td>" + esc(r.map) + "</td><td>" + r.x + "," + r.y + "</td>" +
      "<td>" + bossDrops(r.dropped) + "</td>" +
      "<td>" + ((r.loot && r.loot.length) ? esc(r.loot.join(", ")) : "—") + "</td></tr>").join("") +
    "</table>";
}

// Grouped by name and outcome: "Rejuvenation Potion x3 (taken)", "Gold 90,000 (taken)".
function bossDrops(drops) {
  if (!drops || !drops.length) return "—";
  const groups = new Map();
  for (const d of drops) {
    const key = d.name + "\u0001" + d.outcome;
    const g = groups.get(key) || { name: d.name, outcome: d.outcome, gold: d.gold, n: 0, sum: 0 };
    g.n++; g.sum += d.count; groups.set(key, g);
  }
  const colour = { taken: "", left: "color:var(--bad,#c33)", gone: "opacity:.6" };
  return [...groups.values()].map(g =>
    `<span style="${colour[g.outcome] || ""}">${esc(g.name)}` +
    (g.gold ? " " + group(g.sum) : g.n > 1 ? " x" + g.n : "") +
    ` (${esc(g.outcome)})</span>`).join(", ");
}

document.getElementById("bossq").addEventListener("input", renderBossKills);
for (const id of ["bosschar", "bossname", "bossmap", "bosskind"])
  document.getElementById(id).addEventListener("change", renderBossKills);
for (const id of ["deathchar", "deathmap", "deathkiller"])
  document.getElementById(id).addEventListener("change", renderDeaths);

// DROP LOOKUP.
//
// Searched on demand rather than polled: the host reads up to three 32MB log files to answer, so
// this must never join the one-second refresh. Deliberately NOT wired to the memory auto-refresh
// below for the same reason.
async function loadLoot() {
  const q = document.getElementById("lootq").value.trim();
  const box = document.getElementById("loottable");
  const note = document.getElementById("lootnote");

  if (q.length < 2) {
    box.innerHTML = "<div class='detail'>type at least two characters</div>";
    return;
  }

  note.textContent = "searching...";

  let rows;
  try { rows = await (await fetch("/api/loot?take=400&item=" + encodeURIComponent(q),
                                  { cache:"no-store" })).json(); }
  catch (e) { note.textContent = "search failed"; return; }

  note.textContent = `${rows.length} result(s) for "${q}"`;

  if (!rows.length) {
    box.innerHTML = "<div class='detail'>never looted in the retained log. " +
      "Note the log rotates, so this is not proof it has never dropped.</div>";
    return;
  }

  // A per-map tally first: "where does this come from" is the question being asked far more often
  // than "list every instance", and scrolling 400 rows to count them by eye is not an answer.
  const byMap = {};
  for (const r of rows) byMap[r.mapName || "?"] = (byMap[r.mapName || "?"] || 0) + 1;

  const summary = Object.entries(byMap).sort((a, b) => b[1] - a[1])
    .map(([m, n]) => `${esc(m)} <span class="count">x${n}</span>`).join(" &middot; ");

  box.innerHTML = "<div class='detail' style='margin:6px 0'>" + summary + "</div>" +
    "<table><tr><th>When</th><th>Item</th><th>Who</th><th>Class</th><th class='num'>Lvl</th>" +
    "<th>Map</th><th>Where</th></tr>" +
    rows.map(r => "<tr><td>" + esc(r.time) + "</td><td>" + esc(r.item) + "</td>" +
      "<td>" + esc(r.character || r.bot) + "</td><td>" + esc(r.class || "—") +
      "</td><td class='num'>" + r.level + "</td>" +
      "<td>" + esc(r.mapName) + "</td>" +
      "<td>" + r.x + "," + r.y + "</td></tr>").join("") + "</table>";
}

document.getElementById("lootgo").addEventListener("click", loadLoot);
document.getElementById("lootq").addEventListener("keydown", e => {
  if (e.key === "Enter") loadLoot();
});

let memFetchedAt = 0;
let levelRows = [];
let mapTripRows = [];
let upgradeRows = [];
let skillRows = [];

function fillProgressCharacters(selectId, rows) {
  const sel = document.getElementById(selectId), chosen = sel.value;
  const names = [...new Map(rows.map(r => [levelKey(r), r.character || r.bot])).entries()]
    .sort((a,b) => a[1].localeCompare(b[1]));
  sel.innerHTML = "<option value=''>all characters</option>" +
    names.map(([key,name]) => `<option value="${esc(key)}">${esc(name)}</option>`).join("");
  sel.value = chosen;
}

function renderUpgrades() {
  const chosen = document.getElementById("upgradechar").value;
  const rows = upgradeRows.filter(r => !chosen || levelKey(r) === chosen);
  const box = document.getElementById("upgradetable");
  if (!rows.length) { box.innerHTML = "<div class='detail'>none recorded yet</div>"; return; }
  box.innerHTML = "<table><tr><th>Time (local)</th><th>Bot</th><th>Class</th>" +
    "<th>Previous gear</th><th>New gear</th><th>Score increase</th></tr>" +
    rows.map(r => "<tr><td>" + new Date(r.utc).toLocaleString() + "</td><td>" +
      esc(r.character || r.bot) + "</td><td>" + esc(r.class) + "</td><td>" +
      esc(r.previousGear) + "</td><td>" + esc(r.newGear) +
      "</td><td class='num'>+" + r.scoreIncrease + "</td></tr>").join("") + "</table>";
}

async function loadUpgrades() {
  try { upgradeRows = await (await fetch("/api/upgrades?take=1000", {cache:"no-store"})).json(); }
  catch (e) { document.getElementById("upgradetable").textContent = "upgrade history unavailable"; return; }
  fillProgressCharacters("upgradechar", upgradeRows);
  renderUpgrades();
}
document.getElementById("upgradechar").addEventListener("change", renderUpgrades);

function renderSkills() {
  const chosen = document.getElementById("skillchar").value;
  const rows = skillRows.filter(r => !chosen || levelKey(r) === chosen);
  const box = document.getElementById("skilltable");
  if (!rows.length) { box.innerHTML = "<div class='detail'>none recorded yet</div>"; return; }
  box.innerHTML = "<table><tr><th>Time (local)</th><th>Skill</th><th>Bot</th>" +
    "<th>Class</th><th>Result</th><th>Source</th></tr>" +
    rows.map(r => "<tr><td>" + new Date(r.utc).toLocaleString() + "</td><td>" +
      esc(r.skill) + "</td><td>" + esc(r.character || r.bot) + "</td><td>" +
      esc(r.class) + "</td><td>" + (r.success ? "success" : "fail") +
      "</td><td>" + esc(r.source) + "</td></tr>").join("") + "</table>";
}

async function loadSkills() {
  try { skillRows = await (await fetch("/api/skills-history?take=1000", {cache:"no-store"})).json(); }
  catch (e) { document.getElementById("skilltable").textContent = "skill history unavailable"; return; }
  fillProgressCharacters("skillchar", skillRows);
  renderSkills();
}
document.getElementById("skillchar").addEventListener("change", renderSkills);

function tripStatus(r) { return !r.arrivedUtc ? (r.leftUtc ? "never" : "active") : (r.leftUtc ? "left" : "active"); }

function renderMapTrips() {
  const chosen = document.getElementById("maptripchar").value;
  const map = document.getElementById("maptripmap").value;
  const status = document.getElementById("maptripstatus").value;
  const q = document.getElementById("maptripq").value.trim();
  const rows = mapTripRows.filter(r =>
    (!chosen || levelKey(r) === chosen) && (!map || r.map === map) &&
    (!status || tripStatus(r) === status) &&
    matchesQuery(q, [r.map, r.selectionReason, r.leavingReason, r.character, r.bot, r.class]));
  document.getElementById("maptripcount").textContent =
    `${rows.length} of ${mapTripRows.length} trip${mapTripRows.length === 1 ? "" : "s"}`;
  const box = document.getElementById("maptriptable");
  if (!rows.length) {
    box.innerHTML = `<div class='detail'>${mapTripRows.length ? "no trips match" : "none recorded yet"}</div>`;
    return;
  }
  box.innerHTML = "<table><tr><th>Selected (local)</th><th>Character</th><th>Class</th>" +
    "<th>Destination</th><th>Reason selected</th><th>Arrived</th><th>Kills*</th>" +
    "<th>Left (local)</th><th>Reason left</th></tr>" +
    rows.map(r => "<tr><td>" + new Date(r.selectedUtc).toLocaleString() + "</td><td>" +
      esc(r.character || r.bot) + "</td><td>" + esc(r.class || "—") + "</td><td>" +
      esc(r.map) + "</td><td>" + esc(r.selectionReason) + "</td><td>" +
      (r.arrivedUtc ? new Date(r.arrivedUtc).toLocaleString() : "—") +
      "</td><td class='num'>" + r.creditedKills + "</td><td>" +
      (r.leftUtc ? new Date(r.leftUtc).toLocaleString() : "active") + "</td><td>" +
      esc(r.leavingReason || "—") + "</td></tr>").join("") + "</table>";
}

async function loadMapTrips() {
  try { mapTripRows = await (await fetch("/api/map-trips?take=2000", {cache:"no-store"})).json(); }
  catch (e) { return; }
  fillFilter("maptripchar", "all characters", mapTripRows.map(r => [levelKey(r), r.character || r.bot]));
  fillFilter("maptripmap", "all maps", mapTripRows.map(r => [r.map, r.map]));
  renderMapTrips();
}
for (const id of ["maptripchar", "maptripmap", "maptripstatus"])
  document.getElementById(id).addEventListener("change", renderMapTrips);
document.getElementById("maptripq").addEventListener("input", renderMapTrips);
// Option values pass through the HTML parser, which replaces U+0000 with U+FFFD.
// Keep this key HTML-safe so a selected character can match the JSON rows.
function levelKey(r) { return encodeURIComponent(r.bot) + "|" + encodeURIComponent(r.character); }

function renderLevels() {
  const chosen = document.getElementById("levelchar").value;
  const rows = levelRows.filter(r => !chosen || levelKey(r) === chosen);
  const box = document.getElementById("leveltable");
  if (!rows.length) { box.innerHTML = "<div class='detail'>none recorded yet</div>"; return; }
  box.innerHTML = "<table><tr><th>When (local)</th><th>Character</th><th>Class</th>" +
    "<th>Level reached</th><th>Time since previous level-up</th><th>Source</th></tr>" +
    rows.map(r => "<tr><td>" + new Date(r.utc).toLocaleString() + "</td><td>" +
      esc(r.character || r.bot) + "</td><td>" + esc(r.class || "—") + "</td><td>" +
      r.toLevel + "</td><td>" + (r.interval ? (r.intervalApproximate ? "≈ " : "") + esc(r.interval) : "—") +
      "</td><td>" + esc(r.source) + "</td></tr>").join("") + "</table>";
}

async function loadLevels() {
  try { levelRows = await (await fetch("/api/levels?take=2000", {cache:"no-store"})).json(); }
  catch (e) { return; }
  const sel = document.getElementById("levelchar"), chosen = sel.value;
  const names = [...new Map(levelRows.map(r => [levelKey(r), r.character])).entries()]
    .sort((a,b) => a[1].localeCompare(b[1]));
  sel.innerHTML = "<option value=''>all characters</option>" +
    names.map(([key,name]) => `<option value="${esc(key)}">${esc(name)}</option>`).join("");
  sel.value = chosen;
  renderLevels();
}
document.getElementById("levelchar").addEventListener("change", renderLevels);

async function loadMemory(bots) {
  if (activeTab !== "info") return;
  const now = Date.now();
  if (now - memFetchedAt < 30000) return;
  memFetchedAt = now;

  if (document.getElementById("huntmem").open) loadHunting(bots);
  if (document.getElementById("deathmem").open) loadDeaths();
  if (document.getElementById("bossmem").open) loadBossKills();
  if (document.getElementById("questmem").open) loadQuestLog();
  if (document.getElementById("levelmem").open) loadLevels();
  if (document.getElementById("maptripmem").open) loadMapTrips();
  if (document.getElementById("upgrademem").open) loadUpgrades();
  if (document.getElementById("skillmem").open) loadSkills();
}

for (const id of ["huntmem", "deathmem", "bossmem", "questmem", "levelmem", "maptripmem", "upgrademem", "skillmem"]) {
  document.getElementById(id).addEventListener("toggle", () => { memFetchedAt = 0; });
}

// --- settings, for the whole host -------------------------------------------------------------
//
// One form, not one per bot. Settings express how the operator wants the bots to play, and four
// copies of that question on four cards was both a lot of scrolling and an invitation to let the
// bots drift apart. They are still STORED per bot, in four ini files, so they can still disagree -
// a field marked "mixed" says so rather than showing one bot's value as though it were the truth.
//
// Built once. The values refresh on a slow cadence, and a field you are editing is never
// overwritten underneath you.

let cfgFields = [];
let cfgBuilt = false;
let cfgInputs = new Map();
let cfgFetchedAt = 0;

async function setConfigAll(key, value) {
  try {
    const r = await fetch(`/api/config?key=${encodeURIComponent(key)}&value=${encodeURIComponent(value)}`,
                          { method:"POST", headers:{ "X-MirBot":"1" } });

    const body = await r.json().catch(() => ({}));

    if (!r.ok) return { error: body.error || ("rejected (" + r.status + ")") };
    return { bots: body.bots };
  } catch (e) {
    return { error: "could not reach the host" };
  }
}

function cfgSay(message, bad) {
  const box = document.getElementById("cfgresult");
  text(box, message);
  box.style.color = bad ? "var(--bad)" : "var(--good)";
}

function buildConfigForm() {
  const groups = [];

  for (const f of cfgFields) {
    let g = groups.find(x => x.name === f.group);
    if (!g) groups.push(g = { name: f.group, fields: [] });
    g.fields.push(f);
  }

  document.getElementById("cfgform").innerHTML = groups.map(g =>
    `<div class="cfggroup"><b>${esc(g.name)}</b>` +
    g.fields.map(f => `<div class="cfgrow">
        <span class="name">${esc(f.key)}</span>
        ${f.kind === 1
          ? `<select data-key="${esc(f.key)}">
               <option value="true">true</option><option value="false">false</option></select>`
          : `<input type="number" data-key="${esc(f.key)}" min="${f.min}" max="${f.max}">`}
        <span class="note">${esc(f.note)}</span>
        <span class="reach" data-mixed="${esc(f.key)}"></span>
      </div>`).join("") + `</div>`).join("");

  cfgInputs = new Map();

  for (const input of document.querySelectorAll("#cfgform [data-key]")) {
    const key = input.dataset.key;
    cfgInputs.set(key, input);

    const commit = async () => {
      if (input.value === input.dataset.served) return;

      const result = await setConfigAll(key, input.value);

      if (result.error) {
        cfgSay(`${key}: ${result.error}`, true);
        input.value = input.dataset.served;      // never leave a refused value looking accepted
      } else {
        cfgSay(`${key} = ${input.value} on ${result.bots} bot(s)`, false);
        cfgFetchedAt = 0;                        // read it back promptly
      }

      // Either way this is no longer an uncommitted edit, so it goes back to tracking the bots.
      input.classList.remove("dirty");
    };

    input.addEventListener("keydown", e => {
      if (e.key === "Enter") { e.preventDefault(); input.blur(); }
      if (e.key === "Escape") { input.value = input.dataset.served; input.blur(); }
    });

    input.addEventListener("change", commit);
    input.addEventListener("blur", commit);
    input.addEventListener("input", () =>
      input.classList.toggle("dirty", input.value !== input.dataset.served));
  }

  cfgBuilt = true;
}

function patchConfigForm() {
  if (!cfgBuilt) buildConfigForm();

  for (const f of cfgFields) {
    const input = cfgInputs.get(f.key);
    if (!input) continue;

    input.dataset.served = f.value;

    const flag = document.querySelector(`[data-mixed="${CSS.escape(f.key)}"]`);
    if (flag) {
      text(flag, f.mixed ? "mixed: " + f.perBot.join(" / ")
                         : (f.reach === 1 ? "re-applied" : ""));
      flag.style.color = f.mixed ? "var(--warn)" : "var(--dim)";
    }

    if (input.value === f.value) input.classList.remove("dirty");
    if (document.activeElement === input) continue;
    if (input.classList.contains("dirty")) continue;
    if (input.value !== f.value) input.value = f.value;
  }
}

async function loadConfig() {
  const now = Date.now();
  if (now - cfgFetchedAt < 5000) return;
  cfgFetchedAt = now;

  try { cfgFields = await (await fetch("/api/config", { cache:"no-store" })).json(); }
  catch (e) { return; }

  patchConfigForm();
}

// --- page tabs --------------------------------------------------------------------------------

let activeTab = "bots";

function showTab(name) {
  activeTab = name;

  for (const id of ["bots", "info", "settings", "notify"]) {
    document.getElementById("tab-" + id).classList.toggle("on", id === name);
    document.getElementById("page-" + id).classList.toggle("hide", id !== name);
  }

  if (name === "settings") { cfgFetchedAt = 0; loadConfig(); }
  if (name === "info") { memFetchedAt = 0; loadMemory(huntBots); }
  if (name === "notify") loadNotify(true);
}

document.getElementById("tab-bots").addEventListener("click", () => showTab("bots"));
document.getElementById("tab-info").addEventListener("click", () => showTab("info"));
document.getElementById("tab-settings").addEventListener("click", () => showTab("settings"));
document.getElementById("tab-notify").addEventListener("click", () => showTab("notify"));

// --- notifications tab ------------------------------------------------------------------------
//
// A friendlier face on the Notify* settings: the same /api/config values the Settings tab edits,
// as switches, plus a test button and what was actually sent (from /api/notify).

function buffLeft(sec) {
  if (sec === null || sec === undefined) return "";
  if (sec >= 3600) return Math.floor(sec / 3600) + "h " + Math.floor(sec % 3600 / 60) + "m left";
  if (sec >= 60) return Math.floor(sec / 60) + "m left";
  return sec + "s left";
}

const NOTIFY_TYPES = [
  { key:"NotifyLevelUp", label:"Level-ups",      example:"Wizzler reached level 37 - Wizard on Deserted Mine Lv 2" },
  { key:"NotifyUpgrade", label:"Gear upgrades",  example:"Jill equipped Platinum Ring - replaced Ring Of Discipline (score +8)" },
  { key:"NotifySkill",   label:"Skills learned", example:"Jill learned Soul Shield" },
  { key:"NotifyQuest",   label:"Quests completed", example:"Sindo completed Do your dailies 2 - Daily Buffs v2 [T], Scroll Of Boss Tracking x2" },
  { key:"NotifyFame",    label:"Fame ranks", example:"Mirbot reached fame rank Village Explorer - DC/MC/SC +4" },
  { key:"NotifyFault",   label:"Bot faults",     example:"Mirbot4 stopped - needs attention" },
  { key:"NotifyDeath",   label:"Deaths",         example:"Sindo died - killed by Stone Golem on Desert (can be noisy)" }
];

let notifyFetchedAt = 0;
let notifyBuilt = false;
let notifyIdleLast = 20;

// A change is queued to each bot and applied on its own thread, so /api/config still reports the
// old value for a moment. Hold what the user chose until the bots agree (or 10s pass), otherwise
// the next refresh flips the switch straight back.
const notifyPending = new Map();

function notifyValue(field, key) {
  const want = notifyPending.get(key);
  if (want && Date.now() < want.until && (!field || String(field.value) !== want.value)) return want.value;
  notifyPending.delete(key);
  return field ? String(field.value) : "";
}

function buildNotify() {
  const rows = NOTIFY_TYPES.map(t => `
    <div class="nrow">
      <label class="switch"><input type="checkbox" data-nkey="${t.key}"><span class="sl"></span></label>
      <div class="nl"><b>${esc(t.label)}<span class="mixed" data-nmixed="${t.key}"></span></b>
        <span>e.g. ${esc(t.example)}</span></div>
    </div>`).join("") + `
    <div class="nrow">
      <label class="switch"><input type="checkbox" id="notifyidleon"><span class="sl"></span></label>
      <div class="nl"><b>Idle bots<span class="mixed" data-nmixed="NotifyIdleMinutes"></span></b>
        <span>A bot has earned no XP for this many minutes - once per dry spell</span></div>
      <input type="number" id="notifyidlemin" min="1" max="600" step="1"> <span class="detail">min</span>
    </div>`;
  document.getElementById("notifyswitches").innerHTML = rows;

  for (const box of document.querySelectorAll("#notifyswitches input[data-nkey]"))
    box.addEventListener("change", () => saveNotify(box, box.dataset.nkey, box.checked ? "true" : "false"));

  const idleOn = document.getElementById("notifyidleon");
  const idleMin = document.getElementById("notifyidlemin");
  idleOn.addEventListener("change", () => {
    const minutes = Math.min(600, Math.max(1, parseInt(idleMin.value, 10) || notifyIdleLast));
    saveNotify(idleOn, "NotifyIdleMinutes", idleOn.checked ? String(minutes) : "0");
  });
  idleMin.addEventListener("change", () => {
    const minutes = parseInt(idleMin.value, 10);
    if (!(minutes >= 1 && minutes <= 600)) { notifyResult("idle minutes must be 1-600", true); return; }
    if (idleOn.checked) saveNotify(idleMin, "NotifyIdleMinutes", String(minutes));
    else notifyIdleLast = minutes;
  });

  document.getElementById("notifytest").addEventListener("click", sendNotifyTest);
  notifyBuilt = true;
}

function notifyResult(text, bad) {
  const el = document.getElementById("notifyresult");
  el.textContent = text;
  el.style.color = bad ? "var(--bad)" : "";
}

async function saveNotify(input, key, value) {
  input.disabled = true;
  const r = await setConfigAll(key, value);
  input.disabled = false;
  if (r.error) notifyResult(`${key}: ${r.error}`, true);
  else {
    notifyPending.set(key, { value, until: Date.now() + 10000 });
    notifyResult(`saved for ${r.bots} bot${r.bots === 1 ? "" : "s"}`);
  }
  setTimeout(() => loadNotify(true), 1500);
}

async function sendNotifyTest() {
  const button = document.getElementById("notifytest");
  button.disabled = true;
  try {
    const r = await fetch("/api/notify/test", { method:"POST", headers:{ "X-MirBot":"1" }, body:"" });
    const body = await r.json().catch(() => ({}));
    notifyResult(r.ok ? "test queued - check your phone" : (body.error || "test failed"), !r.ok);
  } catch (e) {
    notifyResult("could not reach the host", true);
  }
  button.disabled = false;
  setTimeout(() => loadNotify(true), 1500);
}

async function loadNotify(force) {
  if (!force && Date.now() - notifyFetchedAt < 5000) return;
  notifyFetchedAt = Date.now();

  let status;
  try {
    [cfgFields, status] = await Promise.all([
      fetch("/api/config", { cache:"no-store" }).then(r => r.json()),
      fetch("/api/notify", { cache:"no-store" }).then(r => r.json())
    ]);
  } catch (e) { return; }

  if (!notifyBuilt) buildNotify();

  const field = key => cfgFields.find(f => f.key === key);
  const mixedNote = f => f && f.mixed ? " mixed across bots" : "";

  for (const t of NOTIFY_TYPES) {
    const f = field(t.key), box = document.querySelector(`input[data-nkey="${t.key}"]`);
    if (f && box && !box.disabled) box.checked = notifyValue(f, t.key).toLowerCase() === "true";
    document.querySelector(`[data-nmixed="${t.key}"]`).textContent = mixedNote(f);
  }

  const idle = field("NotifyIdleMinutes");
  const minutes = parseInt(notifyValue(idle, "NotifyIdleMinutes"), 10) || 0;
  if (minutes > 0) notifyIdleLast = minutes;
  const idleOn = document.getElementById("notifyidleon"), idleMin = document.getElementById("notifyidlemin");
  if (!idleOn.disabled) idleOn.checked = minutes > 0;
  if (document.activeElement !== idleMin && !idleMin.disabled)
    idleMin.value = minutes > 0 ? minutes : notifyIdleLast;
  document.querySelector('[data-nmixed="NotifyIdleMinutes"]').textContent = mixedNote(idle);

  const state = document.getElementById("notifystate");
  state.textContent = status.enabled
    ? "- on"
    : "- not configured: add NotifyWebhookUrl under [Notify] in bot-Mirbot.ini and restart the host";
  state.style.color = status.enabled ? "var(--good)" : "var(--warn)";
  document.getElementById("notifytest").disabled = !status.enabled;

  const rows = status.recent || [];
  const box = document.getElementById("notifyrecent");
  if (!rows.length) { box.innerHTML = "<div class='detail'>nothing sent since the host started</div>"; return; }
  box.innerHTML = "<table><tr><th>When (local)</th><th>Character</th><th>Type</th><th>Title</th>" +
    "<th>Message</th><th>Outcome</th></tr>" + rows.map(n => {
      const cls = /^sent/.test(n.outcome) ? "out-sent"
                : /^(failed|dropped)/.test(n.outcome) ? "out-failed" : "out-held";
      return "<tr><td>" + new Date(n.utc).toLocaleString() + "</td><td>" + esc(n.character || n.bot) +
        "</td><td>" + esc(n.kind) + "</td><td>" + esc(n.title) + "</td><td>" + esc(n.message) +
        "</td><td class='" + cls + "'>" + esc(n.outcome) + "</td></tr>";
    }).join("") + "</table>";
}

// --- items ------------------------------------------------------------------------------------
//
// A grid of tiles rather than three columns of text. The tiles are coloured by item type and the
// detail lives in a tooltip, which is how the game itself presents a bag - and it makes "what is
// this bot carrying" a glance instead of a read.
//
// Deliberately built to work with NO artwork. The client's sprites are in a container format
// nothing in this project can decode yet, so every cell carries its image index and renders a
// coloured tile with an abbreviated name. If icons ever appear under /icons/, the img swaps in and
// nothing else changes.

const TYPE_CLASS = {
  Weapon:"t-weapon", Armour:"t-armour", Helmet:"t-armour", Shoes:"t-armour", Belt:"t-armour",
  Necklace:"t-jewel", Bracelet:"t-jewel", Ring:"t-jewel", Torch:"t-scroll",
  Consumable:"t-potion", Book:"t-book", Ore:"t-ore", Meat:"t-ore", Nothing:"t-ore",
  Amulet:"t-reagent", Poison:"t-reagent", ItemPart:"t-part", Gem:"t-jewel", Emblem:"t-jewel",
  Shield:"t-armour"
};

// Every item on show, by cell id, so the tooltip can find one without another lookup.
//
// Keys are STABLE - pane plus position, nothing else. They used to carry a sequence number that
// changed on every render, which made the generated HTML differ every tick even when the bag had
// not moved. The signature never matched, the grid was rewritten once a second, and an <img> was
// destroyed and recreated before it could ever finish loading. Only one detail card exists at a
// time, so pane+position is unique, and a stale entry is simply overwritten.
const itemIndex = new Map();

function shortName(name) {
  // Two or three initials read better at 38px than a truncated word.
  const words = String(name).replace(/[()]/g, "").split(/\s+/).filter(Boolean);
  if (words.length === 1) return words[0].slice(0, 4);
  return words.slice(0, 3).map(w => w[0]).join("").toUpperCase();
}

function cellHtml(item, key) {
  if (!item) return `<div class="cell"></div>`;

  itemIndex.set(key, item);

  const cls = TYPE_CLASS[item.type] || "t-ore";
  const wear = item.maxDurability > 0 && item.durability === 0 ? " broken"
             : item.maxDurability > 0 && item.durability * 100 / item.maxDurability <= 30 ? " worn"
             : "";

  // The artwork if it is there, the initials if it is not.
  //
  // Both are rendered: the label sits underneath and the image covers it, so an icon that 404s
  // simply removes itself and the tile is already correct. No probing, no flicker, and a client
  // whose icons were never extracted looks exactly as it did before they existed.
  return `<div class="cell full ${cls}${wear}" data-item="${key}">` +
         `<span class="lbl">${esc(shortName(item.name))}</span>` +
         `<img src="/icons/${item.image}.png" alt="" ` +
              `onload="this.parentNode.classList.add('art')" ` +
              `onerror="this.remove()">` +
         (item.count > 1 ? `<span class="n">${item.count}</span>` : "") +
         `</div>`;
}

function renderCells(card, sigKey, node, items, size) {
  const list = new Array(size).fill(null);

  for (const it of items) {
    if (it.slot >= 0 && it.slot < size) list[it.slot] = it;
  }

  // Storage and the parts grid use offset slot numbers, so anything that did not land goes on
  // the end rather than vanishing.
  for (const it of items) if (!list.includes(it)) list.push(it);

  const html = list.map((it, i) => cellHtml(it, `${sigKey}-${i}`)).join("");

  if (card.sig[sigKey] === html) return;
  card.sig[sigKey] = html;
  node.innerHTML = html;
}

// The order the game's own character screen uses, read off it rather than off the enum - the
// enum is declaration order (Weapon, Armour, Helmet, Torch, ...) and puts the weapon above the
// helmet, which reads as nothing in particular.
//
// Laid out as two columns flowing DOWN, the way the dialog flanks the character: body and hands on
// the left, jewellery on the right.
const EQUIP_ORDER = [
  "Helmet", "Necklace",
  "Armour", "BraceletL",
  "Weapon", "BraceletR",
  "Shoes",  "RingL",
  "Torch",  "RingR",
  "Amulet", "Poison",
  "Shield", "Emblem"
];

function renderSlots(card, node, equipment) {
  const worn = new Map(equipment.map(e => [e.slot, e]));

  // Every slot in the list, occupied or not. An empty slot is information - "no shield", "no
  // amulet" - and hiding it made the panel change shape whenever a bot equipped something.
  const order = EQUIP_ORDER.slice();

  // Anything worn that the list does not mention still has to appear; better an odd position than
  // a silently missing item.
  for (const e of equipment) if (!order.includes(e.slot)) order.push(e.slot);

  const html = order.map((slot, i) => {
    const e = worn.get(slot);
    const key = `eq-${i}`;

    if (!e)
      return `<div class="slot empty"><div class="cell"></div>` +
             `<div class="who"><div class="s">${esc(slot)}</div>` +
             `<div class="i">&mdash;</div></div></div>`;

    return `<div class="slot">` +
      (e.item ? cellHtml(e.item, key) : `<div class="cell"></div>`) +
      `<div class="who"><div class="s">${esc(slot)}</div>` +
      `<div class="i${e.broken ? " broken" : e.worn ? " worn" : ""}">${esc(e.name)}` +
      ` ${e.durability}/${e.maxDurability}</div></div></div>`;
  }).join("");

  if (card.sig.equip === html) return;
  card.sig.equip = html;
  node.innerHTML = html;
}

// --- the tooltip ------------------------------------------------------------------------------

const tip = document.getElementById("tip");

function statRows(list, extra) {
  return list.map(st =>
    `<div class="st ${extra || ""}"><b>${esc(st.name)}</b><span>` +
    `${st.amount > 0 ? "+" : ""}${st.amount}</span></div>`).join("");
}

function showTip(item, x, y) {
  const parts = [`<div class="tn">${esc(item.name)}` +
                 (item.count > 1 ? ` <span class="n">x${item.count}</span>` : "") + `</div>`,
                 `<div class="tt">${esc(item.type)}` +
                 (item.slot >= 0 ? ` · slot ${item.slot}` : "") + `</div>`];

  if (item.maxDurability > 0)
    parts.push(`<div class="st"><b>Durability</b><span>${item.durability}/${item.maxDurability}` +
               `</span></div>`);

  if (item.stats?.length) parts.push(statRows(item.stats));
  if (item.added?.length) parts.push(`<hr>` + statRows(item.added, "add"));
  if (item.sockets?.length)
    parts.push(`<div class="st"><b>Sockets</b><span>${esc(item.sockets.join(", "))}</span></div>`);

  parts.push(`<hr><div class="st"><b>Weight</b><span>${item.weight}</span></div>`);
  // What a vendor pays now (the number the game shows), then the database list price.
  if (item.salePrice > 0)
    parts.push(`<div class="st"><b>Sells for</b><span>${group(item.salePrice)}` +
      (item.count > 1 ? ` each, ${group(item.salePrice * item.count)} for ${item.count}` : "") + `</span></div>`);
  if (item.price > 0)
    parts.push(`<div class="st"><b>List price</b><span>${group(item.price)}</span></div>`);
  if (item.flags) parts.push(`<div class="st"><b>Flags</b><span>${esc(item.flags)}</span></div>`);
  if (!item.canSell) parts.push(`<div class="req">cannot be sold</div>`);
  if (item.requirement) parts.push(`<div class="req">needs ${esc(item.requirement)}</div>`);
  if (item.description) parts.push(`<div class="desc">${esc(item.description)}</div>`);

  tip.innerHTML = parts.join("");
  tip.style.display = "block";

  // Keep it on screen: flip to the other side of the cursor near an edge rather than letting the
  // box run off and clip.
  const box = tip.getBoundingClientRect();
  const left = x + 14 + box.width > window.innerWidth ? x - box.width - 14 : x + 14;
  const top = y + 12 + box.height > window.innerHeight ? y - box.height - 12 : y + 12;

  tip.style.left = Math.max(4, left) + "px";
  tip.style.top = Math.max(4, top) + "px";
}

// One listener on the document rather than one per cell: cells are rewritten whenever a bag
// changes, and per-cell listeners would leak with them.
document.addEventListener("mouseover", e => {
  const cell = e.target.closest?.("[data-item]");
  if (!cell) return;

  const item = itemIndex.get(cell.dataset.item);
  if (item) showTip(item, e.clientX, e.clientY);
});

document.addEventListener("mousemove", e => {
  if (tip.style.display !== "block") return;
  const cell = e.target.closest?.("[data-item]");
  if (!cell) { tip.style.display = "none"; return; }

  const item = itemIndex.get(cell.dataset.item);
  if (item) showTip(item, e.clientX, e.clientY);
});

document.addEventListener("mouseout", e => {
  if (e.target.closest?.("[data-item]")) tip.style.display = "none";
});

// --- render -----------------------------------------------------------------------------------
// --- render -----------------------------------------------------------------------------------

// Loops and deadlocks are what this codebase produces; crashes are rare. These are the three
// shapes that have actually bitten, phrased so the banner says what to go and look at.
function alertsFor(h) {
  const out = [];

  for (const b of h.bots) {
    if (b.state !== "Playing") continue;

    const idle = idleSeconds(b);

    if (idle > STALE_SECONDS)
      out.push([`${b.characterName || b.id} has earned nothing for ${ago(idle)} ` +
                `(${b.activity})`, false]);

    const trips = b.uptimeSeconds > 600 ? b.tripSequence / (b.uptimeSeconds / 3600) : 0;
    if (trips >= 8)
      out.push([`${b.characterName || b.id} has made ${b.tripSequence} town trips in ` +
                `${Math.round(b.uptimeSeconds / 60)} min — check "Why not?"`, true]);

    if (b.sellRefusals > 0)
      out.push([`${b.characterName || b.id}: ${b.sellRefusals} sell order(s) refused whole — ` +
                `the bag listing may not match what it is really carrying`, false]);
  }

  return out;
}

let alertSig = "";

function renderAlerts(h) {
  const list = alertsFor(h);
  const sig = JSON.stringify(list);
  if (sig === alertSig) return;
  alertSig = sig;

  document.getElementById("alerts").innerHTML = list
    .map(([msg, warn]) => `<div class="alert${warn ? " warn" : ""}">${esc(msg)}</div>`)
    .join("");
}

function render(h) {
  renderAlerts(h);
  text(document.getElementById("host"),
    `${h.server} · db ${h.databaseVersion} · ${h.bots.length} bot(s)`);
  text(document.getElementById("foot"),
    `updated ${new Date(h.generatedAt).toLocaleTimeString()} · host up ${h.uptimeSeconds}s ` +
    `· ${h.magicCount} skills, ${h.monsterCount} monsters, ${h.vendorPages} trading pages`);

  const seen = new Set();

  for (const b of h.bots) {
    seen.add(b.id);

    let mini = minis.get(b.id);

    if (!mini) {
      mini = makeMini(b.id);
      minis.set(b.id, mini);
      stripEl.appendChild(mini.root);
    }

    patchMini(mini, b);
  }

  // Nothing chosen yet, or the chosen bot has gone away: fall back to the first.
  if (!selected || !seen.has(selected)) {
    selected = null;
    if (h.bots.length) select(h.bots[0].id);
  }

  const chosen = h.bots.find(b => b.id === selected);

  if (chosen) {
    let card = cards.get(chosen.id);

    if (!card) {
      card = makeCard(chosen.id);
      cards.set(chosen.id, card);
    }

    // Built lazily and only attached while selected, so the detail card for a bot you are not
    // looking at is neither drawn nor patched.
    const detail = document.getElementById("botdetail");
    if (card.root.parentNode !== detail) {
      while (detail.firstChild) detail.firstChild.remove();
      detail.appendChild(card.root);
    }

    for (const [botId, mini] of minis) mini.root.classList.toggle("on", botId === chosen.id);

    patch(card, chosen);
  }

  loadGold(selected ? [selected] : []);
  loadMemory(h.bots);
  if (activeTab === "settings") loadConfig();
  if (activeTab === "notify") loadNotify(false);

  for (const [id, mini] of minis) {
    if (seen.has(id)) continue;
    mini.root.remove();
    minis.delete(id);
    cards.get(id)?.root.remove();
    cards.delete(id);
  }
}

async function refresh() {
  // No focus guard any more. Cards are patched rather than rebuilt, so a focused select, a
  // half-typed input and a hovered row all survive a poll - which is the whole point of 0c.
  try {
    const r = await fetch("/api/status", { cache:"no-store" });
    render(await r.json());
  } catch (e) {
    text(document.getElementById("foot"), "host unreachable — " + e);
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
