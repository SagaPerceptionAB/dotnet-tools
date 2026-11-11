// Systemkartan: ett nätverk ritat med Cytoscape.js och layouten fcose. Projekten är mjuka områden med klasserna i,
// integrationerna är ikonnoder utanför systemet. Färgerna är VS Codes (ett ljust och ett mörkt tema, se THEMES): solutionen lila, projekt grönt,
// klass orange, funktion brunt (gult i det mörka), integration blått och anropen in grått; pilar mellan kod är grå, pilar till en integration blå.
// Typen står alltid i text och som ikon, aldrig bara som färg.
//
// Används av den fristående sidan (page.html) och av sidor som bäddar in kartan. En karta per sida:
//   var map = SystemMap.create(root, { mermaid: function (level, direction, entry) { return Promise<text>; }, ... });
//   map.show(json);   // kartan som JSON (CodeMapJson)
// Inställningar (alla valfria):
//   mermaid(level, direction, entry, hidden, unreached): texten till "Exportera som Mermaid" i verktygsraden. Utan den syns inte knappen. Ett fel
//     ges som { message, tip }. Knappen vägrar själv när ett filter är valt eller flera anrop är kryssade, och när något är dolt eller delar som
//     inte nås är avbockade, om inte sidan tar hand om det (mermaidAccepts). hidden är id:n på det som är dolt, unreached om delar som inte nås visas.
//   mermaidAccepts: { hidden: true, unreached: true }: sidans fil följer det som är dolt (hidden) eller det som inte nås (unreached); knappen vägrar då inte för det.
//   onError(error): när Mermaid-filen inte gick att hämta. Utan den står felet bredvid knappen.
//   hidden: id:n på delar som är dolda när kartan öppnas första gången (det sparade gäller före).
//   unreached: false = "Visa delar som inte nås" är avbockad när kartan öppnas första gången (annars ibockad; det man själv väljer sparas och gäller före).
//   theme: 'light' eller 'dark' låser temat, 'auto' låter det följa datorn; båda döljer temaknappen, för sidor med eget tema. Utan den visas knappen
//     "Tema" i raden (en knapp som byter mellan ljust och mörkt); från början följer kartan datorn, och valet sparas som riktningen.
// Kartan fyller ytan sidan ger den: sidan sätter höjden med --map-height (annars hela fönstret). Anrop, projekt, klass, funktion och
// integration väljs i sökbara listor i raden ovanför kartan, där också riktningen och exporten finns. Ett val av projekt, klass, funktion
// eller integration är ett filter: kartan visar bara anropen som går genom det. Detaljnivån väljs i samma listor ("Visas inte", "Alla",
// "Alla, med läsningar och skrivningar"). "Rensa" tar bort filtren. Ett anrop går också att välja genom att klicka på dess nod.
// Rutan nere till vänster i kartan har valet om det som ingen ingång når (delar med reached: false, ritade dämpat och streckat) syns, och
// "Visa dem" när något är dolt med hidden. SystemMap.hide(overview, ids) är regeln för det som döljs; SystemMap.dropUnreached(overview) tar bort det som inte nås.
// SystemMap.summary(json) ger "4 anrop, 3 delar av systemet och 6 integrationer.".
(function () {
  'use strict';

  // Integrationernas typer: [namn, kort namn under ikonen (där ikonen redan visar att det är ett annat system)].
  var KINDS = { database: ['Databas', 'Databas'], queue: ['Kö', 'Kö'], http: ['Annat system (HTTP)', 'HTTP'], wcf: ['Annat system (WCF/SOAP)', 'WCF/SOAP'],
    storage: ['Lagring', 'Lagring'], email: ['E-post', 'E-post'] };
  // Färgerna i varje tema. node: delarnas färger (kant, ikon, text i rubriker), arrows: pilarna mellan kod; fill: fyllningar; fg och bg: text i
  // noder och kartans bakgrund (samma som sidans, systemmap.css); group: hur tydligt en utfälld yta tonas av sin färg.
  var THEMES = {
    light: { node: { solution: '#652D90', project: '#388A34', 'class': '#D67E00', method: '#795E26', integration: '#0063D3', entry: '#767676', arrows: '#A0A0A0' },
      fg: '#3B3B3B', bg: '#FFFFFF', fill: { node: '#FFFFFF', entry: '#F8F8F8', round: '#EBF3FC', box: '#EEF5EE', boxLine: '#A6CBA4', group: 0.06 } },
    dark: { node: { solution: '#B180D7', project: '#89D185', 'class': '#EE9D28', method: '#DCDCAA', integration: '#59A4F9', entry: '#9D9D9D', arrows: '#6E6E6E' },
      fg: '#CCCCCC', bg: '#1F1F1F', fill: { node: '#1F1F1F', entry: '#262626', round: '#1A2838', box: '#222D22', boxLine: '#4C6E4A', group: 0.09 } }
  };
  var FONT = 'system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';
  // Temaknappen: en knapp med bara ikon som visar temat man byter TILL (månen i ljust tema, solen i mörkt); namnet står i title och aria-label.
  var THEME_ICONS = {
    light: '<circle cx="9" cy="9" r="3.4"/><path d="M9 1.8v1.6M9 14.6v1.6M1.8 9h1.6M14.6 9h1.6M3.9 3.9l1.1 1.1M13 13l1.1 1.1M3.9 14.1L5 13M13 5l1.1-1.1"/>',
    dark: '<path d="M14.8 11.2A6.3 6.3 0 0 1 6.8 3.2a6.3 6.3 0 1 0 8 8z"/>'
  };
  var MARKUP =
    '<div class="map-wrap" id="map-wrap">' +
    '  <div class="map-bar" id="map-bar">' +
    '    <span id="map-filters" class="map-filters"></span>' +
    '    <button type="button" id="map-clear" aria-disabled="true" title="Tar bort valen av anrop, projekt, klass, funktion och integration">✕ Rensa</button>' +
    '    <span class="map-field map-field-direction"><span class="map-label" aria-hidden="true">Riktning</span>' +
    '      <span class="segmented" role="group" aria-label="Riktning"><button type="button" data-direction="LR" title="Anropen går från vänster till höger">→</button><button type="button" data-direction="TB" title="Anropen går uppifrån och ned">↓</button></span></span>' +
    '    <span class="map-field map-field-theme" id="map-theme-field"><span class="map-label" aria-hidden="true">Tema</span>' +
    '      <button type="button" id="map-theme"><svg width="20" height="20" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"></svg></button></span>' +
    '    <span id="map-export"><button type="button" id="map-mermaid" aria-label="Exportera som Mermaid" title="Exportera som Mermaid. Ett flödesschema att lägga i dokumentation. Filen följer riktningen, nivån och anropet du valt."><svg width="20" height="20" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9 2.5 V11 M5.5 7.5 L9 11 L12.5 7.5"/><path d="M2.5 11.5 V15 H15.5 V11.5"/></svg></button>' +
    '      <span class="map-sr" id="map-mermaid-status"></span></span>' +
    '    <details class="map-extra" id="map-diff" name="map-extra" hidden><summary id="map-diff-summary" title="Anrop som bara finns i koden eller bara i API-beskrivningen"></summary><ul id="map-diff-list"></ul></details>' +
    '    <details class="map-extra" id="map-warnings" name="map-extra" hidden><summary id="map-warnings-summary"></summary><ul id="map-warnings-list"></ul></details>' +
    '  </div>' +
    '  <div class="map-view" id="map-view" tabindex="0" role="group" aria-label="Systemkarta. Tangenterna + och − zoomar, 0 passar in hela kartan."></div>' +
    '  <div class="map-status" id="map-status" hidden></div>' +
    '  <div class="map-legend" id="map-legend" role="group" aria-label="Förklaring" hidden>' +
    '    <label class="map-unreached" id="map-unreached-field" hidden><input type="checkbox" id="map-unreached"><span class="map-swatch" aria-hidden="true"></span><span>Visa delar som inte nås</span></label>' +
    '    <span class="map-hidden-row" id="map-hidden-row" hidden><span class="map-hint" id="map-hint"></span><button type="button" id="map-show-all">Visa dem</button></span>' +
    '  </div>' +
    '  <div class="map-zoom-hint" id="map-zoom-hint" aria-hidden="true"></div>' +
    '  <div class="map-zoom" id="map-zoom" role="group" aria-label="Zoom">' +
    '    <button type="button" id="map-zoom-in" aria-label="Zooma in (+)" title="Zooma in (+)">+</button>' +
    '    <button type="button" id="map-zoom-out" aria-label="Zooma ut (−)" title="Zooma ut (−)">−</button>' +
    '    <button type="button" id="map-zoom-fit" aria-label="Passa in hela kartan (0)" title="Passa in hela kartan (0)"><svg width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><path d="M2.5 6.5V2.5H6.5M11.5 2.5H15.5V6.5M15.5 11.5V15.5H11.5M6.5 15.5H2.5V11.5"/></svg></button>' +
    '    <div class="map-zoom-pct" id="map-zoom-pct" aria-live="off">100 %</div>' +
    '  </div>' +
    '  <div class="map-card" id="map-card" hidden></div>' +
    '</div>' +
    '<div class="map-sr" id="map-live" role="status" aria-live="polite"></div>';

  // open: delar användaren fällt ut (också anropsgrupper, 'cg:' och etiketten). shut: delar användaren fällt ihop (open och shut gäller före nivån och vägen).
  // callBox, callKids: anrop → anropsgruppen det ligger i, och hopfälld anropsgrupp → anropen i den (för var nya noder börjar). card: noden i detaljkortet.
  // dir: LR (vänster → höger) eller TB (uppifrån och ned). level: projects (från början), classes, methods eller all (vad som är utfällt); den enda
  // sanningen om detaljnivån, och nivåvalen i listorna Klass, Funktion och Integration visar den.
  // hidden: id → true för delarna som är dolda (hidden, sparat). unreached: om delar som ingen ingång når syns. pre: översikten utan det
  // dolda (hide) och, om unreached är falskt, utan det som inte nås (dropUnreached). view: pre med filtret (filterView), som allt ritas ur.
  // f: valen i listorna Projekt, Klass, Funktion och Integration: project, cls, fn och integ är listor med id (kryssade).
  // picked: de kryssade anropen (entry: det enda anropet när bara ett är kryssat, annars null; pickedKeys: deras namn → true).
  // calls: anropen som går genom filtret (namn → true), null utan filter. visibleEntries: anropen som visas. combos: de fem listorna.
  // out/into: pilarna från och till varje nod. visible: visibleOf för det som ritas just nu. zoomGoal: dit en pågående zoom är på väg.
  var state = { map: null, allNodes: {}, anyUnreached: false, pre: null, view: null, f: emptyFilter(), picked: [], pickedKeys: {}, calls: null, visibleEntries: [], combos: {}, hidden: {}, unreached: true, groups: [], entry: null, cy: null, nodes: {}, kids: {}, out: {}, into: {}, visible: {}, open: {}, shut: {}, callBox: {}, callKids: {},
    dir: stored('map-direction', ['LR', 'TB'], 'LR'), theme: 'auto', mode: 'light', locked: false, mq: null, level: stored('map-level', ['projects', 'classes', 'methods', 'all'], 'projects'),
    layout: null, steps: {}, base: {}, last: 1, card: null, hovered: null, pulses: [], root: null, opts: {}, unfitted: false, zoomGoal: null, zoomUi: null };

  var $ = function (id) { return document.getElementById(id); };
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function fillList(id, lines) {
    var ul = $(id); ul.textContent = '';
    (lines || []).forEach(function (t) { ul.appendChild(el('li', null, t)); });
  }
  // Läses upp av skärmläsare. Rutan töms först, så att samma text läses upp igen.
  function announce(text) { var live = $('map-live'); live.textContent = ''; setTimeout(function () { live.textContent = text; }, 30); }
  function stored(key, allowed, fallback) {
    try { var v = sessionStorage.getItem(key); return allowed.indexOf(v) >= 0 ? v : fallback; } catch (e) { return fallback; }
  }
  // En sparad lista (JSON), eller null om ingen finns. En tom lista är ett val ("visa allt") och skiljer sig från ingen.
  function storedList(key) {
    try { var v = JSON.parse(sessionStorage.getItem(key)); return Array.isArray(v) ? v : null; } catch (e) { return null; }
  }
  function remember(key, value) { try { sessionStorage.setItem(key, value); } catch (e) { /* privat läge eller en fil utan lagring */ } }
  function motion() { return !window.matchMedia('(prefers-reduced-motion: reduce)').matches; }
  function kindName(kind, short) { return KINDS[kind] ? KINDS[kind][short ? 1 : 0] : 'Integration'; }
  function pathKey(p) { return String(p || '').toLowerCase().replace(/\/+$/, '').replace(/\{[^}]*\}/g, '{}'); }
  function shapeText(s) { return s ? s.type + (s.fields && s.fields.length ? ' {' + s.fields.join(', ') + '}' : '') : ''; }
  function integrationOf(id) {
    return (state.map && state.map.integrations.find(function (i) { return i.id === id; })) || { id: id, kind: '', name: id, detail: '' };
  }
  // Föräldrakedjan: går uppåt från noden (den själv först) tills visit ger sant, och ger den noden, annars null. lookup: id → nod
  // (state.nodes för det som syns, state.allNodes för hela kartan).
  function climb(n, lookup, visit) {
    for (; n; n = n.parent ? lookup[n.parent] : null) if (visit(n)) return n;
    return null;
  }
  function projectNodeOf(n, lookup) { return climb(n, lookup, function (x) { return x.kind === 'project'; }); }
  // En mängd (id → true) av id:n.
  function idSet(ids) { var set = {}; ids.forEach(function (id) { set[id] = true; }); return set; }
  // Är delen (i hela kartan) en av delarna i want, eller ligger den under någon av dem?
  function within(id, want) { return !!climb(state.allNodes[id], state.allNodes, function (n) { return want[n.id]; }); }
  // Kryssar eller avkryssar id i listan. Ger true när det blev kryssat.
  function ticks(list, id) { var i = list.indexOf(id); if (i >= 0) list.splice(i, 1); else list.push(id); return i < 0; }
  // "ShopDatabase (databas, EF Core och SQL)", som i terminalen.
  function integrationLabel(it) { return it.name + (it.detail ? ' (' + it.detail + ')' : ''); }
  // Operationen som den visas: verbet, och för ett HTTP-anrop vars adress inte går att läsa ur koden (address: 'runtime') en förklaring.
  // Samma text som CodeMapOverview.RuntimeAddress i C#.
  function operationText(operation, address) { return address === 'runtime' ? operation + ' (adressen bestäms vid körning)' : operation; }
  // Ingången på kartan för ett anrop (t.ex. i ett test): samma operation i API-beskrivningen, annars samma metod och sökväg. Finns anropet i flera
  // projekt (samma rutt i två webbappar) väljs det i projektet som testas, om det anges; annars det första.
  function entryFor(op, method, path, project) {
    if (!state.map) return null;
    var list = state.map.entryPoints;
    function pick(found) { return (project && found.find(function (e) { return e.project === project; })) || found[0] || null; }
    return (op && pick(list.filter(function (e) { return e.operationId === op; }))) ||
      pick(list.filter(function (e) { return e.method === method && (pathKey(e.path) === pathKey(path) || pathKey(e.codePath) === pathKey(path)); }));
  }
  // Data in och ut för ett steg: "tar X, ger Y" för kod, "skickar X, får Y" för en integration.
  function dataText(s, inVerb, outVerb) {
    var parts = [];
    if (s['in']) parts.push(inVerb + ' ' + s['in'].type);
    if (s.out) parts.push(outVerb + ' ' + s.out.type);
    return parts.join(', ');
  }
  // "4 anrop, 3 delar av systemet och 6 integrationer."
  function summary(map) {
    var p = map.projects.length, i = map.integrations.length;
    return map.entryPoints.length + ' anrop, ' + p + (p === 1 ? ' del av systemet och ' : ' delar av systemet och ') + i + (i === 1 ? ' integration.' : ' integrationer.');
  }

  // Anropen i grupper efter sökvägsområde: den gemensamma början för alla HTTP-anrop (t.ex. /v1) tas bort och nästa segment blir
  // gruppen (/v1/auth, /v1/family ...). Ger [{ label, entries }]: grupperna där deras första anrop står, och anropen i en grupp
  // i samma ordning som i kartan (sökväg, sedan metod). Ett ensamt anrop är ingen grupp (label är null). Efter HTTP-anropen kommer det
  // som inte är HTTP, en grupp per sort med sort som etikett (kind: true), även om det bara är ett: Bakgrundstjänster, Konsolprogram,
  // Meddelanden, Jobb, gRPC och SignalR. En sådan grupp med ett anrop ritas utan ruta (single: true).
  var KIND_GROUPS = [['background', 'Bakgrundstjänster'], ['console', 'Konsolprogram'], ['consumer', 'Meddelanden'], ['job', 'Jobb'], ['grpc', 'gRPC'], ['signalr', 'SignalR']];
  function callGroups(entries) {
    var http = entries.filter(function (e) { return !!e.method; });
    var segs = http.map(function (e) { return String(e.path || '').split('/').filter(Boolean); });
    var common = 0;
    while (segs.length && segs.every(function (x) { return x.length > common && x[common] === segs[0][common]; })) common++;
    var list = [], byKey = Object.create(null);
    http.forEach(function (e, i) {
      var x = segs[i];
      if (x.length <= common) { list.push({ label: null, entries: [e] }); return; }
      var g = byKey[x[common]];
      if (!g) { g = byKey[x[common]] = { label: '/' + x.slice(0, common + 1).join('/'), entries: [] }; list.push(g); }
      g.entries.push(e);
    });
    list = list.map(function (g) { return g.entries.length > 1 ? g : { label: null, entries: g.entries }; });
    KIND_GROUPS.forEach(function (k) {
      var own = entries.filter(function (e) { return !e.method && e.kind === k[0]; });
      if (own.length) list.push({ label: k[1], entries: own, kind: true, single: own.length < 2 });
    });
    return list;
  }

  // Bygger kartans delar i roten och kopplar knapparna. Kartan ritas först med show().
  function create(root, opts) {
    state.root = root; state.opts = opts || {};
    root.classList.add('systemmap');   // systemmap.css gäller det som står i roten
    root.innerHTML = MARKUP;
    initTheme(root);
    $('map-export').hidden = !state.opts.mermaid;
    $('map-mermaid').addEventListener('click', downloadMermaid);
    $('map-show-all').addEventListener('click', function () { if (hiddenIds().length) showHidden(); });
    $('map-unreached').addEventListener('change', function () { setUnreached(this.checked); });
    $('map-clear').addEventListener('click', function () {
      if (this.getAttribute('aria-disabled') === 'true') return;
      changeFilter(function () { state.f = emptyFilter(); state.level = 'projects'; setPicked([]); state.shut = {}; }, 'Filtren är borttagna.');
      $('map-clear').blur();
    });
    initZoom(root);
    // Esc stänger detaljkortet, och skillnader och varningar som fällts ut över kartan (fokus går då tillbaka till rubriken).
    // En öppen lista stänger sig själv och stoppar Esc, så att inget annat stängs samtidigt.
    document.addEventListener('keydown', function (ev) {
      if (ev.key !== 'Escape') return;
      hideCard();
      var open = root.querySelector('.map-extra[open]');
      if (open) { open.open = false; open.querySelector('summary').focus(); }
    });
    // De fem listorna, med flerval. Anrop markerar vägarna i kartan; de andra är filter (changeFilter). Nivåvalen överst i Klass, Funktion och
    // Integration är radioknappar: de byter state.level (se Filtret). Ett kryss i en lista och Rensa är från början ett kryss och Rensa i filtret.
    var filters = $('map-filters');
    function add(o) {
      o.id = 'map-' + o.key;
      o.empty = o.empty || 'Alla';
      o.name = o.name || nameInMap;
      o.onToggle = o.onToggle || function (id) { changeFilter(function (f) { ticks(f[o.key], id); }); };
      o.onClear = o.onClear || function () { changeFilter(function (f) { f[o.key] = []; }); };
      filters.appendChild((state.combos[o.key] = combo(o)).field);
    }
    add({ key: 'entry', label: 'Anrop', plural: 'anrop', empty: 'Hela systemet', note: 'Inget kryss = hela systemet', name: function (key) { return key; },
      onToggle: function (key) { var l = state.picked.slice(); ticks(l, entryByKey(key)); choosePicked(l); },
      onClear: function () { choosePicked([]); } });
    add({ key: 'project', label: 'Projekt', plural: 'projekt',
      onToggle: function (id) {
        changeFilter(function (f) {
          // Avkryssas ett projekt tas kryssen under det bort.
          if (ticks(f.project, id)) return;
          var gone = idSet([id]), outside = function (x) { return !within(x, gone); };
          f.cls = f.cls.filter(outside); f.fn = f.fn.filter(outside);
        });
      } });
    add({ key: 'cls', label: 'Klass', plural: 'klasser', levels: [{ value: 'all', text: 'Alla' }, { value: 'none', text: 'Visas inte' }],
      onToggle: function (id) {
        changeFilter(function (f) {
          if (ticks(f.cls, id)) { if (state.level === 'projects') state.level = 'classes'; }
          else f.fn = f.fn.filter(function (m) { return state.allNodes[m].parent !== id; });
        });
      },
      onLevel: function (v) {
        changeFilter(function (f) {
          if (v === 'none') { f.cls = []; f.fn = []; state.level = 'projects'; }
          else if (state.level === 'projects') state.level = 'classes';
        });
      } });
    add({ key: 'fn', label: 'Funktion', plural: 'funktioner', levels: [{ value: 'none', text: 'Visas inte' }, { value: 'all', text: 'Alla' }],
      onLevel: function (v) { changeFilter(function () { state.level = v === 'none' ? 'classes' : state.level === 'all' ? 'all' : 'methods'; }); } });
    add({ key: 'integ', label: 'Integration', plural: 'integrationer', levels: [{ value: 'all', text: 'Alla' }, { value: 'reads', text: 'Alla, med läsningar och skrivningar' }],
      onLevel: function (v) { changeFilter(function () { if (v === 'reads') state.level = 'all'; else if (state.level === 'all') state.level = 'methods'; }); } });
    // Riktningen. Den lägger ut kartan på nytt och sparas till nästa gång.
    Array.prototype.forEach.call(root.querySelectorAll('[data-direction]'), function (b) {
      b.addEventListener('click', function () {
        state.dir = b.getAttribute('data-direction'); remember('map-direction', state.dir);
        syncDirection(); hideCard();
        render(function () { fitView(); }, true);
      });
    });
    syncDirection();
    return {
      show: renderMap,
      // Välj ett anrop (en ingång ur kartan, eller null för hela systemet). { focus: true } flyttar fokus till anropslistan.
      select: function (e, opts) {
        selectEntry(e);
        if (opts && opts.focus) state.combos.entry.input.focus({ preventScroll: true });
      },
      entryFor: entryFor,
      // Stegen för ett anrop som en lista (för sidor som visar vägen som text).
      steps: function (e) { return stepsList(e.steps); },
      loaded: function () { return !!state.map; }
    };
  }
  // Temat: 'light', 'dark' eller 'auto' (följ datorn). Inställningen theme låser det och döljer knappen; annars gäller det sparade valet
  // och från början 'auto'. mode är det som ritas (light eller dark) och står som data-theme på roten, där systemmap.css byter färgvariablerna.
  var THEME_SAID = { light: 'Ljust tema.', dark: 'Mörkt tema.' };
  function systemMode() { return state.mq && state.mq.matches ? 'dark' : 'light'; }
  function initTheme(root) {
    var forced = ['light', 'dark', 'auto'].indexOf(state.opts.theme) >= 0 ? state.opts.theme : null;
    state.locked = !!forced;
    state.theme = forced || stored('map-theme', ['light', 'dark'], 'auto');   // inget sparat val = följ datorn
    state.mq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    $('map-theme-field').hidden = state.locked;
    // Ett klick byter till det andra temat och sparar det; blir det datorns tema tas valet bort, så att sidan följer datorn igen.
    $('map-theme').addEventListener('click', function () {
      var to = state.mode === 'dark' ? 'light' : 'dark';
      if (to === systemMode()) { state.theme = 'auto'; try { sessionStorage.removeItem('map-theme'); } catch (e) { /* ingen lagring */ } }
      else { state.theme = to; remember('map-theme', to); }
      applyTheme(); announce(THEME_SAID[to]);
    });
    // Följer datorn gör kartan det medan sidan är öppen.
    var follow = function () { if (state.theme === 'auto') applyTheme(); };
    if (state.mq) { if (state.mq.addEventListener) state.mq.addEventListener('change', follow); else if (state.mq.addListener) state.mq.addListener(follow); }
    applyTheme();
  }
  // Sätter temat på roten och knappen, och ritar om färgerna (inte utläggningen: noderna står kvar).
  function applyTheme() {
    state.mode = state.theme === 'auto' ? systemMode() : state.theme;
    state.root.setAttribute('data-theme', state.mode);
    var to = state.mode === 'dark' ? 'light' : 'dark', button = $('map-theme'), name = 'Byt till ' + (to === 'dark' ? 'mörkt' : 'ljust') + ' tema';
    button.title = name; button.setAttribute('aria-label', name);
    button.firstChild.innerHTML = THEME_ICONS[to];
    if (state.cy) recolor();
  }
  function palette() { return THEMES[state.mode]; }
  function syncDirection() {
    Array.prototype.forEach.call(state.root.querySelectorAll('[data-direction]'), function (b) {
      b.setAttribute('aria-pressed', b.getAttribute('data-direction') === state.dir ? 'true' : 'false');
    });
  }

  // Kartan som Mermaid, att lägga i dokumentation: samma riktning, nivå och valda anrop som kartan visar. Filen följer inte filtret, flera
  // anrop eller (om inte sidan tar hand om det, mermaidAccepts) det som är dolt eller inte nås: samma besked för alla sidor som bäddar in kartan.
  var CLI = 'dotnet systemmap --mermaid karta.mmd';
  function refusal(message, fix, flags) { return { message: message, tip: fix + ', eller skapa filen med ' + CLI + ' ' + flags + '.' }; }
  function mermaidRefusal() {
    var accepts = state.opts.mermaidAccepts || {}, hidden = 'Mermaid-filen kan inte följa det som är dolt i sidan.';
    if (filterActive()) return refusal('Mermaid-filen kan inte följa filtret.', 'Välj Rensa', '--entry "<anrop>"');
    if (state.picked.length > 1) return refusal('Mermaid-filen kan bara visa ett anrop i taget.', 'Kryssa ett anrop eller inget', '--entry "<anrop>"');
    if (hiddenIds().length && !accepts.hidden) return refusal(hidden, 'Välj Visa dem i rutan nere till vänster', '--hide <namn>');
    if (!state.unreached && !accepts.unreached) return refusal(hidden, 'Bocka i Visa delar som inte nås i rutan nere till vänster', '--no-unreached');
    return null;
  }
  async function downloadMermaid() {
    var button = $('map-mermaid'), status = $('map-mermaid-status');
    if (!state.map || button.getAttribute('aria-disabled') === 'true') return;
    button.setAttribute('aria-disabled', 'true');
    status.textContent = '';
    try {
      var refused = mermaidRefusal();
      if (refused) throw refused;
      var key = state.entry ? entryKey(state.entry) : null;
      var text = await state.opts.mermaid(state.level, state.dir, key, hiddenIds(), state.unreached);
      var name = 'systemmap' + (key ? '-' + key.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/-$/, '') : '') + '.mmd';
      var link = document.createElement('a');
      link.href = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
      link.download = name;
      document.body.appendChild(link); link.click(); link.remove();
      setTimeout(function () { URL.revokeObjectURL(link.href); }, 1000);
      status.textContent = 'Sparad som ' + name + '.';
      announce('Kartan är sparad som ' + name + '.');
    } catch (e) {
      var error = e && e.message ? e : { message: 'Mermaid-filen kunde inte skapas.', tip: '' };
      if (state.opts.onError) state.opts.onError(error);
      else { status.textContent = error.message + (error.tip ? ' ' + error.tip : ''); announce(status.textContent); }
    } finally {
      button.removeAttribute('aria-disabled');
    }
  }

  function renderMap(map) {
    state.map = map;
    state.allNodes = {};   // hela kartans delar, till skillnad från state.nodes som bara har dem som syns
    map.overview.nodes.forEach(function (x) { state.allNodes[x.id] = x; });
    state.anyUnreached = hasUnreached(map);
    state.root.hidden = false;
    state.hidden = loadHidden(map);
    state.unreached = loadUnreached(map);
    prepare();
    useView();   // tar också bort kryssade anrop som filtret stänger ute
    var diff = map.differences || [];
    $('map-diff-summary').textContent = 'Skillnader (' + diff.length + ')';
    fillList('map-diff-list', diff.map(function (d) {
      return d.kind === 'only-in-code'
        ? 'Bara i koden: ' + d.method + ' ' + d.path + ' (' + d.handler + '). API-beskrivningen saknar anropet.'
        : 'Bara i API-beskrivningen: ' + d.method + ' ' + d.path + (d.operationId ? ' (' + d.operationId + ')' : '') + '. Koden saknar anropet.';
    }));
    $('map-diff').hidden = !diff.length;
    var warn = map.warnings || [];
    fillList('map-warnings-list', warn);
    $('map-warnings-summary').textContent = 'Varningar (' + warn.length + ')';
    $('map-warnings').hidden = !warn.length;
    var some = map.entryPoints.length > 0, drawable = some && !!window.cytoscape;
    $('map-wrap').hidden = !drawable;
    hideCard();
    state.shut = {};
    if (state.cy) state.cy.elements().remove();       // ny karta: lägg ut allt från början
    setPicked(state.picked.map(function (e) { return entryFor(e.operationId, e.method, e.path, e.project); }).filter(Boolean));
    syncControls();
    if (some) showPicked(true);
  }

  // ---- Det som är dolt, det som inte nås och filtret ----
  // Delarna som går att dölja: projekt, klasser och integrationer. Anrop går inte att dölja.
  function hideable(n) { return n.kind === 'project' || n.kind === 'class' || n.kind === 'integration'; }
  function hiddenKey(map) { return 'map-hidden:' + (map.solution || ''); }
  // Det som är dolt när kartan öppnas: det användaren bockat av förut (sparat), annars det sidan ber om. Delar som inte finns hoppas över.
  function loadHidden(map) {
    var exists = {}, keep = {};
    map.overview.nodes.forEach(function (n) { if (hideable(n)) exists[n.id] = true; });
    (storedList(hiddenKey(map)) || state.opts.hidden || []).forEach(function (id) { if (exists[id]) keep[id] = true; });
    return keep;
  }
  function hiddenIds() { return Object.keys(state.hidden); }
  // Delar som ingen ingång når (projekt, klasser och metoder med reached: false). Finns inga syns inte valet i rutan nere till vänster.
  function isUnreached(n) { return n.reached === false; }
  function hasUnreached(map) { return map.overview.nodes.some(isUnreached); }
  function unreachedKey(map) { return 'map-unreached:' + (map.solution || ''); }
  // Syns de när kartan öppnas: det användaren valde förut (sparat), annars det sidan ber om (från början ja).
  function loadUnreached(map) {
    var saved = stored(unreachedKey(map), ['show', 'hide'], null);
    return saved ? saved === 'show' : state.opts.unreached !== false;
  }
  // Kartan utan det som ingen ingång når (samma regel som CodeMapOverview.WithoutUnreached i C#). Delarna har inga pilar, så bara noderna går bort.
  function dropUnreached(overview) {
    if (!overview.nodes.some(isUnreached)) return overview;
    return { nodes: overview.nodes.filter(function (n) { return !isUnreached(n); }), links: overview.links };
  }
  // Kartan utan de dolda delarna och, om det inte visas, utan det som ingen ingång når. Räknas om bara när kartan, det dolda eller
  // valet om det som inte nås ändras, inte vid varje kryss i filtret.
  function prepare() {
    var pre = hideParts(state.map.overview, hiddenIds());
    state.pre = state.unreached ? pre : dropUnreached(pre);
  }
  // Valen sparas till nästa gång; ritas om gör den som väljer.
  function setUnreached(on) {
    state.unreached = on;
    remember(unreachedKey(state.map), on ? 'show' : 'hide');
    prepare();
    refresh(on ? 'Delar som inte nås visas.' : 'Delar som inte nås är dolda.');
  }
  // "Visa dem": allt som var dolt visas, och det sparas.
  function showHidden() {
    state.hidden = {};
    remember(hiddenKey(state.map), '[]');
    prepare();
    refresh('Alla delar visas.');
  }
  // Slutet på en ändring av det dolda, det som inte nås eller filtret: det som visas, listorna och rutorna följer med, skärmläsaren får veta
  // vad som visas (said är en text, eller en funktion som ger den när kartan är uträknad) och kartan ritas om och passas in.
  function refresh(said) {
    useView();
    syncControls();
    hideCard();
    announce(typeof said === 'function' ? said() : said);
    if (state.cy && state.map.entryPoints.length) render(function () { fitView(); }, true);
  }
  // Kartan med filtret (state.pre ur filterView), så att allt annat ritas ur state.view och bygger sina uppslag ur den.
  function useView() {
    sanitizeFilter();
    var f = filterView(state.pre);
    state.view = f.view; state.calls = f.calls;
    state.visibleEntries = f.calls ? state.map.entryPoints.filter(function (e) { return f.calls[entryKey(e)]; }) : state.map.entryPoints;
    state.groups = callGroups(state.visibleEntries);
    // Faller det valda anropet bort ur filtret visas hela systemet.
    if (f.calls && state.picked.some(function (e) { return !f.calls[entryKey(e)]; })) {
      setPicked(state.picked.filter(function (e) { return f.calls[entryKey(e)]; })); state.shut = {};
    }
    state.nodes = {}; state.kids = {}; state.out = {}; state.into = {};
    state.view.nodes.forEach(function (n) {
      state.nodes[n.id] = n;
      if (n.parent) (state.kids[n.parent] = state.kids[n.parent] || []).push(n.id);
    });
    state.view.links.forEach(function (k) {
      (state.out[k.from] = state.out[k.from] || []).push(k);
      (state.into[k.to] = state.into[k.to] || []).push(k);
    });
    computeSteps();
  }

  // ---- Filtret ----
  // Listorna Projekt, Klass, Funktion och Integration är flerval (kryssrutor): inom en lista gäller ELLER. Klass, Funktion och Integration börjar
  // med val av detaljnivå ("Alla", "Visas inte", "Alla, med läsningar och skrivningar", radioknappar): de är inga filter, de visar och byter
  // state.level. Klass: Visas inte är projects, Klass: Alla är classes, Funktion: Alla är methods och Integration: Alla, med läsningar och
  // skrivningar är all; "Klass: Visas inte" tar bort klassernas och funktionernas kryss och stänger av Funktion.
  function emptyFilter() { return { project: [], cls: [], fn: [], integ: [] }; }
  // Är något kryssat som filter? (Anrop är inget filter.)
  function filterActive() { var f = state.f; return f.project.length + f.cls.length + f.fn.length + f.integ.length > 0; }
  // Är allt som i början (Rensa är då grå)?
  function pristine() { return !state.picked.length && !filterActive() && state.level === 'projects'; }
  // Valen hänger ihop: projekten begränsar klasserna och funktionerna, klasserna funktionerna. Det som inte hör till de kryssade tas bort.
  function normalizeFilter() {
    var f = state.f;
    if (f.project.length) {
      var projects = idSet(f.project), inside = function (id) { return within(id, projects); };
      f.cls = f.cls.filter(inside); f.fn = f.fn.filter(inside);
    }
    if (f.cls.length) { var classes = idSet(f.cls); f.fn = f.fn.filter(function (m) { return classes[state.allNodes[m].parent]; }); }
  }
  // Val som inte längre finns (t.ex. något som dolts) tas bort.
  function sanitizeFilter() {
    var f = state.f, here = idSet(state.pre.nodes.map(function (n) { return n.id; }));
    ['project', 'cls', 'fn', 'integ'].forEach(function (k) { f[k] = f[k].filter(function (id) { return here[id]; }); });
    normalizeFilter();
  }
  // Delarna som anropen ska gå genom: de mest precisa av funktioner, klasser och projekt som har kryss.
  function targetIds() { var f = state.f; return f.fn.length ? f.fn : f.cls.length ? f.cls : f.project; }
  // Kartan med bara anropen som går genom någon av de valda koddelarna (eller något under dem) och, om integrationer är valda, når någon av
  // dem (eller något under den). Ger { view, calls }: översikten med bara de länkarna (med anropen begränsade till dessa) och noderna de går
  // mellan med deras föräldrar, och anropens namn. De valda delarna visas alltid. Utan filter är view samma som översikten.
  function filterView(pre) {
    var f = state.f, targets = targetIds(), integs = f.integ, nodes = state.allNodes;
    if (!targets.length && !integs.length) return { view: pre, calls: null };
    var keep = {}, calls = null;
    // Anropen i länkarna som har en av delarna (eller något under dem) i någon ände.
    var through = function (roots) {
      var set = {}, want = idSet(roots);
      pre.links.forEach(function (l) {
        if (within(l.from, want) || within(l.to, want)) l.entries.forEach(function (e) { set[e] = true; });
      });
      return set;
    };
    [targets, integs].forEach(function (roots) {
      if (!roots.length) return;
      var set = through(roots);
      if (calls === null) calls = set;
      else Object.keys(calls).forEach(function (e) { if (!set[e]) delete calls[e]; });
    });
    // En del och dess föräldrar visas; ett föräldraled som redan står med har sina föräldrar med.
    var mark = function (id) { climb(nodes[id], nodes, function (n) { if (keep[n.id]) return true; keep[n.id] = true; return false; }); };
    var links = [];
    pre.links.forEach(function (l) {
      var entries = l.entries.filter(function (e) { return calls[e]; });
      if (!entries.length) return;
      links.push(Object.assign({}, l, { entries: entries }));
      mark(l.from); mark(l.to);
    });
    targets.concat(integs).forEach(mark);
    var visible = pre.nodes.filter(function (n) { return keep[n.id]; }).map(function (n) {
      return Object.assign({}, n, { entries: (n.entries || []).filter(function (e) { return calls[e]; }) });
    });
    return { view: { nodes: visible, links: links }, calls: calls };
  }
  // Ändrar valen (mutate ändrar state.f och får ändra state.level och annat), ritar om och passar in kartan. Ändrades inget görs inget.
  // said är det skärmläsaren får höra, annars meningen om filtret eller nivån.
  function changeFilter(mutate, said) {
    var before = state.level, was = filterSignature();
    mutate(state.f);
    if (filterSignature() === was) return;
    // Ny nivå: det man fällt ut eller ihop själv gäller tills nivån byts.
    if (state.level !== before) { state.open = {}; state.shut = {}; remember('map-level', state.level); }
    openSelected();
    refresh(function () { return filterActive() ? statusSentence() : said || (state.level !== before ? LEVEL_TEXT[state.level] : 'Visar alla anrop.'); });
  }
  function filterSignature() { return JSON.stringify([state.f, state.level, Object.keys(state.pickedKeys)]); }
  var LEVEL_TEXT = { projects: 'Visar bara projekten.', classes: 'Visar klasserna.', methods: 'Visar funktionerna.', all: 'Visar allt, även läsningar och skrivningar.' };
  // Valda funktioner fäller ut sin klass och sitt projekt, valda klasser sitt projekt, så att de valda delarna syns oavsett nivå.
  function openSelected() {
    var f = state.f;
    f.fn.concat(f.cls).forEach(function (id) {
      climb(state.allNodes[id], state.allNodes, function (n) { if (n.parent) { state.open[n.parent] = true; delete state.shut[n.parent]; } });
    });
  }
  function nameInMap(id) { return nameOf(id, state.allNodes); }
  // Namnen på det valda, eller antalet när det är fler än två ("3 klasser").
  function namesOf(ids, plural) {
    return ids.length > 2 ? ids.length + ' ' + plural : ids.map(nameInMap).join(' och ');
  }
  function statusSentence() {
    var f = state.f, targets = targetIds(), n = state.visibleEntries.length, total = state.map.entryPoints.length, where = [];
    var plural = f.fn.length ? 'funktioner' : f.cls.length ? 'klasser' : 'projekt';
    if (targets.length) where.push('genom ' + namesOf(targets, plural));
    if (f.integ.length) where.push('till ' + namesOf(f.integ, 'integrationer'));
    return n ? n + ' av ' + total + ' anrop går ' + where.join(' ') + '.' : 'Inga anrop går ' + where.join(' ') + '.';
  }
  // Statusrutan uppe till vänster i kartan: bara när ett filter är valt, och bara antalet (fälten säger vad som är valt).
  function syncStatus() {
    var box = $('map-status'), on = filterActive(), was = !box.hidden;
    box.hidden = !on;
    if (!on) return;
    var n = state.visibleEntries.length;
    box.textContent = '';
    if (n) { box.appendChild(el('strong', null, String(n))); box.appendChild(document.createTextNode(' av ' + state.map.entryPoints.length + ' anrop')); }
    else box.textContent = 'Inga anrop';
    if (!was) placeStatus();
  }
  // Rutan ligger under verktygsraden, som kan ha brutits till flera rader. Den flyttas när den visas och när kartans storlek ändras.
  function placeStatus() { $('map-status').style.top = ($('map-bar').offsetHeight + 10) + 'px'; }

  // Listorna och det som visas av valen: texterna, vilka som är kryssade, nivåvalen (ur state.level), Rensa, rutan nere till vänster och statusrutan.
  function syncControls() {
    var f = state.f, c = state.combos, nodes = state.pre.nodes, level = state.level;
    var groupsOf = function (list, groupName, text) {
      var by = {}, out = [];
      list.forEach(function (n) {
        var g = groupName(n);
        if (!by[g]) { by[g] = { label: g, items: [] }; out.push(by[g]); }
        by[g].items.push({ value: n.id, text: text(n) });
      });
      return out;
    };
    c.entry.setItems(state.groups.map(function (g) {
      return { label: g.label, items: g.entries.map(function (e) { var key = entryKey(e); return { value: key, text: key }; }) };
    }));
    syncPicked();
    var projects = nodes.filter(function (n) { return n.kind === 'project'; });
    c.project.setItems([{ label: null, items: projects.map(function (n) { return { value: n.id, text: n.name }; }) }]);
    c.project.set(f.project, '');
    var name = function (id) { return state.allNodes[id].name; };
    var wanted = idSet(f.project), inProjects = function (n) { return !f.project.length || within(n.id, wanted); };
    var classes = nodes.filter(function (n) { return n.kind === 'class' && inProjects(n); });
    c.cls.setItems(groupsOf(classes, function (n) { return name(n.parent); }, function (n) { return n.name; }));
    c.cls.set(f.cls, level === 'projects' ? 'none' : 'all');
    var methods = nodes.filter(function (n) { return n.kind === 'method' && inProjects(n) && (!f.cls.length || f.cls.indexOf(n.parent) >= 0); });
    c.fn.setItems(groupsOf(methods, function (n) { var k = state.allNodes[n.parent]; return name(k.parent) + ' / ' + k.name; }, function (n) { return n.name + '()'; }));
    c.fn.set(f.fn, level === 'methods' || level === 'all' ? 'all' : 'none');
    c.fn.setDisabled(level === 'projects');
    var ints = nodes.filter(function (n) { return n.kind === 'integration'; });
    c.integ.setItems(groupsOf(ints, function (n) { return kindName(integrationOf(n.ref).kind); }, function (n) { return n.name; }));
    c.integ.set(f.integ, level === 'all' ? 'reads' : 'all');
    syncLegend();
    syncStatus();
  }
  // De kryssade anropen i Anrop-listan, och Rensa (grå när inget är valt).
  function syncPicked() {
    state.combos.entry.set(state.picked.map(entryKey), '');
    $('map-clear').setAttribute('aria-disabled', pristine() ? 'true' : 'false');
  }
  // Rutan nere till vänster: valet om det som inte nås syns (med den streckade rutan som förklaring) och, när något är dolt, hur många och
  // "Visa dem". Den syns bara när något av det finns.
  function syncLegend() {
    var n = hiddenIds().length, names = hiddenIds().map(nameInMap);
    $('map-unreached-field').hidden = !state.anyUnreached;
    $('map-unreached').checked = state.unreached;
    $('map-hidden-row').hidden = n === 0;
    $('map-hint').textContent = n === 1 ? '1 dold med --hide' : n + ' dolda med --hide';
    $('map-hint').title = $('map-show-all').title = (n === 1 ? 'Dold: ' : 'Dolda: ') + names.join(', ');
    $('map-legend').hidden = !state.anyUnreached && n === 0;
  }

  // ---- Sökbara listor med flerval ----
  // En sökbar lista enligt ARIA 1.2 (combobox med listbox, aria-multiselectable). Alternativen har en kryssruta; högst upp kan det finnas
  // nivåval (radioknappar). Bredden (minst) sätter systemmap.css. o: key, id, label, plural ("klasser"), levels ([{ value, text }] eller null),
  // empty (texten när inget är kryssat och det inte finns nivåval), note (grå rad överst i listan), name(value) (namnet i title när flera är
  // kryssade), onToggle(value), onLevel(value), onClear().
  // setItems(groups): [{ label, items: [{ value, text }] }] (label null = ingen gruppetikett). set(values, level): de kryssade och valt nivåval.
  // Alternativens element byggs när listan öppnas och efter att alternativen ändrats; en sökning och ett kryss växlar bara hidden och aria-selected.
  // Skriv filtrerar (delsträng, skiftlägesokänsligt). Pil upp och ned, Home och End flyttar; Enter och Mellanslag (när inget är skrivet) växlar
  // ett val; listan står kvar öppen. Esc, Tab eller klick utanför stänger den och återställer texten.
  function combo(o) {
    var field = el('span', 'map-field map-field-' + o.key), label = el('label', 'map-label', o.label), box = el('span', 'map-combo');
    var input = el('input', 'map-input'), arrow = el('span', 'map-arrow', '▾'), clear = el('button', 'map-x', '×'), list = el('ul', 'map-list');
    var levels = o.levels || [], groups = [], sig = '', texts = Object.create(null);
    var options = [], parts = [], none = null, built = false;   // alternativen (li, nivåvalen först), grupperna med sina alternativ och "Inga träffar"
    var checked = [], level = null, shown = [], display = '';
    var opened = false, typed = false, active = -1, query = '';
    label.htmlFor = input.id = o.id;
    input.type = 'text'; input.setAttribute('role', 'combobox'); input.setAttribute('aria-expanded', 'false'); input.setAttribute('aria-controls', o.id + '-list');
    input.setAttribute('aria-autocomplete', 'list'); input.autocomplete = 'off'; input.spellcheck = false;
    arrow.setAttribute('aria-hidden', 'true');
    clear.type = 'button'; clear.hidden = true; clear.setAttribute('aria-label', 'Ta bort filtret ' + o.label); clear.title = 'Ta bort filtret ' + o.label;
    list.id = o.id + '-list'; list.setAttribute('role', 'listbox'); list.setAttribute('aria-multiselectable', 'true'); list.setAttribute('aria-label', o.label); list.hidden = true;
    box.appendChild(input); box.appendChild(arrow); box.appendChild(clear); box.appendChild(list); field.appendChild(label); field.appendChild(box);

    function textOf(value) { return value in texts ? texts[value] : value; }
    // Det som står i fältet när listan är stängd: nivåtexten, namnet eller antalet.
    function summary() {
      if (!checked.length) { var lv = levels.filter(function (l) { return l.value === level; })[0]; return lv ? lv.text : o.empty; }
      return checked.length === 1 ? textOf(checked[0]) : checked.length + ' ' + o.plural;
    }
    function sync() {
      display = summary();
      if (!opened || !typed) { input.value = display; if (opened && document.activeElement === input) input.select(); }   // öppen utan sökning: texten visar valet
      box.classList.toggle('has-value', checked.length > 0);
      clear.hidden = !checked.length;
      input.title = checked.length > 1 ? checked.map(o.name).join(', ') : '';
      // Ett kryssat val får plats med hela namnet (minst den bredd systemmap.css ger).
      input.style.width = checked.length ? Math.ceil(textWidth(display, 16) * 1.08) + 64 + 'px' : '';
    }
    function highlight(into, text, q) {
      var i = q ? text.toLowerCase().indexOf(q) : -1;
      into.textContent = '';
      if (i < 0) { into.textContent = text; return; }
      into.appendChild(document.createTextNode(text.slice(0, i)));
      into.appendChild(el('mark', null, text.slice(i, i + q.length)));
      into.appendChild(document.createTextNode(text.slice(i + q.length)));
    }
    // Bygger alternativen: nivåvalen först, sedan grupperna.
    function build() {
      list.textContent = ''; options = []; parts = [];
      var option = function (into, item, isLevel) {
        var li = el('li', 'map-option'), mark = el('span', isLevel ? 'map-radio' : 'map-check'), text = el('span', 'map-text', item.text);
        li.id = o.id + '-o' + options.length; li.setAttribute('role', 'option'); li.setAttribute('aria-selected', 'false');
        li._value = item.value; li._level = isLevel; li._text = item.text; li._label = text; li._query = ''; li._on = false;
        mark.setAttribute('aria-hidden', 'true');
        li.appendChild(mark); li.appendChild(text);
        into.appendChild(li); options.push(li);
        return li;
      };
      // En grupp: en yta med etiketten (nivåvalen har ingen synlig) och en ul för skärmläsaren.
      var group = function (name, items, isLevel) {
        var wrap = el('li', isLevel ? 'map-levels' : null), ul = el('ul');
        wrap.setAttribute('role', 'presentation'); ul.setAttribute('role', 'group'); ul.setAttribute('aria-label', name);
        if (!isLevel) { var title = el('div', 'map-group-label', name); title.setAttribute('aria-hidden', 'true'); wrap.appendChild(title); }
        parts.push({ wrap: wrap, lis: items.map(function (item) { return option(ul, item, isLevel); }) });
        wrap.appendChild(ul); list.appendChild(wrap);
      };
      if (o.note) { var note = el('li', 'map-note', o.note); note.setAttribute('role', 'presentation'); list.appendChild(note); }
      if (levels.length) group('Nivå', levels, true);
      groups.forEach(function (g) {
        if (g.label) group(g.label, g.items, false);
        else g.items.forEach(function (item) { option(list, item, false); });
      });
      none = el('li', 'map-none', 'Inga träffar'); none.setAttribute('role', 'presentation'); none.hidden = true; list.appendChild(none);
      built = true;
    }
    // Det som är valt (aria-selected): nivåvalet och de kryssade.
    function paint() {
      var on = idSet(checked);
      options.forEach(function (li) {
        var selected = li._level ? li._value === level : !!on[li._value];
        if (selected !== li._on) { li._on = selected; li.setAttribute('aria-selected', selected ? 'true' : 'false'); }
      });
    }
    function keyOf(li) { return li._value + '|' + li._level; }
    // Sökningen: träffarna visas (med träffen markerad), resten och tomma grupper döljs. Nivåvalen står kvar. at: alternativet som ska
    // vara markerat om det finns kvar (keyOf), annars första träffen vid sökning och annars översta alternativet.
    function show(at) {
      var q = query.toLowerCase(), any = false;
      if (shown[active]) shown[active].classList.remove('active');
      options.forEach(function (li) {
        if (li._level) return;
        var hit = !q || li._text.toLowerCase().indexOf(q) >= 0;
        li.hidden = !hit;
        if (!hit) return;
        any = true;
        if (li._query !== q) { li._query = q; highlight(li._label, li._text, q); }
      });
      parts.forEach(function (p) { p.wrap.hidden = !p.lis.some(function (li) { return !li.hidden; }); });
      none.hidden = any;
      shown = options.filter(function (li) { return !li.hidden; });
      var start = 0;
      shown.forEach(function (li, i) { li._at = i; });
      if (q) shown.some(function (li, i) { if (li._level) return false; start = i; return true; });
      if (at) shown.forEach(function (li, i) { if (keyOf(li) === at) start = i; });
      active = -1;
      setActive(shown.length ? start : -1, !at);
    }
    function setActive(i, scroll) {
      if (shown[active]) shown[active].classList.remove('active');
      active = i;
      var li = shown[i];
      if (!li) { input.removeAttribute('aria-activedescendant'); return; }
      li.classList.add('active');
      input.setAttribute('aria-activedescendant', li.id);
      if (scroll === false) return;
      var t = li.offsetTop, b = t + li.offsetHeight;
      if (t < list.scrollTop + 24) list.scrollTop = Math.max(0, t - 24);
      else if (b > list.scrollTop + list.clientHeight) list.scrollTop = b - list.clientHeight;
    }
    // Öppnar listan. Utan sökning markeras texten, så att det man skriver ersätter den.
    function open(q) {
      opened = true; query = q; list.hidden = false; input.setAttribute('aria-expanded', 'true');
      if (!built) build();
      paint();
      show(null);
      if (!q) input.select();
    }
    function close() {
      if (!opened) return;
      opened = false; typed = false; query = ''; list.hidden = true; input.setAttribute('aria-expanded', 'false'); input.removeAttribute('aria-activedescendant');
      input.value = display;   // texten är åter det valda
    }
    function activate(li) { if (li._level) o.onLevel(li._value); else o.onToggle(li._value); }
    input.addEventListener('click', function () { if (!opened) open(''); });
    input.addEventListener('input', function () { typed = true; query = input.value.trim(); if (opened) show(null); else open(query); });
    input.addEventListener('keydown', function (ev) {
      var n = shown.length;
      if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
        ev.preventDefault();
        if (!opened) open('');
        else if (n) setActive(ev.key === 'ArrowDown' ? (active + 1) % n : (active - 1 + n) % n);
      } else if ((ev.key === 'Home' || ev.key === 'End') && opened) {
        ev.preventDefault();
        if (n) setActive(ev.key === 'Home' ? 0 : n - 1);
      } else if (ev.key === 'Enter' && opened) {
        ev.preventDefault();
        if (shown[active]) activate(shown[active]);
      } else if (ev.key === ' ' && (!opened || !typed)) {
        // Mellanslag växlar valet så länge inget är skrivet; annars är det ett tecken i sökningen.
        ev.preventDefault();
        if (!opened) open(''); else if (shown[active]) activate(shown[active]);
      } else if (ev.key === 'Escape' && opened) {
        ev.preventDefault(); ev.stopPropagation();   // stänger bara listan
        close();
      }
    });
    list.addEventListener('mousedown', function (ev) { ev.preventDefault(); });   // fokus stannar i fältet
    list.addEventListener('click', function (ev) {
      var li = ev.target.closest ? ev.target.closest('.map-option') : null;
      if (li) activate(li);
    });
    list.addEventListener('mousemove', function (ev) {
      var li = ev.target.closest ? ev.target.closest('.map-option') : null;
      if (li && li._at !== active) setActive(li._at, false);
    });
    clear.addEventListener('click', function () { o.onClear(); input.focus(); });
    field.addEventListener('focusout', function (ev) { if (!field.contains(ev.relatedTarget)) close(); });
    return {
      field: field, input: input,
      set: function (values, lv) { checked = values; level = lv; sync(); if (opened) paint(); },
      // Ändrade alternativ byggs om (och står kvar öppna om listan är öppen); samma alternativ som förut rörs inte.
      setItems: function (g) {
        var next = JSON.stringify(g);
        if (next === sig) return;
        sig = next; groups = g; built = false; texts = Object.create(null);
        g.forEach(function (grp) { grp.items.forEach(function (item) { texts[item.value] = item.text; }); });
        if (!opened) return;
        var at = shown[active] ? keyOf(shown[active]) : null, top = list.scrollTop;
        build(); paint(); show(at);
        list.scrollTop = top;
      },
      setDisabled: function (off) { if (off) close(); input.disabled = off; box.classList.toggle('disabled', off); }
    };
  }

  // Kartan utan de dolda delarna (samma regel som CodeMapOverview.Hide i C#). Ett dolt projekt eller en dold klass tas bort med sina
  // metoder, men vägarna genom dem består: en pil A → dold → B blir A → B (kedjor och cykler räknas igenom), med de anrop som går
  // hela vägen, och finns pilen redan slås de ihop. En dold integration tas bort med sina rader och pilarna dit. Anrop går inte att dölja.
  // overview: { nodes, links } som i JSON. hidden: id:n. Ger en ny översikt, eller samma om inget döljs.
  function hideParts(overview, hidden) {
    var hide = {}, byId = {}, gone = {}, any = false;
    (hidden || []).forEach(function (id) { hide[id] = true; });
    overview.nodes.forEach(function (n) { byId[n.id] = n; });
    overview.nodes.forEach(function (node) {
      for (var n = node; n; n = n.parent ? byId[n.parent] : null)
        if (hideable(n) && hide[n.id]) { gone[node.id] = true; any = true; break; }
    });
    if (!any) return overview;
    var outgoing = {}, kept = [], index = {};
    overview.links.forEach(function (l) { (outgoing[l.from] = outgoing[l.from] || []).push(l); });
    function emit(from, to, file, line, entries) {
      if (from === to) return;
      var key = from + '\u0000' + to, k = index[key];
      if (!k) {
        k = index[key] = { from: from, to: to, entries: [] };
        if (file != null) k.file = file;
        if (line != null) k.line = line;
        kept.push(k);
      }
      entries.forEach(function (e) { if (k.entries.indexOf(e) < 0) k.entries.push(e); });
    }
    overview.links.forEach(function (link) {
      if (gone[link.from]) return;
      if (!gone[link.to]) { emit(link.from, link.to, link.file, link.line, link.entries); return; }
      // Pilen går in i en dold del: följ den vidare tills den kommer ut. Vid varje del noteras vilka anrop som redan gått igenom
      // den, så att en cykel av dolda delar tar slut.
      var seen = {}, work = [{ node: link.to, entries: link.entries.slice() }];
      seen[link.to] = {};
      link.entries.forEach(function (e) { seen[link.to][e] = true; });
      while (work.length) {
        var item = work.pop();
        (outgoing[item.node] || []).forEach(function (next) {
          var through = item.entries.filter(function (e) { return next.entries.indexOf(e) >= 0; });
          if (!through.length) return;
          if (!gone[next.to]) { emit(link.from, next.to, next.file, next.line, through); return; }
          var done = seen[next.to] = seen[next.to] || {};
          var fresh = through.filter(function (e) { if (done[e]) return false; done[e] = true; return true; });
          if (fresh.length) work.push({ node: next.to, entries: fresh });
        });
      }
    });
    // Anropen i pilarna i anropens ordning, oavsett i vilken ordning vägarna följdes.
    var order = {};
    overview.nodes.forEach(function (n, i) { if (n.kind === 'entry') order[n.name] = i; });
    var rank = function (e) { return e in order ? order[e] : Infinity; };
    kept.forEach(function (k) { k.entries.sort(function (a, b) { return rank(a) === rank(b) ? 0 : rank(a) < rank(b) ? -1 : 1; }); });
    return { nodes: overview.nodes.filter(function (n) { return !gone[n.id]; }), links: kept };
  }

  // Ett HTTP-anrop heter metod och sökväg, t.ex. POST /orders; annat (bakgrund, konsol, meddelande, jobb, gRPC, SignalR) har ett eget namn.
  // Namnet på anropet i kartan, som EntryPoint.Key i C#: namnet, och när flera anrop heter lika projektet efter (qualifier).
  function entryKey(e) { return (e.name || e.method + ' ' + e.path) + (e.qualifier ? ' (' + e.qualifier + ')' : ''); }
  function entryByKey(key) { return state.map.entryPoints.find(function (e) { return entryKey(e) === key; }) || null; }

  // De kryssade anropen. entry är det enda anropet när bara ett är kryssat, annars null.
  function setPicked(list) {
    state.picked = list;
    state.entry = list.length === 1 ? list[0] : null;
    state.pickedKeys = {};
    list.forEach(function (e) { state.pickedKeys[entryKey(e)] = true; });
  }
  function anyPicked(keys) { return state.picked.length > 0 && (keys || []).some(function (k) { return state.pickedKeys[k]; }); }
  // Ett val av användaren (i listan eller på kartan): bara Anrop-listan och Rensa följer med, kartan ritas om.
  function choosePicked(list) {
    var same = list.length === state.picked.length && list.every(function (e) { return state.picked.indexOf(e) >= 0; });
    if (!same) state.shut = {};
    setPicked(list);
    syncPicked();
    showPicked(!same);
  }
  // Andra anrop fäller ut andra delar: allt läggs ut på nytt (och glider dit), så att ordningen blir rätt.
  function showPicked(fresh) {
    if (!window.cytoscape) return;
    hideCard();
    var one = state.entry;
    render(function () { fitView(one ? function () { pulse(entryKey(one)); } : null); }, fresh);
  }
  function choose(list) {
    choosePicked(list);
    var n = list.length;
    announce(n === 0 ? 'Visar hela systemet.' : n === 1 ? 'Visar vägen för ' + entryKey(list[0]) + '.' : 'Visar vägarna för ' + n + ' anrop.');
  }
  // Välj exakt ett anrop (en ingång ur kartan), eller null/undefined = hela systemet.
  function selectEntry(e) { choosePicked(e ? [e] : []); }

  // Delar som fälls ut av sig själva när ett anrop är valt: projekten och integrationerna på vägen (då syns läsningar,
  // skrivningar och meddelanden). Klasserna står kvar som noder.
  function onPath(id) {
    var n = state.nodes[id];
    return !!n && (n.kind === 'integration' || n.kind === 'project') && anyPicked(n.entries);
  }
  // Utfälld: det användaren fällt ut eller ihop (open, shut) gäller före nivån. Nivån fäller ut projekten (Klass: Alla), projekten och
  // klasserna (Funktion: Alla) eller allt (Integration: med läsningar och skrivningar); på nivån Projekt är inget utfällt.
  function levelOpens(n) {
    var l = state.level;
    return l === 'all' || (l === 'classes' && n.kind === 'project') || (l === 'methods' && (n.kind === 'project' || n.kind === 'class'));
  }
  function isOpen(id) {
    if (state.open[id]) return true;
    if (state.shut[id]) return false;
    return state.level !== 'projects' && (levelOpens(state.nodes[id]) || onPath(id));
  }
  function toggle(id) {
    if (isOpen(id)) { delete state.open[id]; state.shut[id] = true; }
    else { state.open[id] = true; delete state.shut[id]; }
    hideCard();
    render();
  }

  // En anropsgrupp (en yta med anrop och ett namn, som "/orders") är hopfälld från början och ritas som ett enda anrop ("/orders · 5 anrop").
  // Den är utfälld när användaren fällt ut den (open, som för projekt och klasser) eller när ett valt anrop ligger i den (om användaren
  // inte fällt ihop den: shut). Gruppens id är 'cg:' och etiketten, både hopfälld (ett anrop) och utfälld (ytan).
  function callsOpen(g) {
    var id = 'cg:' + g.label;
    if (state.open[id]) return true;
    if (state.shut[id]) return false;
    return anyPicked(g.entries.map(entryKey));
  }
  function toggleCalls(id) {
    var g = state.groups.filter(function (x) { return 'cg:' + x.label === id; })[0];
    if (!g) return;
    if (callsOpen(g)) { delete state.open[id]; state.shut[id] = true; }
    else { state.open[id] = true; delete state.shut[id]; }
    hideCard();
    render();
  }

  // Delen som syns för en nod: den själv, eller den närmaste hopfällda delen den ligger i. Svaren gäller tills nästa ritning.
  function visibleOf(id) {
    if (id in state.visible) return state.visible[id];
    var chain = [], n = state.nodes[id], hit = null;
    while (n) { chain.unshift(n.id); n = n.parent ? state.nodes[n.parent] : null; }
    for (var i = 0; i < chain.length - 1 && hit === null; i++) if (!isOpen(chain[i])) hit = chain[i];
    return (state.visible[id] = hit !== null ? hit : chain.length ? chain[chain.length - 1] : null);
  }

  // ---- Utseendet ----
  function projectOf(n) {
    var p = projectNodeOf(n, state.nodes);
    return p ? p.name : null;
  }
  // Steget för varje klass i sitt projekt: 1 när ett anrop in eller ett annat projekt anropar den (eller ingen gör det),
  // annars 1 + det högsta steget hos klasserna i projektet som anropar den. Vägen räknas med ett tak, så att cykler tar slut.
  // Ger också första lagret för varje projekt (base) och lagret för integrationerna (last). Projekten står efter varandra i flödets
  // ordning: ett projekts djup är kortaste vägen från ett anrop in (projekten som anropen går in i har djup 1, projekten de anropar
  // djup 2 och så vidare), och projekt på samma djup delar lager.
  var MAX_STEP = 6, MAX_DEPTH = 1000;
  function computeSteps() {
    var steps = {}, roots = {}, calls = [], base = {}, span = {}, next = 1, depth = {}, onward = {};
    var owner = function (id) {
      var n = state.nodes[id];
      while (n && n.kind !== 'class' && n.kind !== 'entry') n = n.parent ? state.nodes[n.parent] : null;
      return n;
    };
    Object.keys(state.nodes).forEach(function (id) { if (state.nodes[id].kind === 'class') steps[id] = 1; });
    state.view.links.forEach(function (k) {
      var a = owner(k.from), b = owner(k.to);
      if (!a || !b || a === b || b.kind !== 'class') return;
      if (a.kind === 'entry') depth[projectOf(b)] = 1;
      else if (projectOf(a) !== projectOf(b)) (onward[projectOf(a)] = onward[projectOf(a)] || []).push(projectOf(b));
      if (a.kind === 'entry' || projectOf(a) !== projectOf(b)) roots[b.id] = true;
      else calls.push([a.id, b.id]);
    });
    // Djupet: bredden först från projekten som anropen går in i, så att varje projekt får kortaste vägen.
    var wave = Object.keys(depth);
    while (wave.length) {
      var after = [];
      wave.forEach(function (p) { (onward[p] || []).forEach(function (q) { if (!(q in depth)) { depth[q] = depth[p] + 1; after.push(q); } }); });
      wave = after;
    }
    for (var round = 0, changed = true; round < MAX_STEP && changed; round++) {
      changed = false;
      calls.forEach(function (c) {
        var to = Math.min(MAX_STEP, steps[c[0]] + 1);
        if (!roots[c[1]] && to > steps[c[1]]) { steps[c[1]] = to; changed = true; }
      });
    }
    // Klasser som ingen ingång når har inga pilar: de står i en kolumn efter de andra i sitt projekt.
    var top = {};
    Object.keys(steps).forEach(function (id) { if (!isUnreached(state.nodes[id])) { var p = projectOf(state.nodes[id]); top[p] = Math.max(top[p] || 1, steps[id]); } });
    Object.keys(steps).forEach(function (id) { if (isUnreached(state.nodes[id])) steps[id] = Math.min(MAX_STEP + 1, (top[projectOf(state.nodes[id])] || 0) + 1); });
    Object.keys(steps).forEach(function (id) { var p = projectOf(state.nodes[id]); span[p] = Math.max(span[p] || 1, steps[id]); });
    // Projekten efter varandra i flödets ordning (djupet), sedan integrationerna, och sist projekt som ingen ingång når. Projekt på samma djup
    // delar lager (samma första lager); nästa djup börjar efter det bredaste av dem. Inom ett djup står de i ordningen i state.view.nodes.
    var projects = state.view.nodes.filter(function (n) { return n.kind === 'project'; }), levels = {};
    projects.filter(function (n) { return !isUnreached(n); }).forEach(function (n) { var d = depth[n.name] || MAX_DEPTH; (levels[d] = levels[d] || []).push(n); });
    Object.keys(levels).map(Number).sort(function (x, y) { return x - y; }).forEach(function (d) {
      var widest = 1;
      levels[d].forEach(function (n) { base[n.name] = next; widest = Math.max(widest, span[n.name] || 1); });
      next += widest;
    });
    var last = next;
    projects.filter(isUnreached).forEach(function (n) { base[n.name] = ++next; next += (span[n.name] || 1) - 1; });
    state.steps = steps; state.base = base; state.last = last;
  }
  function stepOf(n) {
    while (n && n.kind !== 'class') n = n.parent ? state.nodes[n.parent] : null;
    return n ? state.steps[n.id] || 1 : 1;
  }
  // Lagret en nod hör till: anropen in (0), projekten efter varandra med klasserna i steg, integrationerna sist. Lagren står i
  // flödets riktning, men placeringen inom ett lager räknar fcose ut.
  function columnOf(n) {
    if (n.kind === 'entry') return 0;
    if (n.kind === 'integration' || n.kind === 'operation') return state.last;
    return (state.base[projectOf(n)] || 1) + stepOf(n) - 1;
  }
  // Färgen för en nodtyp i temat som ritas: egen färg för projekt, klass och funktion, integrationens för den och dess rader, anropens för anrop
  // (och rutorna runt dem).
  function colorOf(kind) {
    var c = palette().node;
    return kind === 'project' || kind === 'class' || kind === 'method' ? c[kind] : kind === 'integration' || kind === 'operation' ? c.integration : c.entry;
  }
  // Pilens färg: till en integration integrationens, från ett anrop anropets, annars grå. kind: 'integration', 'entry' eller 'arrows'.
  function arrowColor(kind) { return palette().node[kind]; }
  function arrowKind(from, to) {
    var a = state.nodes[from], b = state.nodes[to];
    return b && (b.kind === 'integration' || b.kind === 'operation') ? 'integration' : !a || a.kind === 'entry' ? 'entry' : 'arrows';
  }
  // Ikonen för en del, eller null (metoder och rader i andra integrationer än köer har ingen).
  var ENTRY_ICONS = { 'razor-page': 'page', 'blazor-page': 'page', 'mvc-view': 'page', background: 'background', console: 'console', consumer: 'message', job: 'job', grpc: 'plug', signalr: 'signal' };
  function iconKind(n) {
    if (n.kind === 'integration') return integrationOf(n.ref).kind || 'http';
    if (n.kind === 'operation') return iconKind(state.nodes[n.parent]) === 'queue' ? 'message' : null;
    if (n.kind === 'entry') return ENTRY_ICONS[n.ref] || 'entry';
    return n.kind === 'method' ? null : n.kind;
  }
  var ICONS = {
    database: '<ellipse cx="9" cy="4.5" rx="6" ry="2.5"/><path d="M3 4.5 V13.5 A6 2.5 0 0 0 15 13.5 V4.5"/><path d="M3 9 A6 2.5 0 0 0 15 9"/>',
    queue: '<rect x="1.5" y="5" width="15" height="8" rx="4"/><path d="M6 7.5 V10.5 M9 7.5 V10.5 M12 7.5 V10.5"/>',
    message: '<rect x="2.5" y="3.5" width="13" height="11" rx="1.5"/><path d="M5.5 7 H12.5 M5.5 10 H10.5"/>',
    http: '<circle cx="9" cy="9" r="7"/><ellipse cx="9" cy="9" rx="3" ry="7"/><path d="M2 9 H16"/>',
    wcf: '<rect x="2.5" y="2.5" width="13" height="5.5" rx="1.2"/><rect x="2.5" y="10" width="13" height="5.5" rx="1.2"/><path d="M5.5 5.25 h0.01 M5.5 12.75 h0.01"/>',
    storage: '<path d="M2 4 H7.5 L9 6.5 H16 V14.5 H2 Z"/>',
    email: '<rect x="2" y="4" width="14" height="10" rx="1.5"/><path d="M2.5 4.5 L9 9.5 L15.5 4.5"/>',
    project: '<path d="M9 2.5 L16 6 L9 9.5 L2 6 Z"/><path d="M2 9.5 L9 13 L16 9.5"/><path d="M2 12.5 L9 16 L16 12.5"/>',
    'class': '<rect x="2.5" y="2.5" width="13" height="13" rx="2"/><path d="M11.5 6.5 A3.5 3.5 0 1 0 11.5 11.5"/>',
    entry: '<path d="M2 9 H11.5 M8 5.5 L11.5 9 L8 12.5"/><path d="M12.5 3 H15.5 V15 H12.5"/>',
    page: '<rect x="2.5" y="2.5" width="13" height="13" rx="1.5"/><path d="M2.5 6.5 H15.5 M5 4.5 h0.01"/>',
    background: '<path d="M14.5 9 A5.5 5.5 0 1 1 12.9 5.1"/><path d="M14.8 2.8 V5.4 H12.2"/>',
    console: '<rect x="2" y="3" width="14" height="12" rx="1.5"/><path d="M5 7 L7.5 9 L5 11 M9 11.5 H12.5"/>',
    job: '<circle cx="9" cy="9" r="6.5"/><path d="M9 5.2 V9 L11.7 10.6"/>',
    plug: '<path d="M6.5 2.5 V6 M11.5 2.5 V6"/><path d="M4.5 6 H13.5 V8 A4.5 4.5 0 0 1 4.5 8 Z"/><path d="M9 12.5 V15.5"/>',
    signal: '<path d="M3.5 12 A7 7 0 0 1 3.5 6 M14.5 12 A7 7 0 0 0 14.5 6 M6 10.5 A3.6 3.6 0 0 1 6 7.5 M12 10.5 A3.6 3.6 0 0 0 12 7.5"/><circle cx="9" cy="9" r="1"/>'
  };
  function svgUri(w, h, body) {
    return 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h +
      '" viewBox="0 0 ' + w + ' ' + h + '">' + body + '</svg>');
  }
  function iconUri(kind, color) {
    return ICONS[kind] ? svgUri(18, 18, '<g fill="none" stroke="' + color + '" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">' +
      ICONS[kind] + '</g>') : '';
  }
  // Brickan på en hopfälld del: hur mycket som finns inuti, t.ex. "+6".
  // Kanten har kartans bakgrund och siffran nodens fyllning, så att den syns i båda temana.
  function badgeUri(count, color) {
    var text = '+' + count, w = 12 + text.length * 7, t = palette();
    return svgUri(w, 18, '<rect x="0.75" y="0.75" width="' + (w - 1.5) + '" height="16.5" rx="8.25" fill="' + color + '" stroke="' + t.bg + '" stroke-width="1.5"/>' +
      '<text x="' + (w / 2) + '" y="13" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="11" font-weight="700" fill="' + t.fill.node + '">' + text + '</text>');
  }
  // Det som beror på temat för en nod: färgen, ikonen och brickan. ck: nodtypen för färgen, ik: ikonen, count: antalet i brickan (0 = ingen).
  function look(ck, ik, count) {
    var color = colorOf(ck);
    return { color: color, icon: ik ? iconUri(ik, color) : '', badge: count ? badgeUri(count, color) : '' };
  }
  // Byte av tema: färgerna ritas om utan ny utläggning.
  function recolor() {
    var cy = state.cy;
    cy.batch(function () {
      cy.style(cyStyle(palette()));
      cy.nodes().forEach(function (n) {
        var ck = n.data('ck');
        if (ck) n.data(look(ck, n.data('ik'), n.data('bn')));
      });
      cy.edges().forEach(function (e) { var ek = e.data('ek'); if (ek) e.data('color', arrowColor(ek)); });
    });
  }

  // "N funktioner" under en hopfälld klass (räknat i det som visas efter filtret), eller tomt när klassen inte har några.
  function functionsText(id) {
    var n = (state.kids[id] || []).filter(function (k) { return state.nodes[k].kind === 'method'; }).length;
    return n ? n + (n === 1 ? ' funktion' : ' funktioner') : '';
  }
  // Vad som ligger i ett projekt (det som syns om det fälldes ut, enligt filtret): "3 klasser · 4 funktioner". Delar som är 0 utelämnas.
  function contentText(id) {
    var classes = 0, methods = 0, stack = (state.kids[id] || []).slice(), n;
    while (stack.length) {
      n = state.nodes[stack.pop()];
      if (n.kind === 'class') classes++; else if (n.kind === 'method') methods++;
      stack = stack.concat(state.kids[n.id] || []);
    }
    var parts = [];
    if (classes) parts.push(classes + (classes === 1 ? ' klass' : ' klasser'));
    if (methods) parts.push(methods + (methods === 1 ? ' funktion' : ' funktioner'));
    return parts.join(' \u00b7 ');
  }

  var measurer = null, measured = {}, measuredSize = 0;
  function textWidth(text, size) {
    size = size || 13;
    var key = size + '|' + text;
    if (!(key in measured)) {
      measurer = measurer || document.createElement('canvas').getContext('2d');
      if (measuredSize !== size) { measurer.font = size + 'px ' + FONT; measuredSize = size; }
      measured[key] = measurer.measureText(text).width;
    }
    return measured[key];
  }

  // Det som syns just nu: noder (med compound-föräldrar för det som är utfällt) och pilar, där en pil in i en hopfälld del
  // räknas till delen och dubbletter slås ihop. Ger också placeringsreglerna för fcose (rules, align).
  function graphElements() {
    var map = state.map, key = state.picked.length > 0, nodes = [], edges = [], byId = {}, layers = {}, info = {}, parentOf = {}, pads = { system: { x: 20, y: 44 } };
    state.visible = {};
    var mark = function (list) { return key ? (anyPicked(list) ? ' path' : ' dimmed') : ''; };
    var file = String(map.solution || '').split(/[\\/]/).pop();
    nodes.push({ group: 'nodes', data: { id: 'system', label: 'Systemet' + (file ? ' (' + file + ')' : '') }, classes: 'system' });
    var add = function (n, parent) {
      var kids = state.kids[n.id], open = !!kids && isOpen(n.id), kind = iconKind(n);
      var label = n.kind === 'method' ? n.name + '()' : n.kind === 'operation' ? operationText(n.name, n.address) : n.name;
      if (n.kind === 'integration') {
        var short = kindName(integrationOf(n.ref).kind, true);
        if (short !== n.name) label += open ? ' · ' + short : '\n' + short;
      }
      // Rader (metoder, operationer) och avlånga noder (klass, projekt, anrop) har namnet inuti; integrationer är runda med namnet under.
      var pill = n.kind === 'method' || n.kind === 'operation', round = n.kind === 'integration' && !open;
      // Ett hopfällt projekt är en liten variant av den utfällda rutan: texten med innehållet ersätter brickan. En hopfälld klass är en ännu
      // mindre ruta med antalet funktioner på raden under namnet (ingen rad om det är 0) och ingen bricka.
      var box = n.kind === 'project' && !open, cbox = n.kind === 'class' && !open, inside = box ? contentText(n.id) : cbox ? functionsText(n.id) : '';
      if (inside) label += '\n' + inside;
      var count = kids && !open && !box && !cbox ? kids.length : 0;
      var data = Object.assign({ id: n.id, label: label, ck: n.kind, ik: kind, bn: count }, look(n.kind, kind, count)), icon = data.icon;
      if (parent) data.parent = parent;
      var widest = function (px) { return Math.max.apply(null, label.split('\n').map(function (t) { return textWidth(t, px); })); };
      if (pill) data.w = Math.ceil(textWidth(label, 13)) + (icon ? 44 : 24);
      else if (box) data.w = Math.max(Math.ceil(widest(14) * 1.08) + 72, 230);
      else if (cbox) data.w = Math.ceil(widest(14) * 1.08) + 60;
      else if (!open && !round) data.w = Math.ceil(textWidth(label, 14)) + 56;
      var shape = open ? 'group' : box ? 'box' : cbox ? 'cbox' : pill ? 'pill' : round ? 'round' : 'long';
      var cls = shape + ' t-' + n.kind + (data.badge ? ' badge' : '') + (icon && pill ? ' with-icon' : '') + (isUnreached(n) ? ' unreached' : '') + mark(n.entries);
      nodes.push({ group: 'nodes', data: data, classes: cls });
      parentOf[n.id] = parent;
      if (open) pads[n.id] = { x: (n.kind === 'class' ? 10 : 12) + 2, y: (n.kind === 'class' ? 10 : 12) + 24 };
      else info[n.id] = { w: data.w || (round ? Math.max(60, Math.max.apply(null, label.split('\n').map(function (t) { return textWidth(t, 14); })) + 8) : 60),
        h: round ? 94 : pill ? 26 : box ? 84 : cbox ? 50 : n.kind === 'entry' ? 30 : 34 };
      if (open) kids.forEach(function (k) { add(state.nodes[k], n.id); });
      else { var c = columnOf(n); (layers[c] = layers[c] || []).push(n); }
    };
    // Anropen i en grupp ligger i en egen yta med områdets namn, i fast ordning (sökväg, metod). En grupp som inte är utfälld ritas
    // som ett enda anrop ("/orders · 5 anrop"); pilarna från de anropen blir pilar från det. calls: anropen (och hopfällda grupper) i
    // ordning; shut: anrop → den hopfällda gruppen det ligger i; shutKids: hopfälld grupp → anropen i den.
    var callGroup = {}, order = {}, fixed = {}, seq = 0, calls0 = [], shut = {}, shutKids = {};
    state.groups.forEach(function (g) {
      var keys = g.entries.map(entryKey), gid = 'cg:' + g.label;
      if (g.label && !g.single && !callsOpen(g)) {
        var text = g.label + ' \u00b7 ' + keys.length + ' anrop', kinds = g.entries.map(function (e) { return e.kind; });
        var ik = kinds.every(function (k) { return k === kinds[0]; }) && ENTRY_ICONS[kinds[0]] || 'entry', w = Math.ceil(textWidth(text, 14)) + 56;
        nodes.push({ group: 'nodes', data: Object.assign({ id: gid, label: text, ck: 'entry', ik: ik, bn: 0, w: w }, look('entry', ik)), classes: 'long t-entry t-calls' + mark(keys) });
        info[gid] = { w: w, h: 30 };
        (layers[0] = layers[0] || []).push({ id: gid });
        order[gid] = seq++; calls0.push(gid); shutKids[gid] = [];
        keys.forEach(function (k) { shut['in:' + k] = gid; shutKids[gid].push('in:' + k); });
        return;
      }
      keys.forEach(function (k) { order['in:' + k] = seq++; calls0.push('in:' + k); });
      if (!g.label || g.single) return;
      nodes.push({ group: 'nodes', data: Object.assign({ id: gid, label: g.label, ck: 'callgroup' }, look('callgroup')), classes: 'group t-callgroup' + mark(keys) });
      pads[gid] = { x: 16, y: 40 };
      fixed[gid] = true;
      keys.forEach(function (k) { callGroup['in:' + k] = gid; });
    });
    state.view.nodes.forEach(function (n) {
      if (n.kind === 'entry') { if (!shut[n.id]) add(n, callGroup[n.id] || null); }
      else if (n.kind === 'integration') add(n, null);
      else if (n.kind === 'project') add(n, 'system');
    });
    state.view.links.forEach(function (k) {
      var a = visibleOf(k.from), b = visibleOf(k.to);
      a = shut[a] || a;
      if (!a || !b || a === b) return;
      var id = 'k:' + a + '>' + b, e = byId[id];
      if (!e) { e = byId[id] = { id: id, a: a, b: b, entries: [] }; edges.push(e); }
      k.entries.forEach(function (x) { if (e.entries.indexOf(x) < 0) e.entries.push(x); });
    });
    // Pilarna som ritas: utan valt anrop blir pilarna från anropen i en ruta en pil från rutan till varje mål (arrange räknar
    // fortfarande på pilarna per anrop). Med ett valt anrop ritas pilarna per anrop, så att vägen syns.
    var drawn = edges;
    if (!key) {
      drawn = []; byId = {};
      edges.forEach(function (e) {
        var a = callGroup[e.a] || e.a, id = 'k:' + a + '>' + e.b, d = byId[id];
        if (!d) { d = byId[id] = { id: id, a: a, b: e.b, entries: [] }; drawn.push(d); }
        e.entries.forEach(function (x) { if (d.entries.indexOf(x) < 0) d.entries.push(x); });
      });
    }
    // Placeringen (arrange) ger lagren i flödets riktning, ordningen inom varje lager (färre korsande pilar), en startplacering
    // och reglerna till fcose. Allt i ett lager står före allt i nästa (till vänster om eller ovanför), med avstånd som räknar in
    // nodernas storlek och ytornas kanter. Inom lagret får fcose placera fritt, men ytor bredvid varandra hålls isär.
    var lr = state.dir !== 'TB';
    var plan = arrange({ layers: layers, info: info, parentOf: parentOf, pads: pads, edges: edges, lr: lr,
      calls: calls0, order: order, fixed: fixed, groupOf: callGroup });
    state.callBox = Object.assign({}, callGroup, shut); state.callKids = shutKids;
    var calls = plan.calls, rules = plan.rules, ints = plan.integrations;
    // Anropen in. Från vänster till höger (→: uppifrån och ned) står de i den ordning som ger färst korsningar; avståndet räknas
    // från mitt till mitt. Uppifrån och ned blir varje ruta (eller ensamt anrop) en kolumn med anropen under varandra, och
    // kolumnerna står bredvid varandra. units: kolumnerna (→: ett anrop var).
    var callGap = function (a, b) {
      var pa = parentOf[a], pb = parentOf[b];
      // Anrop i olika grupper får också plats för ytornas kant.
      return CALL_GAP + (pa === pb ? 0 : (pa ? pads[pa].y : 0) + (pb ? pads[pb].y : 0));
    };
    var units = lr ? calls.map(function (c) { return { id: c, calls: [c] }; }) : columns(calls, callGroup, info, pads);
    var unitGap = function (a, b) { return lr ? callGap(a.calls[0], b.calls[0]) : columnGap(a, b); };
    // Linjerna som fcose håller raka: tvärs över flödet (→: lodrätt, samma x) och längs flödet (→: vågrätt, samma y).
    var acrossFlow = lr ? 'vertical' : 'horizontal', alongFlow = lr ? 'horizontal' : 'vertical';
    var unitOf = {}, align = {}, bands = [];
    var line = function (axis, ids) { if (ids.length > 1) (align[axis] = align[axis] || []).push(ids); };
    units.forEach(function (u) { u.calls.forEach(function (c) { unitOf[c] = u.id; }); });
    // Uppifrån och ned står kolumnerna bredvid varandra. Inom en kedja (→: alla anrop; uppifrån och ned: anropen i en kolumn)
    // står anropen under varandra, med osynliga fjädrar emellan så att de håller ihop i stället för att dras isär mot var sin
    // controller. Fjädern är 2 längre än regelns avstånd, så att den inte spänner anropen närmare varandra än regeln tillåter.
    if (!lr) for (var j = 0; j + 1 < units.length; j++) rules.push(before(units[j].calls[0], units[j + 1].calls[0], 'x', columnGap(units[j], units[j + 1])));
    (lr ? [calls] : units.map(function (u) { return u.calls; })).forEach(function (chain) {
      for (var q = 0; q + 1 < chain.length; q++) {
        var gap = callGap(chain[q], chain[q + 1]);
        rules.push(before(chain[q], chain[q + 1], 'y', gap)); bands.push([chain[q], chain[q + 1], gap]);
      }
    });
    // Kolumnernas första anrop (→: alla anrop) står på en linje tvärs över flödet.
    line(acrossFlow, units.map(function (u) { return u.calls[0]; }));
    // Integrationerna på sin linje får plats för namnet under ikonen.
    for (var k = 0; k + 1 < ints.length; k++) rules.push(before(ints[k], ints[k + 1], lr ? 'y' : 'x', lr ? 125 : 120));
    line(acrossFlow, ints);
    // Klasserna som anropen in når står på en linje längs flödet med (uppifrån och ned: rakt under) ett av sina anrop, det
    // mittersta, så att pilen från rutan går rakt. Uppifrån och ned ställs en klass per kolumn under kolumnen. Paren hålls i samma
    // ordning och avstånd som klasserna.
    var at = {}, pos = 0, reach = {}, paired = {}, lastIn = {}, along = lr ? 'h' : 'w';
    units.forEach(function (u, i) { at[u.id] = pos; if (i + 1 < units.length) pos += unitGap(u, units[i + 1]); });
    edges.forEach(function (e) {
      var n = state.nodes[e.b];
      if (e.a in unitOf && info[e.b] && n && (n.kind === 'class' || n.kind === 'project')) {
        var list = reach[e.b] = reach[e.b] || [];
        if (list.indexOf(unitOf[e.a]) < 0) list.push(unitOf[e.a]);
      }
    });
    Object.keys(reach).sort(function (x, y) { return plan.rank[x] - plan.rank[y]; }).forEach(function (id) {
      var list = reach[id].sort(function (x, y) { return at[x] - at[y]; }), mid = (list.length - 1) / 2, layer = columnOf(state.nodes[id]);
      var prev = lastIn[layer], room = function (c) { return !prev || at[c] - at[prev.call] >= (info[prev.id][along] + info[id][along]) / 2 + 8; };
      var pick = list.slice().sort(function (x, y) { return Math.abs(list.indexOf(x) - mid) - Math.abs(list.indexOf(y) - mid); })
        .filter(function (c) { return !paired[c] && room(c); })[0];
      if (!pick) return;
      paired[pick] = id; lastIn[layer] = { id: id, call: pick };
    });
    units.forEach(function (u) { line(alongFlow, paired[u.id] ? u.calls.concat([paired[u.id]]) : u.calls); });
    return {
      elements: nodes.concat(plan.anchors).concat(drawn.map(function (e) {
        var ek = arrowKind(e.a, e.b);
        return { group: 'edges', data: { id: e.id, source: e.a, target: e.b, ek: ek, color: arrowColor(ek) }, classes: mark(e.entries).trim() };
      })).concat(bands.map(function (b, i) {
        return { group: 'edges', data: { id: 'band:' + i, source: b[0], target: b[1], color: '#000000', len: b[2] + 2 }, classes: 'band' };
      })),
      rules: rules,
      align: Object.keys(align).length ? align : null,
      seed: plan.seed
    };
  }

  // Anropen in uppifrån och ned: en kolumn per ruta (anropen under varandra) eller ensamt anrop, i anropens ordning. w är
  // kolumnens bredd: det bredaste anropet plus rutans kant.
  function columns(calls, groupOf, info, pads) {
    var list = [], by = {};
    calls.forEach(function (c) {
      var id = groupOf[c] || c, u = by[id];
      if (!u) { u = by[id] = { id: id, calls: [], w: 0 }; list.push(u); }
      u.calls.push(c); u.w = Math.max(u.w, info[c].w);
    });
    list.forEach(function (u) { if (pads[u.id]) u.w += 2 * pads[u.id].x; });
    return list;
  }
  // Avståndet mellan mitten på två kolumner bredvid varandra.
  function columnGap(a, b) { return (a.w + b.w) / 2 + 30; }
  // Avståndet mellan mitten på två anrop under varandra, och var det översta anropet står uppifrån och ned.
  var CALL_GAP = 42, CALL_TOP = 55;

  // En placeringsregel för fcose: a före b längs axeln (x: till vänster om, y: ovanför), mitt till mitt minst gap.
  function before(a, b, axis, gap) {
    return axis === 'x' ? { left: a, right: b, gap: gap } : { top: a, bottom: b, gap: gap };
  }

  // Placeringen av det som syns. layers: lagernummer -> noderna i lagret. info: id -> { w, h } (nodens storlek). parentOf: id -> id på
  // ytan noden ligger i. pads: id -> { x, y } (ytans kant i sidled och höjdled). edges: pilarna (a, b). Ger:
  //   calls, integrations: id:na i bästa ordning; rules, anchors: reglerna för fcose; seed: id -> startplats för varje nod.
  function arrange(m) {
    var lr = m.lr, flow = lr ? 'x' : 'y', side = lr ? 'y' : 'x';
    var layerNos = Object.keys(m.layers).map(Number).sort(function (x, y) { return x - y; });
    var leaves = [], layerOf = {}, nb = {};
    layerNos.forEach(function (c) { m.layers[c].forEach(function (n) { leaves.push(n.id); layerOf[n.id] = c; nb[n.id] = []; }); });
    m.edges.forEach(function (e) { if (nb[e.a] && nb[e.b]) { nb[e.a].push(e.b); nb[e.b].push(e.a); } });
    // chains: ytorna en nod ligger i, den yttersta först. under: noderna under varje yta (och under varje nod: den själv).
    var chains = {}, under = {};
    leaves.forEach(function (id) {
      var c = [];
      for (var p = m.parentOf[id]; p; p = m.parentOf[p]) c.unshift(p);
      chains[id] = c;
      (under[id] = under[id] || []).push(id);
      c.forEach(function (a) { (under[a] = under[a] || []).push(id); });
    });
    var size = function (id, axis) { return axis === 'x' ? m.info[id].w : m.info[id].h; };
    var padOf = function (id, axis) { return m.pads[id] ? m.pads[id][axis] : 0; };
    // Summan av ytornas kant i kedjan, från och med plats from.
    var padSum = function (chain, axis, from) {
      var sum = 0;
      for (var i = from || 0; i < chain.length; i++) sum += padOf(chain[i], axis);
      return sum;
    };
    var spread = function (i, n) { return (i + 0.5) / n; };

    // Ordningen inom lagren: genomsnitt av grannarnas plats (barycenter), fram och tillbaka. Ytor hålls samman: delarna i en yta
    // står efter varandra, och ytorna ordnas efter delarnas genomsnitt.
    var rank = {}, value = {}, best = null, bestCross = Infinity;
    var sortLayer = function (ids) {
      var groupOf = function (id, depth) { return depth < chains[id].length ? chains[id][depth] : id; };
      var sortGroup = function (list, depth) {
        var groups = [], index = {};
        list.forEach(function (id) {
          var g = groupOf(id, depth);
          if (!(g in index)) { index[g] = groups.length; groups.push({ id: g, ids: [], sum: 0 }); }
          var grp = groups[index[g]]; grp.ids.push(id); grp.sum += value[id];
        });
        groups.forEach(function (g, i) { g.key = g.sum / g.ids.length; g.at = i; });
        groups.sort(function (a, b) { return a.key - b.key || a.at - b.at; });
        var out = [];
        // Anropen i en grupp står i fast ordning; allt annat ordnas efter grannarna.
        var inOrder = function (ids) { return ids.slice().sort(function (a, b) { return m.order[a] - m.order[b]; }); };
        groups.forEach(function (g) {
          (g.ids.length === 1 && g.id === g.ids[0] ? [g.id] : m.fixed[g.id] ? inOrder(g.ids) : sortGroup(g.ids, depth + 1)).forEach(function (id) { out.push(id); });
        });
        return out;
      };
      return sortGroup(ids, 0);
    };
    var inLayer = {};
    layerNos.forEach(function (c) { inLayer[c] = m.layers[c].map(function (n) { return n.id; }); });
    var assign = function (c, ids) {
      inLayer[c] = ids;
      ids.forEach(function (id, i) { rank[id] = i; value[id] = spread(i, ids.length); });
    };
    layerNos.forEach(function (c) { inLayer[c].forEach(function (id, i) { value[id] = spread(i, inLayer[c].length); }); assign(c, sortLayer(inLayer[c])); });
    // Pilarna mellan lager, grupperade per lagerpar (från det tidigare lagret till det senare): bara pilar i samma grupp kan korsa.
    var groups = {};
    m.edges.forEach(function (e) {
      if (!nb[e.a] || !nb[e.b] || layerOf[e.a] === layerOf[e.b]) return;
      var from = layerOf[e.a] < layerOf[e.b] ? e.a : e.b, to = from === e.a ? e.b : e.a, key = layerOf[from] + '>' + layerOf[to];
      (groups[key] = groups[key] || []).push([from, to]);
    });
    var crossings = function () {
      var x = 0;
      Object.keys(groups).forEach(function (key) {
        var list = groups[key];
        for (var i = 0; i < list.length; i++) for (var j = i + 1; j < list.length; j++) {
          var p = list[i], q = list[j];
          if (p[0] === q[0] || p[1] === q[1]) continue;
          if ((rank[p[0]] - rank[q[0]]) * (rank[p[1]] - rank[q[1]]) < 0) x++;
        }
      });
      return x;
    };
    var keep = function () {
      var c = crossings();
      if (c < bestCross) { bestCross = c; best = {}; leaves.forEach(function (id) { best[id] = rank[id]; }); }
    };
    keep();
    for (var round = 0; round < 8 && bestCross > 0; round++) {
      for (var pass = 0; pass < 2; pass++) {
        var seq = pass === 0 ? layerNos : layerNos.slice().reverse();
        seq.forEach(function (c) {
          inLayer[c].forEach(function (id) {
            var near = nb[id].filter(function (o) { return pass === 0 ? layerOf[o] < c : layerOf[o] > c; });
            if (!near.length) near = nb[id].filter(function (o) { return layerOf[o] !== c; });
            if (near.length) value[id] = near.reduce(function (s, o) { return s + value[o]; }, 0) / near.length;
          });
          assign(c, sortLayer(inLayer[c]));
        });
        keep();
      }
    }
    layerNos.forEach(function (c) {
      var ids = m.layers[c].map(function (n) { return n.id; });
      ids.sort(function (a, b) { return best[a] - best[b]; });
      assign(c, ids);
    });
    var byRank = function (a, b) { return rank[a] - rank[b]; };

    // Avståndet mellan mitten på två noder som ligger efter varandra längs en axel: halva storlekarna, kanten på de ytor som
    // inte delas, och luft för pilen. Ytornas namn står ovanför ytan, vilket räknas in i höjdled.
    var apart = function (a, b, axis, air) {
      var ca = chains[a], cb = chains[b], k = 0;
      while (k < ca.length && k < cb.length && ca[k] === cb[k]) k++;
      return (size(a, axis) + size(b, axis)) / 2 + padSum(ca, axis, k) + padSum(cb, axis, k) + air;
    };
    var rules = [], anchors = [];

    // Lagren: varje nod i ett lager före varje nod i nästa. Blir det för många par (stora system) håller i stället en osynlig
    // ankarnod mellan lagren isär dem, så att antalet regler växer med antalet noder.
    var pairs = 0, air = lr ? 40 : 36;
    for (var p = 0; p + 1 < layerNos.length; p++) pairs += inLayer[layerNos[p]].length * inLayer[layerNos[p + 1]].length;
    for (var i = 0; i + 1 < layerNos.length; i++) {
      var here = inLayer[layerNos[i]], next = inLayer[layerNos[i + 1]];
      if (pairs <= 1500) {
        here.forEach(function (a) { next.forEach(function (b) { rules.push(before(a, b, flow, apart(a, b, flow, air))); }); });
        continue;
      }
      var anchor = 'anchor:' + i;
      anchors.push({ group: 'nodes', data: { id: anchor }, classes: 'anchor' });
      here.forEach(function (a) { rules.push(before(a, anchor, flow, size(a, flow) / 2 + padSum(chains[a], flow) + air / 2)); });
      next.forEach(function (b) { rules.push(before(anchor, b, flow, size(b, flow) / 2 + padSum(chains[b], flow) + air / 2)); });
    }

    // Ordningen inom ett lager hålls: varje nod före nästa i sidled, men bara så att mitterna skiljer sig åt. Noderna får alltså
    // stå förskjutna i flödets riktning (fcose knuffar isär dem), så att bilden förblir ett nätverk och inte en kolumn.
    layerNos.forEach(function (c) {
      var ids = inLayer[c].slice().sort(byRank);
      if (!ids.length || !state.nodes[ids[0]] || ['entry', 'integration', 'operation'].indexOf(state.nodes[ids[0]].kind) >= 0) return;
      for (var q = 0; q + 1 < ids.length; q++) rules.push(before(ids[q], ids[q + 1], side, (size(ids[q], side) + size(ids[q + 1], side)) / 2 + 8));
    });

    // Ytor bredvid varandra i samma lager (klasser med metoder, integrationer med rader): en osynlig ankarnod mellan två grannar
    // håller isär dem, i den ordning som ger färst korsningar.
    var kids = {};
    Object.keys(m.parentOf).forEach(function (id) { var p = m.parentOf[id] || ''; (kids[p] = kids[p] || []).push(id); });
    var rows = function (id) { var n = state.nodes[id]; return !!n && (n.kind === 'method' || n.kind === 'operation'); };
    Object.keys(kids).forEach(function (parent) {
      if (parent === 'system') return;
      var byLayer = {};
      kids[parent].forEach(function (id) {
        var ls = under[id];
        if (!ls) return;
        (byLayer[layerOf[ls[0]]] = byLayer[layerOf[ls[0]]] || []).push({ id: id, leaves: ls, single: ls.length === 1 && ls[0] === id,
          at: ls.reduce(function (s, l) { return s + rank[l]; }, 0) / ls.length });
      });
      Object.keys(byLayer).forEach(function (c) {
        var items = byLayer[c].sort(function (a, b) { return a.at - b.at; });
        for (var q = 0; q + 1 < items.length; q++) {
          var a = items[q], b = items[q + 1];
          if (a.single && b.single) {
            // Två noder: repulsionen räcker, utom för rader (metoder, operationer) som står efter varandra som en lista.
            if (rows(a.id) && rows(b.id)) rules.push(before(a.id, b.id, side, (size(a.id, side) + size(b.id, side)) / 2 + 10));
            continue;
          }
          var gapId = 'apart:' + parent + ':' + c + ':' + q;
          anchors.push({ group: 'nodes', data: { id: gapId }, classes: 'anchor' });
          var half = function (l) { return size(l, side) / 2 + padSum(chains[l], side, chains[l].indexOf(parent) + 1) + 8; };
          a.leaves.forEach(function (l) { rules.push(before(l, gapId, side, half(l))); });
          b.leaves.forEach(function (l) { rules.push(before(gapId, l, side, half(l))); });
        }
      });
    });

    // Startplatsen: lagren efter varandra i flödets riktning, noderna i varje lager i sin ordning och med ytorna samlade.
    // Varje nod börjar så nära snittet av sina grannar i tidigare lager som det finns plats för, så att pilarna går så rakt som
    // möjligt. Ett långt lager bryts i flera rader (kolumner), så att kartan inte blir bara en smal remsa.
    var seed = {}, cross = {}, cap = lr ? 520 : 1100, edge = 0;
    layerNos.forEach(function (c, li) {
      var ids = inLayer[c].slice().sort(byRank), lines = [[]], used = 0, last = null;
      if (!lr && !li && m.calls.indexOf(ids[0]) >= 0) {
        // Uppifrån och ned: anropen i kolumner, en per ruta, med anropen under varandra (samma mått som reglerna).
        var cols = columns(ids, m.groupOf, m.info, m.pads), x = 0, rows = 0;
        cols.forEach(function (u, i) {
          if (i) x += columnGap(cols[i - 1], u);
          u.calls.forEach(function (id, r) { cross[id] = x; seed[id] = { x: x, y: CALL_TOP + r * CALL_GAP }; });
          rows = Math.max(rows, u.calls.length);
        });
        var mid = x / 2;
        Object.keys(seed).forEach(function (id) { seed[id].x -= mid; cross[id] -= mid; });
        edge = CALL_TOP + (rows - 1) * CALL_GAP + 31;
        return;
      }
      ids.forEach(function (id) {
        var s = size(id, side) + (last && chains[last].join('/') !== chains[id].join('/') ? 40 : 14);
        if (used + s > cap && lines[lines.length - 1].length) { lines.push([]); used = 0; s = size(id, side) + 14; }
        lines[lines.length - 1].push({ id: id, s: s }); used += s; last = id;
      });
      var ext = Math.max.apply(null, ids.map(function (id) { return size(id, flow); })), span = lines.length * (ext + 30) - 30;
      var start = li ? edge + 90 : 0;
      lines.forEach(function (line, ri) {
        var f = start + ext / 2 + ri * (ext + 30), at = placeLine(line.map(function (e) { return e.id; }), line.map(function (e) { return e.s; }));
        line.forEach(function (e, i) { cross[e.id] = at[i]; seed[e.id] = lr ? { x: f, y: at[i] } : { x: at[i], y: f }; });
      });
      edge = start + span;
      function placeLine(list, extent) {
        // Önskad plats: snittet av grannarna i tidigare lager; saknas det fylls luckorna efter grannen före (eller efter).
        var cum = [], t = 0, want = list.map(function (id) {
          var near = nb[id].filter(function (o) { return layerOf[o] < c && o in cross; });
          return near.length ? near.reduce(function (sum, o) { return sum + cross[o]; }, 0) / near.length : null;
        });
        extent.forEach(function (e, i) { t += i ? (extent[i - 1] + e) / 2 : 0; cum.push(t); });
        var first = want.findIndex(function (w) { return w !== null; });
        if (first < 0) want = cum.map(function (v) { return v - t / 2; });
        else want = want.map(function (w, i) {
          if (w !== null) return w;
          var j = i; while (j >= 0 && want[j] === null) j--;
          if (j >= 0) return want[j] + cum[i] - cum[j];
          return want[first] - (cum[first] - cum[i]);
        });
        // Närmast önskad plats utan att ordningen eller avstånden bryts (isotonisk regression på platserna minus avstånden).
        var blocks = [];
        want.forEach(function (w, i) {
          blocks.push({ sum: w - cum[i], n: 1 });
          while (blocks.length > 1 && blocks[blocks.length - 2].sum / blocks[blocks.length - 2].n >= blocks[blocks.length - 1].sum / blocks[blocks.length - 1].n) {
            var top = blocks.pop(); blocks[blocks.length - 1].sum += top.sum; blocks[blocks.length - 1].n += top.n;
          }
        });
        var out = [];
        blocks.forEach(function (bl) { for (var k = 0; k < bl.n; k++) out.push(bl.sum / bl.n + cum[out.length]); });
        return out;
      }
    });

    var entries = m.calls.filter(function (id) { return id in rank; }).sort(byRank);
    var integrations = leaves.filter(function (l) { return state.nodes[l] && state.nodes[l].kind === 'integration'; }).sort(byRank);
    return { rules: rules, anchors: anchors, seed: seed, rank: rank, calls: entries.length === m.calls.length ? entries : m.calls, integrations: integrations };
  }

  // Kartans stil i ett tema (t ur THEMES). Färgen på varje nod och pil står i dess data (color, icon, badge) och byts av recolor.
  function cyStyle(t) {
    var f = t.fill;
    return [
    { selector: 'node', style: { label: 'data(label)', 'font-family': FONT, 'font-size': 14, color: t.fg, 'text-wrap': 'wrap',
      'text-max-width': 150, 'line-height': 1.25, 'min-zoomed-font-size': 6 } },
    // Anrop in: avlånga runda noder med ikonen till vänster och namnet inuti.
    { selector: 'node.long', style: { shape: 'round-rectangle', 'corner-radius': 17, width: 'data(w)', height: 34, 'background-color': f.node,
      'border-width': 2, 'border-color': 'data(color)', 'background-image': 'data(icon)', 'background-width': 18, 'background-height': 18,
      'background-position-x': 12, 'background-position-y': '50%', 'text-valign': 'center', 'text-halign': 'center', 'text-margin-x': 12,
      'text-max-width': 400 } },
    // Hopfällt projekt: som den utfällda projektrutan i liten, men täckande så att pilarna inte syns genom texten.
    { selector: 'node.box', style: { shape: 'round-rectangle', 'corner-radius': 24, width: 'data(w)', height: 84, 'background-color': f.box,
      'background-opacity': 1, 'border-width': 1.5, 'border-color': f.boxLine, 'font-weight': 600, 'line-height': 1.5, 'text-wrap': 'wrap',
      'text-valign': 'center', 'text-halign': 'center', 'text-max-width': 400, 'background-image': 'data(icon)', 'background-width': 18,
      'background-height': 18, 'background-position-x': 16, 'background-position-y': 14, 'background-clip': 'node' } },
    // Hopfälld klass: en liten ruta som den utfällda klassrutan, med namnet och antalet funktioner, kant i klassens färg.
    { selector: 'node.cbox', style: { shape: 'round-rectangle', 'corner-radius': 12, width: 'data(w)', height: 50, 'background-color': f.node,
      'background-opacity': 1, 'border-width': 1.5, 'border-color': 'data(color)', 'font-weight': 600, 'line-height': 1.45, 'text-valign': 'center',
      'text-halign': 'center', 'text-margin-x': 8, 'text-max-width': 400, 'background-image': 'data(icon)', 'background-width': 16,
      'background-height': 16, 'background-position-x': 10, 'background-position-y': 9, 'background-clip': 'node' } },
    { selector: 'node.long.t-entry', style: { height: 30, 'corner-radius': 15, 'background-color': f.entry, 'font-size': 13 } },
    // Integrationer: stora runda ikonnoder med namnet under, på kartans bakgrund så att det går att läsa över pilarna.
    { selector: 'node.round', style: { shape: 'ellipse', width: 60, height: 60, 'background-color': f.round, 'border-width': 2.5,
      'border-color': 'data(color)', 'background-image': 'data(icon)', 'background-width': 28, 'background-height': 28, 'text-valign': 'bottom',
      'text-margin-y': 6, 'text-background-color': t.bg, 'text-background-opacity': 0.9, 'text-background-padding': 2,
      'text-background-shape': 'roundrectangle', 'font-weight': 600 } },
    // Brickan "+6" i övre högra hörnet på det som går att fälla ut.
    { selector: 'node.badge', style: {
      'background-image': function (n) { return [n.data('icon'), n.data('badge')]; },
      'background-width': function (n) { return [n.hasClass('round') ? 28 : 18, 'auto']; },
      'background-height': function (n) { return [n.hasClass('round') ? 28 : 18, 18]; },
      'background-position-x': function (n) { return [n.hasClass('round') ? '50%' : 12, '100%']; },
      'background-position-y': ['50%', '0%'], 'background-offset-x': [0, 8], 'background-offset-y': [0, -9],
      'background-clip': ['node', 'none'], 'background-image-containment': ['inside', 'over'], 'bounds-expansion': 12 } },
    // Rader inuti en utfälld del: metoder och det som görs mot en integration.
    { selector: 'node.pill', style: { shape: 'round-rectangle', width: 'data(w)', height: 26, 'background-color': f.node, 'border-width': 1.5,
      'border-color': 'data(color)', 'font-size': 13, 'text-valign': 'center', 'text-halign': 'center', 'text-max-width': 400 } },
    { selector: 'node.pill.with-icon', style: { 'background-image': 'data(icon)', 'background-width': 16, 'background-height': 16,
      'background-position-x': 8, 'background-position-y': 5, 'text-margin-x': 10 } },
    // Utfällda delar: mjuka ytor tonade av sin färg, med namnet ovanför.
    { selector: 'node.group', style: { shape: 'round-rectangle', 'corner-radius': 18, 'background-color': 'data(color)', 'background-opacity': f.group,
      'border-width': 1.5, 'border-color': 'data(color)', 'border-opacity': 0.45, padding: 12, 'text-valign': 'top', 'text-halign': 'center',
      'text-margin-y': -4, 'font-weight': 600, color: t.fg, 'compound-sizing-wrt-labels': 'include', 'background-image': 'none' } },
    { selector: 'node.t-project.group', style: { 'corner-radius': 24, 'background-opacity': f.group + 0.01 } },
    // En utfälld klass är fylld med nodens fyllning, så att funktionerna och pilarna inuti syns tydligt.
    { selector: 'node.t-class.group', style: { 'background-color': f.node, 'background-opacity': 1, padding: 10, 'corner-radius': 14, 'border-opacity': 0.8 } },
    { selector: 'node.t-integration.group', style: { 'background-opacity': f.group + 0.02, 'border-opacity': 0.55 } },
    // Systemgränsen: allt inom den är systemets egen kod.
    { selector: 'node.system', style: { shape: 'round-rectangle', 'corner-radius': 36, 'background-color': t.node.solution, 'background-opacity': 0.035,
      'border-width': 1.5, 'border-style': 'dashed', 'border-color': t.node.solution, padding: 18, 'text-valign': 'top', 'text-halign': 'center',
      'text-margin-y': -6, color: t.node.solution, 'font-size': 14, 'font-weight': 600, 'compound-sizing-wrt-labels': 'include' } },
    { selector: 'edge', style: { width: 1.6, 'curve-style': 'bezier', 'line-color': 'data(color)', 'target-arrow-color': 'data(color)',
      'target-arrow-shape': 'triangle', 'arrow-scale': 0.85, opacity: 0.75 } },
    // Det som ingen ingång når: dämpat och streckat (streck, inte bara blekare, så att det syns även utan att skilja på ljushet).
    { selector: 'node.unreached', style: { opacity: 0.6, 'border-style': 'dashed', 'background-color': f.entry } },
    { selector: 'node.group.unreached', style: { 'background-color': f.entry, 'background-opacity': 0.5 } },
    { selector: '.dimmed', style: { opacity: 0.18 } },
    { selector: 'edge.path', style: { width: 3, opacity: 1 } },
    { selector: 'node.long.path, node.box.path, node.cbox.path, node.round.path', style: { 'border-width': 3.5 } },
    // Pulsen längs vägen och grannarna när muspekaren står på en nod.
    { selector: '.pulse', style: { 'underlay-color': 'data(color)', 'underlay-opacity': 0.35, 'underlay-padding': 7, 'underlay-shape': 'ellipse' } },
    { selector: 'edge.pulse', style: { width: 6, 'underlay-padding': 5 } },
    { selector: '.faded', style: { opacity: 0.12 } },
    // Hjälpmedel för layouten som inte ska synas: fjädrarna mellan anropen in och ankarna mellan lagren.
    { selector: 'edge.band', style: { opacity: 0, events: 'no' } },
    { selector: 'node.anchor', style: { width: 1, height: 1, opacity: 0, events: 'no', label: '' } }
    ];
  }

  // fresh: lägg ut allt från början (första gången, ny riktning eller detaljnivå); annars flyttas det som redan står bara så
  // mycket som behövs (och då räcker en snabbare genomräkning). fcose animerar inte och passar inte in kartan: tighten flyttar ihop
  // anropen och integrationerna efteråt, och runLayout glider och passar in (så att inget hoppar). Etiketterna under integrationerna räknas inte in i storleken,
  // annars blir kartan glest utspridd. Uppifrån och ned får lagren breda ut sig mer (lägre gravitation), annars blir kartan
  // smal och hög.
  function fcose(g, fresh) {
    var lr = state.dir !== 'TB';
    return {
      name: 'fcose', quality: fresh ? 'proof' : 'default', randomize: false, animate: false, fit: false, padding: 30, nodeDimensionsIncludeLabels: false, packComponents: false, tile: false,
      // Ankarna mellan lagren ska bara hålla isär lagren, inte knuffa på noderna.
      nodeRepulsion: function (n) { return n.hasClass('anchor') ? 1 : lr ? 6000 : 9000; }, edgeElasticity: 0.45,
      // Anropen in står nära sin controller; övriga pilar får lite mer luft.
      idealEdgeLength: function (e) { return e.hasClass('band') ? e.data('len') : e.source().hasClass('t-entry') ? 25 : 40; },
      nestingFactor: 0.05,
      gravity: lr ? 0.3 : 0.15, gravityCompound: 1.0, gravityRangeCompound: 1, numIter: 2500, nodeSeparation: 45,
      relativePlacementConstraint: g.rules, alignmentConstraint: g.align || undefined
    };
  }
  // Efter layouten flyttas anropen och integrationerna närmare systemrutan, så att pilarna blir korta och kartan kan zoomas in mer.
  // Anropen (och anropsgrupperna) flyttas ihop i flödets riktning så att luften mellan dem och systemrutan blir TIGHT_AIR. Integrationerna
  // ställs i en tät kolumn intill systemrutans andra sida, med samma luft, i sin ordning tvärs flödet och centrerade mot systemrutan. Blir
  // kolumnen längre än systemrutan tvärs flödet bryts den i flera kolumner efter varandra: de bitar som har flest pilar står närmast.
  var TIGHT_AIR = 90;
  function tighten() {
    var cy = state.cy, sys = cy.getElementById('system');
    if (!sys.length) return;
    var lr = state.dir !== 'TB', across = lr ? 'y' : 'x', box = function (c, labels) { return c.boundingBox({ includeLabels: !!labels, includeOverlays: false }); };
    var shift = function (eles, dx, dy) {
      eles.forEach(function (n) { if (!n.isParent()) { var p = n.position(); n.position({ x: p.x + dx, y: p.y + dy }); } });
    };
    // Luften räknas från rutan; hur långt den är tvärs flödet räknas med namnet (som står ovanför rutan).
    var body = box(sys), start = lr ? body.x1 : body.y1, end = lr ? body.x2 : body.y2, whole = box(sys, true);
    var from = lr ? whole.y1 : whole.x1, to = lr ? whole.y2 : whole.x2;
    var calls = cy.nodes('.t-entry, .t-callgroup');
    if (calls.length) {
      var b = box(calls), d = (start - TIGHT_AIR) - (lr ? b.x2 : b.y2);
      shift(calls.union(calls.descendants()), lr ? d : 0, lr ? 0 : d);
    }
    // En enhet är en integration (en rund nod, eller en yta med rader när den är utfälld) med sina mått längs flödet (utan namnet) och
    // tvärs över det (med namnet under ikonen). Hälften av luften tvärs över flödet är den ordinarie (halva steget) om ytan inte kräver mer.
    var step = lr ? 112 : 130, gap = lr ? 70 : 60;
    var units = cy.nodes('.t-integration').map(function (u) {
      var all = u.union(u.descendants()), b0 = box(all), b1 = box(all, true), leaf = !u.isParent();
      var at = leaf ? u.position() : { x: (b0.x1 + b0.x2) / 2, y: (b0.y1 + b0.y2) / 2 }, half = step / 2;
      if (!leaf) half = Math.max(half, (lr ? b1.h : b1.w) / 2 + 8);
      return { all: all, at: at, along: leaf ? (lr ? u.width() : u.height()) : lr ? b0.w : b0.h, half: half, arrows: all.incomers('edge').not('.band').length };
    }).sort(function (a, c) { return a.at[across] - c.at[across]; });
    if (!units.length) return;
    var total = units.reduce(function (s, u) { return s + 2 * u.half; }, 0), cols = Math.max(1, Math.ceil(total / Math.max(to - from, step)));
    var parts = [], used = 0;
    for (var i = 0; i < cols; i++) parts.push({ units: [], arrows: 0, along: 0, length: 0, at: i });
    units.forEach(function (u) {
      var part = parts[Math.min(cols - 1, Math.floor((used + u.half) / (total / cols)))];
      part.units.push(u); part.arrows += u.arrows; part.along = Math.max(part.along, u.along); part.length += 2 * u.half; used += 2 * u.half;
    });
    parts = parts.filter(function (p) { return p.units.length; }).sort(function (p, q) { return q.arrows - p.arrows || p.at - q.at; });
    var near = end + TIGHT_AIR, mid = (from + to) / 2;
    parts.forEach(function (p) {
      var along = near + p.along / 2, cross = mid - p.length / 2;
      p.units.forEach(function (u) {
        cross += u.half;
        shift(u.all, lr ? along - u.at.x : cross - u.at.x, lr ? cross - u.at.y : along - u.at.y);
        cross += u.half;
      });
      near += p.along + gap;
    });
  }
  // fcose använder slumptal; med samma frö ger samma kod samma bild.
  function seeded(seed, fn) {
    var real = Math.random, a = seed;
    Math.random = function () {
      a = (a + 0x6D2B79F5) | 0;
      var t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
    try { fn(); } finally { Math.random = real; }
  }

  // Ritar om det som syns. Det som fanns kvar står kvar, det som är nytt börjar där dess förälder (eller hopfällda del) stod,
  // och fcose flyttar sedan allt mjukt till sin nya plats. fresh (ny riktning eller detaljnivå): lägg ut allt på nytt, men
  // låt det ändå glida dit från där det står. Första gången läggs allt ut från början.
  function render(after, fresh) {
    var view = $('map-view'), cy = state.cy;
    if (!cy) {
      cy = state.cy = cytoscape({ container: view, style: cyStyle(palette()), elements: [], minZoom: 0.2, maxZoom: 4,
        userZoomingEnabled: false, boxSelectionEnabled: false, autounselectify: true });
      cy.on('tap', 'node', function (ev) { tapNode(ev.target.id()); });
      cy.on('tap', function (ev) { if (ev.target === cy) hideCard(); });
      cy.on('mouseover', 'node', function (ev) { view.style.cursor = 'pointer'; view.title = ev.target.hasClass('unreached') ? 'Nås inte från någon ingång' : ''; hover(ev.target); });
      cy.on('mouseout', 'node', function () { view.style.cursor = ''; view.title = ''; hover(null); });
      cy.on('viewport', placeCard);
      cy.on('viewport', updateZoom);
      cy.on('position', 'node', placeCard);
      // Rullhjulet zoomar bara med Ctrl (⌘ på Mac); annars rullar sidan som vanligt.
      view.addEventListener('wheel', function (ev) { cy.userZoomingEnabled(ev.ctrlKey || ev.metaKey); }, true);
      // Nyp med två fingrar zoomar: Cytoscape styr nyp med samma userZoomingEnabled, så det slås på bara medan två fingrar rör skärmen.
      view.addEventListener('touchstart', function (ev) { if (ev.touches.length >= 2) cy.userZoomingEnabled(true); }, true);
      var pinchOff = function (ev) { if (ev.touches.length < 2) cy.userZoomingEnabled(false); };
      view.addEventListener('touchend', pinchOff, true);
      view.addEventListener('touchcancel', pinchOff, true);
      updateZoom();
      if (window.ResizeObserver) new ResizeObserver(function () {
        cy.resize();
        placeStatus();
        // Ritades kartan medan den saknade storlek (dold flik) passas den in nu, när den har fått en.
        if (state.unfitted && view.clientWidth && view.clientHeight && view.getAttribute('data-ready')) { state.unfitted = false; fitView(); }
      }).observe(view);
    }
    cy.resize();
    var before = {}, first = cy.nodes().length === 0;
    cy.nodes().forEach(function (n) { before[n.id()] = n.position(); });
    var g = graphElements();
    cy.elements().stop(true);   // se runLayout
    cy.batch(function () {
      cy.elements().remove();
      cy.add(g.elements);
      var useSeed = first || !!fresh;   // första gången och efter nytt val av riktning eller nivå börjar noderna på den ordnade startplatsen
      var born = {};
      cy.nodes().forEach(function (n) {
        if (n.isParent()) return;
        var id = n.id(), o = (useSeed ? g.seed[id] : before[id]) || g.seed[id] || { x: 0, y: 0 };
        if (!useSeed && !before[id] && g.seed[id]) {
          var from = startOf(id, before);
          if (from) { (born[from.at] = born[from.at] || []).push({ id: id, x: from.x, y: from.y }); return; }
        }
        n.position(o);
      });
      // Nya delar börjar där delen de kommer ur stod, men i sin inbördes ordning i stället för på en hög.
      Object.keys(born).forEach(function (at) {
        var list = born[at], mx = 0, my = 0;
        list.forEach(function (b) { mx += g.seed[b.id].x / list.length; my += g.seed[b.id].y / list.length; });
        list.forEach(function (b) { cy.getElementById(b.id).position({ x: b.x + g.seed[b.id].x - mx, y: b.y + g.seed[b.id].y - my }); });
      });
    });
    runLayout(g, after, first || !!fresh, first, first ? null : before);
  }
  // Var en ny nod börjar: där närmaste förälder eller hopfälld del stod (at: vilken del), eller mitt bland delarna den ersätter.
  function startOf(id, before) {
    // Anrop som kommer ur en grupp (hopfälld eller utfälld) börjar där gruppen stod, och en grupp som fälls ihop börjar mitt bland sina anrop.
    var box = state.callBox[id];
    if (box && before[box]) return { x: before[box].x, y: before[box].y, at: box };
    for (var n = state.nodes[id]; n; n = n.parent ? state.nodes[n.parent] : null)
      if (before[n.id]) return { x: before[n.id].x, y: before[n.id].y, at: n.id };
    var sum = { x: 0, y: 0 }, count = 0, stack = (state.kids[id] || state.callKids[id] || []).slice();
    while (stack.length) {
      var k = stack.pop();
      if (before[k]) { sum.x += before[k].x; sum.y += before[k].y; count++; } else stack = stack.concat(state.kids[k] || []);
    }
    return count ? { x: sum.x / count, y: sum.y / count, at: '*' + id } : null;
  }
  // before: var noderna stod innan (de glider dit de ska från där de stod), null första gången. fit: passa in kartan direkt.
  function runLayout(g, after, fresh, fit, before) {
    var view = $('map-view'), cy = state.cy;
    if (state.layout) state.layout.stop();
    view.removeAttribute('data-ready');
    var layout = state.layout = cy.layout(fcose(g, fresh));
    var done = function () {
      if (state.layout !== layout) return;
      state.unfitted = !view.clientWidth || !view.clientHeight;
      view.setAttribute('data-ready', 'true');
      if (after) after();
    };
    layout.one('layoutstop', function () {
      if (state.layout !== layout) return;
      tighten();
      if (fit) fitView(null, true);
      if (!before || !motion()) return done();
      // Utläggningen är räknad på en gång; noderna går mjukt dit från där de stod.
      var end = {}, moving = [];
      cy.nodes().forEach(function (n) {
        if (n.isParent()) return;
        var id = n.id(), from = before[id] || startOf(id, before);
        end[id] = { x: n.position().x, y: n.position().y };
        if (from) { n.position({ x: from.x, y: from.y }); moving.push(n); }
      });
      var left = moving.length;
      if (!left) return done();
      moving.forEach(function (n) {
        n.animate({ position: end[n.id()] }, { duration: 450, easing: 'ease-in-out-cubic', complete: function () { n.position(end[n.id()]); if (--left === 0) done(); } });
      });
    });
    // Cytoscape stegar animationerna i en lista med element per id. Ett element som tas bort mitt i en animation ligger kvar
    // i listan till nästa steg, och ett nytt element med samma id kommer då inte med: dess animation står still och layouten
    // blir aldrig klar (t.ex. vid två snabba klick i verktygsfältet). Layouten startar därför efter nästa steg.
    cy.one('step', function () { if (state.layout === layout) seeded(42, function () { layout.run(); }); });
  }

  // Visar det som matchar (t.ex. '.path' eller '*'), mjukt om det går, och zoomar aldrig in mer än 120 %.
  function fitTo(selector, done, instant) {
    var cy = state.cy;
    if (!cy) return;
    var eles = cy.elements(selector).not('.anchor');
    if (!eles.length) eles = cy.elements().not('.anchor');
    // Statusrutan uppe till vänster får plats: kartan passas in under den.
    var status = $('map-status'), top = status.hidden ? 0 : status.offsetHeight + 20;
    var bb = eles.boundingBox(), w = cy.width() - 60, h = cy.height() - 40 - top;
    var zoom = Math.max(cy.minZoom(), Math.min(1.2, w / Math.max(1, bb.w), h / Math.max(1, bb.h)));
    var pan = { x: cy.width() / 2 - zoom * (bb.x1 + bb.w / 2), y: top + (cy.height() - top) / 2 - zoom * (bb.y1 + bb.h / 2) };
    if (motion() && !instant) cy.animate({ zoom: zoom, pan: pan }, { duration: 450, easing: 'ease-in-out-cubic', complete: done });
    else { cy.viewport({ zoom: zoom, pan: pan }); if (done) done(); }
  }
  // Zoomknapparna nere till höger, procenten och tangenterna + (=), - och 0 när fokus är i kartan eller på zoomknapparna.
  function initZoom(root) {
    var mac = /Mac|iPhone|iPad/i.test((navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '');
    var view = $('map-view'), box = $('map-zoom');
    state.zoomUi = { pct: $('map-zoom-pct'), zoomIn: $('map-zoom-in'), zoomOut: $('map-zoom-out'), text: null, atMax: null, atMin: null };
    $('map-zoom-hint').textContent = (mac ? '⌘' : 'Ctrl') + ' + rullhjul zoomar';
    state.zoomUi.zoomIn.addEventListener('click', function () { zoomBy(1.25); });
    state.zoomUi.zoomOut.addEventListener('click', function () { zoomBy(1 / 1.25); });
    $('map-zoom-fit').addEventListener('click', function () { fitView(); });
    root.addEventListener('keydown', function (ev) {
      if (ev.ctrlKey || ev.metaKey || ev.altKey || ev.defaultPrevented) return;
      var t = ev.target;
      if (t !== view && !box.contains(t)) return;
      if (ev.key === '+' || ev.key === '=') zoomBy(1.25);
      else if (ev.key === '-' || ev.key === '−') zoomBy(1 / 1.25);
      else if (ev.key === '0') fitView();
      else return;
      ev.preventDefault();
    });
  }
  // Zoomar med faktorn runt kartans mitt, mjukt om det går, inom minZoom och maxZoom.
  function zoomBy(factor) {
    var cy = state.cy;
    if (!cy) return;
    var from = cy.animated() && state.zoomGoal ? state.zoomGoal : cy.zoom();
    var to = Math.max(cy.minZoom(), Math.min(cy.maxZoom(), from * factor));
    if (Math.abs(to - from) < 1e-6) return;
    var center = { x: cy.width() / 2, y: cy.height() / 2 };
    if (motion()) {
      state.zoomGoal = to;
      cy.animate({ zoom: { level: to, renderedPosition: center } }, { duration: 200, easing: 'ease-in-out-cubic', complete: function () { state.zoomGoal = null; } });
    } else cy.zoom({ level: to, renderedPosition: center });
  }
  // Procenten och att knapparna är avstängda vid gränsen. Skriver bara det som ändrats, eftersom den körs vid varje steg i en zoom.
  function updateZoom() {
    var cy = state.cy, ui = state.zoomUi;
    if (!cy) return;
    var z = cy.zoom(), text = Math.round(z * 100) + ' %', atMax = z >= cy.maxZoom() - 1e-6, atMin = z <= cy.minZoom() + 1e-6;
    if (text !== ui.text) { ui.text = text; ui.pct.textContent = text; }
    if (atMax !== ui.atMax) { ui.atMax = atMax; ui.zoomIn.setAttribute('aria-disabled', atMax ? 'true' : 'false'); }
    if (atMin !== ui.atMin) { ui.atMin = atMin; ui.zoomOut.setAttribute('aria-disabled', atMin ? 'true' : 'false'); }
  }
  // Vägen för det valda anropet, annars hela kartan.
  function fitView(done, instant) { fitTo(state.picked.length ? '.path' : '*', done, instant); }

  // Muspekaren på en nod: noden och dess grannar syns tydligt, resten bleknar.
  function hover(node) {
    var id = node ? node.id() : null, cy = state.cy;
    if (id === state.hovered) return;
    state.hovered = id;
    cy.batch(function () {
      cy.elements('.faded').removeClass('faded');
      if (!node || id === 'system' || node.isParent()) return;
      // Pilarna från en ruta med anrop hör till alla anropen i den.
      var box = node.parent().filter('.t-callgroup'), lines = node.connectedEdges().union(box.connectedEdges()).not('.band');
      var near = node.union(lines).union(lines.connectedNodes()).union(lines.sources().filter('.t-callgroup').children());
      near = near.union(near.nodes().ancestors());
      cy.elements().not(near).addClass('faded');
    });
  }

  // Pulsen längs vägen: kanterna lyser upp i den ordning anropen görs (djupet först från anropet in), en gång.
  function pulse(key) {
    state.pulses.forEach(clearTimeout);
    state.pulses = [];
    if (!motion()) return;
    var cy = state.cy, order = [], seen = {}, done = {};
    var walk = function (from) {
      if (done[from]) return;
      done[from] = true;
      (state.out[from] || []).forEach(function (k) {
        if (k.entries.indexOf(key) < 0) return;
        var id = 'k:' + visibleOf(k.from) + '>' + visibleOf(k.to);
        if (!seen[id] && cy.getElementById(id).length) { seen[id] = true; order.push(id); }
        walk(k.to);
      });
    };
    walk('in:' + key);
    order.forEach(function (id, i) {
      var edge = cy.getElementById(id), target = edge.target();
      state.pulses.push(setTimeout(function () { edge.addClass('pulse'); target.addClass('pulse'); }, i * 130));
      state.pulses.push(setTimeout(function () { edge.removeClass('pulse'); target.removeClass('pulse'); }, i * 130 + 420));
    });
  }

  function tapNode(id) {
    // En anropsgrupp: ett klick på det hopfällda anropet fäller ut den, ett klick på ytan (namnet eller kanten) fäller ihop den.
    if (id.indexOf('cg:') === 0) { toggleCalls(id); return; }
    var n = state.nodes[id];
    if (!n) return;
    // Ett anrop väljs genom att klicka på dess nod; ett klick till på samma nod visar hela systemet igen.
    if (n.kind === 'entry') { var e = entryByKey(n.name); choose(e && state.entry !== e ? [e] : []); return; }
    if (state.kids[id]) { toggle(id); return; }
    showCard(id);
  }

  // "OrderService.PlaceAsync", "ShopDatabase: läsa Customers" eller "POST /orders". lookup: id → nod, från början det som syns.
  function nameOf(id, lookup) {
    lookup = lookup || state.nodes;
    var n = lookup[id];
    if (!n) return id;
    var parent = n.parent && lookup[n.parent];
    if (n.kind === 'method') return (parent ? parent.name + '.' : '') + n.name;
    if (n.kind === 'operation') return (parent ? parent.name + ': ' : '') + operationText(n.name, n.address);
    return n.name;
  }
  function where(file, line) { return file ? file + ', rad ' + line : ''; }
  function callersOf(id) {
    return (state.into[id] || []).map(function (k) { return nameOf(k.from) + (k.file ? ' (' + where(k.file, k.line) + ')' : ''); });
  }
  function calleesOf(id) { return (state.out[id] || []).map(function (k) { return nameOf(k.to); }); }

  // Detaljkortet: flyter vid noden man klickat på och följer med när kartan flyttas. Stängs med ×, Esc eller klick bredvid.
  function showCard(id) {
    var box = $('map-card'), n = state.nodes[id];
    if (!n) return;
    var rows = [], title = nameOf(id), callers = callersOf(id), callees = calleesOf(id);
    if (n.kind === 'entry') {
      var e = entryByKey(n.name);
      if (e && e.operationId) title += ' (' + e.operationId + ')';
      if (e) rows.push([e.method ? 'Tas emot av' : 'Startas av', e.handler + ' i ' + e.project + ' (' + where(e.file, e.line) + ')']);
      if (!e || e.method) {
        rows.push(['Skickar', e && e.input ? shapeText(e.input) : 'inga uppgifter']);
        rows.push(['Svaret', e && e.output ? shapeText(e.output) : 'inget innehåll']);
      } else if (e.input) rows.push(['Tar emot', shapeText(e.input)]);
    } else if (n.kind === 'method' || isUnreached(n)) {
      var cls = state.nodes[n.parent], proj = cls && state.nodes[cls.parent];
      if (proj) rows.push(['Del av systemet', proj.name]);
      rows.push(['Finns i', where(n.file, n.line)]);
      if (n['in']) rows.push(['Tar', shapeText(n['in'])]);
      if (n.out) rows.push(['Ger', shapeText(n.out)]);
      if (callers.length) rows.push(['Anropas från', callers.join(', ')]);
      if (callees.length) rows.push(['Anropar', callees.join(', ')]);
    } else if (n.kind === 'operation') {
      var it = integrationOf(state.nodes[n.parent].ref);
      rows.push(['Typ', it.detail || kindName(it.kind)]);
      if (n['in']) rows.push(['Skickar', shapeText(n['in'])]);
      if (n.out) rows.push(['Får', shapeText(n.out)]);
      rows.push(['Anropas från', callers.join(', ')]);
    }
    if (isUnreached(n)) rows.unshift(['Status', 'Nås inte från någon ingång']);
    else rows.push(['Används av anropen', n.entries.join(', ')]);
    box.textContent = '';
    var close = el('button', 'close', '×'); close.type = 'button'; close.setAttribute('aria-label', 'Stäng detaljerna');
    close.addEventListener('click', hideCard);
    box.appendChild(close);
    box.appendChild(el('h3', null, title));
    rows.forEach(function (r) {
      var p = el('p');
      p.appendChild(el('span', 'label', r[0] + ': '));
      p.appendChild(document.createTextNode(r[1]));
      box.appendChild(p);
    });
    state.card = id;
    box.hidden = false;
    placeCard();
  }
  function hideCard() { state.card = null; $('map-card').hidden = true; }
  function placeCard() {
    if (!state.card || !state.cy) return;
    var box = $('map-card'), node = state.cy.getElementById(state.card);
    if (!node.length) { hideCard(); return; }
    var view = $('map-view'), p = node.renderedPosition(), r = node.renderedOuterWidth() / 2 + 12;
    var x = p.x + r, y = p.y - 20;
    if (x + box.offsetWidth > view.clientWidth - 8) x = p.x - r - box.offsetWidth;
    x = Math.max(8, Math.min(x, view.clientWidth - box.offsetWidth - 8));
    y = Math.max(8, Math.min(y, view.clientHeight - box.offsetHeight - 8));
    // Zoomrutan nere till höger ska inte täckas: kortet flyttas då upp ovanför den.
    var zoom = $('map-zoom'), zoomW = zoom.offsetWidth + 24, zoomH = zoom.offsetHeight + 24;
    if (x + box.offsetWidth > view.clientWidth - zoomW && y + box.offsetHeight > view.clientHeight - zoomH) y = Math.max(8, view.clientHeight - zoomH - box.offsetHeight);
    box.style.left = (x + view.offsetLeft) + 'px'; box.style.top = (y + view.offsetTop) + 'px';   // kortet ligger i map-wrap, under verktygsraden
  }

  // Samma innehåll som kartan, som text.
  function stepsList(steps) {
    var ol = el('ol', 'map-steps');
    if (!steps.length) { ol.appendChild(el('li', 'data', 'Inga anrop vidare.')); return ol; }
    steps.forEach(function (s) {
      var li = el('li'), data;
      if (s.integration) {
        li.className = 'external';
        li.appendChild(document.createTextNode('Ut ur systemet: ' + integrationLabel(integrationOf(s.integration.id)) + ' – ' + operationText(s.integration.operation, s.integration.address)));
        data = dataText(s, 'skickar', 'får');
        if (data) li.appendChild(el('span', 'data', ', ' + data));
      } else {
        li.appendChild(document.createTextNode(s.member + ' i ' + s.project));
        data = dataText(s, 'tar', 'ger');
        if (data) li.appendChild(el('span', 'data', ', ' + data));
        if (s.steps.length) li.appendChild(stepsList(s.steps));
      }
      ol.appendChild(li);
    });
    return ol;
  }
  // groups, hide, dropUnreached, entryFor, cy och steps används av testerna.
  window.SystemMap = { create: create, summary: summary, groups: callGroups, hide: hideParts, dropUnreached: dropUnreached, entryFor: entryFor, cy: function () { return state.cy; }, steps: function () { return state.steps; } };
})();
