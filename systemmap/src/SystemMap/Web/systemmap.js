// Systemkartan: ett nätverk ritat med Cytoscape.js och layouten fcose. Projekten är mjuka områden med klasserna i,
// integrationerna är ikonnoder utanför systemet. Blått = systemets egen kod, grönt = utanför systemet, grått = anropen in.
// Typen står alltid i text och som ikon, aldrig bara som färg.
//
// Används av den fristående sidan (page.html) och av sidor som bäddar in kartan. En karta per sida:
//   var map = SystemMap.create(root, { mermaid: function (level, direction, entry) { return Promise<text>; }, ... });
//   map.show(json);   // kartan som JSON (CodeMapJson)
// Inställningar (alla valfria):
//   mermaid(level, direction, entry): texten till "Exportera som Mermaid" i verktygsraden. Utan den syns inte knappen. Ett fel ges som
//     { message, tip }.
//   onError(error): när Mermaid-filen inte gick att hämta. Utan den står felet bredvid knappen.
//   hidden: id:n på delar som är avbockade i "Visa" när kartan öppnas första gången (det man själv bockat av sparas och gäller före).
//   unreached: false = "Visa delar som inte nås" är avbockad när kartan öppnas första gången (annars ibockad; det man själv väljer sparas och gäller före).
//   mermaid får som fjärde värde id:n på det som är dolt och som femte om delar som inte nås visas, så att filen kan följa kartan.
// Kartan fyller ytan sidan ger den: sidan sätter höjden med --map-height (annars hela fönstret). Anrop, riktning och nivå
// väljs i raden ovanför kartan, där också exporten finns. Ett anrop går också att välja genom att klicka på dess nod.
// "Visa" i raden väljer vilka projekt, klasser och integrationer som syns, och om det som ingen ingång når (delar med reached: false, ritade
// dämpat och streckat) syns. SystemMap.hide(overview, ids) är regeln för det som döljs; SystemMap.dropUnreached(overview) tar bort det som inte nås.
// SystemMap.summary(json) ger "4 anrop, 3 delar av systemet och 6 integrationer.".
(function () {
  'use strict';

  // Integrationernas typer: [namn, kort namn under ikonen (där ikonen redan visar att det är ett annat system)].
  var KINDS = { database: ['Databas', 'Databas'], queue: ['Kö', 'Kö'], http: ['Annat system (HTTP)', 'HTTP'], wcf: ['Annat system (WCF/SOAP)', 'WCF/SOAP'],
    storage: ['Lagring', 'Lagring'], email: ['E-post', 'E-post'] };
  var COLORS = { code: '#2a78d6', external: '#008300', entry: '#6b7a8f' };
  // Riktnings- och nivåknapparna i verktygsraden.
  var TOOLS = '.map-bar [data-level], .map-bar [data-direction]';
  var FONT = 'system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';
  var MARKUP =
    '<div class="map-wrap" id="map-wrap">' +
    '  <div class="map-bar" id="map-bar">' +
    '    <span class="map-field"><label class="map-label" for="map-entry">Anrop</label><select id="map-entry" class="map-entry"></select></span>' +
    '    <span class="map-field"><span class="map-label" aria-hidden="true">Riktning</span>' +
    '      <span class="segmented" role="group" aria-label="Riktning"><button type="button" data-direction="LR" title="Anropen går från vänster till höger">→</button><button type="button" data-direction="TB" title="Anropen går uppifrån och ned">↓</button></span></span>' +
    '    <span class="map-field"><span class="map-label" aria-hidden="true">Nivå</span>' +
    '      <span class="segmented" role="group" aria-label="Nivå"><button type="button" data-level="projects" title="Bara projekten och integrationerna">Projekt</button><button type="button" data-level="classes" title="Klasserna i varje projekt">Klasser</button><button type="button" data-level="methods" title="Metoderna i varje klass. Läsningar, skrivningar och meddelanden ligger kvar i integrationen.">Funktioner</button><button type="button" data-level="all" title="Allt: även läsningarna, skrivningarna och meddelandena i integrationerna">Allt</button></span></span>' +
    '    <details class="map-extra map-show" id="map-show" name="map-extra" hidden><summary id="map-show-summary" title="Välj vad som syns på kartan">Visa</summary>' +
    '      <div class="map-panel" id="map-show-panel" role="group" aria-labelledby="map-show-title"><h3 id="map-show-title">Välj vad som syns</h3>' +
    '        <p class="map-panel-help">Avbockade delar döljs med det som ligger i dem. Vägarna genom dem finns kvar.</p>' +
    '        <button type="button" id="map-show-all">Visa allt</button>' +
    '        <label class="map-unreached" id="map-unreached-field" hidden><input type="checkbox" id="map-unreached"><span>Visa delar som inte nås</span></label>' +
    '        <div id="map-tree"></div></div></details>' +
    '    <span class="map-hint" id="map-hint" hidden></span>' +
    '    <span id="map-export"><button type="button" id="map-mermaid" aria-label="Exportera som Mermaid" title="Exportera som Mermaid. Ett flödesschema att lägga i dokumentation. Filen följer riktningen, nivån och anropet du valt."><svg width="20" height="20" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9 2.5 V11 M5.5 7.5 L9 11 L12.5 7.5"/><path d="M2.5 11.5 V15 H15.5 V11.5"/></svg></button>' +
    '      <span class="map-sr" id="map-mermaid-status"></span></span>' +
    '    <details class="map-extra" id="map-diff" name="map-extra" hidden><summary id="map-diff-summary" title="Anrop som bara finns i koden eller bara i API-beskrivningen"></summary><ul id="map-diff-list"></ul></details>' +
    '    <details class="map-extra" id="map-warnings" name="map-extra" hidden><summary id="map-warnings-summary"></summary><ul id="map-warnings-list"></ul></details>' +
    '  </div>' +
    '  <div class="map-view" id="map-view"></div>' +
    '  <div class="map-legend" id="map-legend" role="note" hidden><span class="map-swatch" aria-hidden="true"></span>Nås inte från någon ingång</div>' +
    '  <div class="map-card" id="map-card" hidden></div>' +
    '</div>' +
    '<div class="map-sr" id="map-live" role="status" aria-live="polite"></div>';

  // open: delar användaren fällt ut. shut: delar på det valda anropets väg som användaren fällt ihop. card: noden i detaljkortet.
  // dir: LR (vänster → höger) eller TB (uppifrån och ned). level: projects, classes, methods eller all (vad som är utfällt från början).
  // hidden: id → true för delarna användaren bockat av i "Visa". unreached: om delar som ingen ingång når syns. view: översikten utan det
  // dolda (hide) och, om unreached är falskt, utan det som inte nås (dropUnreached), som allt ritas ur.
  // out/into: pilarna från och till varje nod. visible: visibleOf för det som ritas just nu.
  var state = { map: null, allNodes: {}, anyUnreached: false, view: null, hidden: {}, unreached: true, groups: [], entry: null, cy: null, nodes: {}, kids: {}, out: {}, into: {}, visible: {}, open: null, shut: {},
    dir: stored('map-direction', ['LR', 'TB'], 'LR'), level: stored('map-level', ['projects', 'classes', 'methods', 'all'], 'classes'),
    layout: null, steps: {}, base: {}, last: 1, card: null, hovered: null, pulses: [], root: null, opts: {}, tools: [], unfitted: false };

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
  // "ShopDatabase (databas, EF Core och SQL)", som i terminalen.
  function integrationLabel(it) { return it.name + (it.detail ? ' (' + it.detail + ')' : ''); }
  // Operationen som den visas: verbet, och för ett HTTP-anrop vars adress inte går att läsa ur koden (address: 'runtime') en förklaring.
  // Samma text som CodeMapOverview.RuntimeAddress i C#.
  function operationText(operation, address) { return address === 'runtime' ? operation + ' (adressen bestäms vid körning)' : operation; }
  // Ingången på kartan för ett anrop (t.ex. i ett test): samma operation i API-beskrivningen, annars samma metod och sökväg.
  function entryFor(op, method, path) {
    if (!state.map) return null;
    var list = state.map.entryPoints;
    return (op && list.find(function (e) { return e.operationId === op; })) ||
      list.find(function (e) { return e.method === method && (pathKey(e.path) === pathKey(path) || pathKey(e.codePath) === pathKey(path)); }) || null;
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
    $('map-export').hidden = !state.opts.mermaid;
    $('map-mermaid').addEventListener('click', downloadMermaid);
    $('map-show-all').addEventListener('click', function () {
      var off = !state.unreached && state.anyUnreached;
      if (!hiddenIds().length && !off) return;
      if (off) chooseUnreached(true);
      setHidden({});
    });
    $('map-unreached').addEventListener('change', function () { setUnreached(this.checked); });
    $('map-tree').addEventListener('change', function (ev) {
      var id = ev.target.getAttribute('data-part');
      if (!id) return;
      var next = Object.assign({}, state.hidden);
      if (ev.target.checked) delete next[id]; else next[id] = true;
      setHidden(next);
    });
    // Esc stänger detaljkortet, och skillnader och varningar som fällts ut över kartan (fokus går då tillbaka till rubriken).
    document.addEventListener('keydown', function (ev) {
      if (ev.key !== 'Escape') return;
      hideCard();
      var open = root.querySelector('.map-extra[open]');
      if (open) { open.open = false; open.querySelector('summary').focus(); }
    });
    // Riktning och detaljnivå. Båda lägger ut kartan på nytt och sparas till nästa gång.
    state.tools = Array.prototype.slice.call(root.querySelectorAll(TOOLS));
    state.tools.forEach(function (b) {
      b.addEventListener('click', function () {
        if (b.hasAttribute('data-level')) { openLevel(b.getAttribute('data-level')); remember('map-level', state.level); }
        else { state.dir = b.getAttribute('data-direction'); remember('map-direction', state.dir); }
        syncTools(); hideCard();
        render(function () { fitView(); }, true);
      });
    });
    $('map-entry').addEventListener('change', function () {
      choose(this.value === '' ? null : state.map.entryPoints[Number(this.value)]);
    });
    return {
      show: renderMap,
      // Välj ett anrop (en ingång ur kartan, eller null för hela systemet). { focus: true } flyttar fokus till anropslistan.
      select: function (e, opts) {
        selectEntry(e);
        if (opts && opts.focus) $('map-entry').focus({ preventScroll: true });
      },
      entryFor: entryFor,
      // Stegen för ett anrop som en lista (för sidor som visar vägen som text).
      steps: function (e) { return stepsList(e.steps); },
      loaded: function () { return !!state.map; }
    };
  }

  // Kartan som Mermaid, att lägga i dokumentation: samma riktning, nivå och valda anrop som kartan visar.
  async function downloadMermaid() {
    var button = $('map-mermaid'), status = $('map-mermaid-status');
    if (!state.map || button.getAttribute('aria-disabled') === 'true') return;
    button.setAttribute('aria-disabled', 'true');
    status.textContent = '';
    try {
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
    buildTree();
    useView();
    if (!state.open) openLevel(state.level);
    syncTools();
    var list = $('map-entry'); list.textContent = '';
    var all = el('option', null, 'Hela systemet'); all.value = ''; list.appendChild(all);
    state.groups = callGroups(map.entryPoints);
    state.groups.forEach(function (g) {
      var into = g.label ? list.appendChild(el('optgroup')) : list;
      if (g.label) into.label = g.label;
      g.entries.forEach(function (e) {
        var o = el('option', null, entryKey(e)); o.value = String(map.entryPoints.indexOf(e)); into.appendChild(o);
      });
    });
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
    if (!some) { if (state.cy) state.cy.elements().remove(); return; }
    var keep = state.entry && entryFor(state.entry.operationId, state.entry.method, state.entry.path);
    state.shut = {};
    if (state.cy) state.cy.elements().remove();       // ny karta: lägg ut allt från början
    selectEntry(keep || null);
  }

  // ---- Visa: välj vad som syns ----
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
  // Delar som ingen ingång når (projekt, klasser och metoder med reached: false). Finns inga syns inte valet i "Visa".
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
  // Valet sparas till nästa gång; ritas om gör den som väljer.
  function chooseUnreached(on) {
    state.unreached = on;
    remember(unreachedKey(state.map), on ? 'show' : 'hide');
  }
  function setUnreached(on) {
    chooseUnreached(on);
    useView();
    syncTree();
    hideCard();
    announce(on ? 'Delar som inte nås visas.' : 'Delar som inte nås är dolda.');
    if (state.cy && state.map.entryPoints.length) render(function () { fitView(); }, true);
  }
  // Kartan utan de dolda delarna, så att allt annat ritas ur state.view och bygger sina uppslag ur den.
  function useView() {
    state.view = hideParts(state.map.overview, hiddenIds());
    if (!state.unreached) state.view = dropUnreached(state.view);
    $('map-legend').hidden = !state.view.nodes.some(isUnreached);
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
  // Det användaren valt: sparas, och kartan ritas om utan de dolda delarna.
  function setHidden(next) {
    state.hidden = next;
    remember(hiddenKey(state.map), JSON.stringify(hiddenIds()));
    useView();
    syncTree();
    hideCard();
    var n = hiddenIds().length;
    announce(n ? n + (n === 1 ? ' del är dold.' : ' delar är dolda.') : 'Alla delar visas.');
    if (state.cy && state.map.entryPoints.length) render(function () { fitView(); }, true);
  }
  // Bockarna: projekten med sina klasser, sedan integrationerna. Byggs om när en karta visas; syncTree sätter bockarna.
  function buildTree() {
    var nodes = state.map.overview.nodes, box = $('map-tree'), item = function (n, label) {
      var li = el('li'), name = el('label'), input = el('input');
      input.type = 'checkbox'; input.setAttribute('data-part', n.id);
      if (n.parent) input.setAttribute('data-parent', n.parent);
      name.appendChild(input); name.appendChild(el('span', null, label));
      li.appendChild(name);
      return li;
    };
    box.textContent = '';
    var code = el('ul', 'map-tree'), outside = el('ul', 'map-tree');
    nodes.filter(function (n) { return n.kind === 'project'; }).forEach(function (p) {
      var li = code.appendChild(item(p, p.name + (isUnreached(p) ? ' · nås inte' : ''))), classes = nodes.filter(function (c) { return c.kind === 'class' && c.parent === p.id; });
      if (!classes.length) return;
      var sub = li.appendChild(el('ul', 'map-tree'));
      classes.forEach(function (c) { sub.appendChild(item(c, c.name + (isUnreached(c) ? ' · nås inte' : ''))); });
    });
    nodes.filter(function (n) { return n.kind === 'integration'; }).forEach(function (i) {
      var short = kindName(integrationOf(i.ref).kind, true);
      outside.appendChild(item(i, short !== i.name ? i.name + ' · ' + short : i.name));
    });
    [['Systemets egen kod', code], ['Utanför systemet', outside]].forEach(function (g) {
      if (!g[1].children.length) return;
      var set = el('fieldset'), legend = el('legend', null, g[0]);
      set.appendChild(legend); set.appendChild(g[1]); box.appendChild(set);
    });
    $('map-show').hidden = !box.children.length;
    syncTree();
  }
  // En bock är ikryssad när delen syns. En del i en dold del står avbockad och går inte att ändra: bocka i den som döljer den.
  function syncTree() {
    $('map-unreached-field').hidden = !state.anyUnreached;
    $('map-unreached').checked = state.unreached;
    Array.prototype.forEach.call($('map-tree').querySelectorAll('input[data-part]'), function (box) {
      var blocked = !!state.hidden[box.getAttribute('data-parent')], dimmed = !state.unreached && isUnreached(state.allNodes[box.getAttribute('data-part')] || {});
      box.checked = !state.hidden[box.getAttribute('data-part')] && !blocked && !dimmed;
      box.disabled = blocked || dimmed;
    });
    var n = hiddenIds().length, hint = $('map-hint'), off = !state.unreached && state.anyUnreached;
    hint.textContent = (n ? n + ' dolda' : '') + (n && off ? ', ' : '') + (off ? 'inte nådda dolda' : '');
    hint.hidden = n === 0 && !off;
    hint.title = n === 1 && !off ? 'En del av systemet är dold. Öppna Visa för att se den igen.' : 'Delar av systemet är dolda. Öppna Visa för att se dem igen.';
    $('map-show-summary').setAttribute('aria-label', n || off ? 'Visa, ' + (hint.textContent) : 'Visa');
    $('map-show-all').setAttribute('aria-disabled', n || off ? 'false' : 'true');
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
  function entryKey(e) { return e.name || e.method + ' ' + e.path; }
  function entryByKey(key) { return state.map.entryPoints.find(function (e) { return entryKey(e) === key; }) || null; }

  // Ett val av användaren (i menyn eller på kartan): samma som selectEntry, och skärmläsaren får veta vad som visas.
  function choose(e) {
    selectEntry(e);
    announce(e ? 'Visar vägen för ' + entryKey(e) + '.' : 'Visar hela systemet.');
  }

  // Välj ett anrop (eller null = hela systemet). Delarna på anropets väg fälls ut och vägen markeras; resten tonas ned.
  // En puls går längs vägen en gång, i den ordning anropen görs.
  function selectEntry(e) {
    var changed = state.entry !== e;
    if (changed) state.shut = {};
    state.entry = e;
    $('map-entry').value = e ? String(state.map.entryPoints.indexOf(e)) : '';
    if (!window.cytoscape) return;
    hideCard();
    // Ett annat anrop fäller ut andra delar: allt läggs ut på nytt (och glider dit), så att ordningen blir rätt.
    render(function () { fitView(e ? function () { pulse(entryKey(e)); } : null); }, changed);
  }

  // Delar som fälls ut av sig själva när ett anrop är valt: projekten och integrationerna på vägen (då syns läsningar,
  // skrivningar och meddelanden). Klasserna står kvar som noder.
  function onPath(id) {
    var n = state.nodes[id];
    return !!state.entry && !!n && (n.kind === 'integration' || n.kind === 'project') && n.entries.indexOf(entryKey(state.entry)) >= 0;
  }
  function isOpen(id) { return !!state.open[id] || (onPath(id) && !state.shut[id]); }
  function toggle(id) {
    if (isOpen(id)) { delete state.open[id]; if (onPath(id)) state.shut[id] = true; }
    else { state.open[id] = true; delete state.shut[id]; }
    hideCard();
    render();
  }
  // Detaljnivån: bara projekten, klasserna i projekten (från början), metoderna i klasserna eller allt utfällt. Det man sedan
  // fäller ut eller ihop själv gäller tills man väljer en nivå igen.
  function openLevel(level) {
    state.level = level; state.open = {}; state.shut = {};
    Object.keys(state.kids).forEach(function (id) {
      var kind = state.nodes[id].kind;
      if (level === 'all' || (level === 'classes' && kind === 'project') || (level === 'methods' && (kind === 'project' || kind === 'class'))) state.open[id] = true;
      else if (level === 'projects') state.shut[id] = true;
    });
  }
  function syncTools() {
    state.tools.forEach(function (b) {
      var on = b.getAttribute('data-level') === state.level || b.getAttribute('data-direction') === state.dir;
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
    });
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
    while (n && n.kind !== 'project') n = n.parent ? state.nodes[n.parent] : null;
    return n ? n.name : null;
  }
  // Steget för varje klass i sitt projekt: 1 när ett anrop in eller ett annat projekt anropar den (eller ingen gör det),
  // annars 1 + det högsta steget hos klasserna i projektet som anropar den. Vägen räknas med ett tak, så att cykler tar slut.
  // Ger också första lagret för varje projekt (base) och lagret för integrationerna (last).
  var MAX_STEP = 6;
  function computeSteps() {
    var steps = {}, roots = {}, calls = [], base = {}, span = {}, next = 1;
    var owner = function (id) {
      var n = state.nodes[id];
      while (n && n.kind !== 'class' && n.kind !== 'entry') n = n.parent ? state.nodes[n.parent] : null;
      return n;
    };
    Object.keys(state.nodes).forEach(function (id) { if (state.nodes[id].kind === 'class') steps[id] = 1; });
    state.view.links.forEach(function (k) {
      var a = owner(k.from), b = owner(k.to);
      if (!a || !b || a === b || b.kind !== 'class') return;
      if (a.kind === 'entry' || projectOf(a) !== projectOf(b)) roots[b.id] = true;
      else calls.push([a.id, b.id]);
    });
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
    // Projekten efter varandra i den ordning anropen når dem, sedan integrationerna, och sist projekt som ingen ingång når.
    var projects = state.view.nodes.filter(function (n) { return n.kind === 'project'; });
    projects.filter(function (n) { return !isUnreached(n); }).forEach(function (n) { base[n.name] = next; next += span[n.name] || 1; });
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
  // Färgen för en nodtyp eller en ikon i förklaringen: grått för anropen in, blått för systemets kod, grönt för resten.
  function colorOf(kind) {
    return kind === 'entry' ? COLORS.entry : kind === 'project' || kind === 'class' || kind === 'method' ? COLORS.code : COLORS.external;
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
  function badgeUri(count, color) {
    var text = '+' + count, w = 12 + text.length * 7;
    return svgUri(w, 18, '<rect x="0.75" y="0.75" width="' + (w - 1.5) + '" height="16.5" rx="8.25" fill="' + color + '" stroke="#ffffff" stroke-width="1.5"/>' +
      '<text x="' + (w / 2) + '" y="13" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="11" font-weight="700" fill="#ffffff">' + text + '</text>');
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
    var map = state.map, key = state.entry ? entryKey(state.entry) : null, nodes = [], edges = [], byId = {}, layers = {}, info = {}, parentOf = {}, pads = { system: { x: 20, y: 44 } };
    state.visible = {};
    var mark = function (list) { return key ? (list.indexOf(key) >= 0 ? ' path' : ' dimmed') : ''; };
    var file = String(map.solution || '').split(/[\\/]/).pop();
    nodes.push({ group: 'nodes', data: { id: 'system', label: 'Systemet' + (file ? ' (' + file + ')' : '') }, classes: 'system' });
    var add = function (n, parent) {
      var kids = state.kids[n.id], open = !!kids && isOpen(n.id), color = colorOf(n.kind), kind = iconKind(n);
      var label = n.kind === 'method' ? n.name + '()' : n.kind === 'operation' ? operationText(n.name, n.address) : n.name;
      if (n.kind === 'integration') {
        var short = kindName(integrationOf(n.ref).kind, true);
        if (short !== n.name) label += open ? ' · ' + short : '\n' + short;
      }
      // Rader (metoder, operationer) och avlånga noder (klass, projekt, anrop) har namnet inuti; integrationer är runda med namnet under.
      var pill = n.kind === 'method' || n.kind === 'operation', round = n.kind === 'integration' && !open, icon = kind ? iconUri(kind, color) : '';
      var data = { id: n.id, label: label, color: color, icon: icon, badge: kids && !open ? badgeUri(kids.length, color) : '' };
      if (parent) data.parent = parent;
      if (pill) data.w = Math.ceil(textWidth(label, 13)) + (icon ? 44 : 24);
      else if (!open && !round) data.w = Math.ceil(textWidth(label, 14) * (n.kind === 'project' ? 1.08 : 1)) + 56;
      var shape = open ? 'group' : pill ? 'pill' : round ? 'round' : 'long';
      var cls = shape + ' t-' + n.kind + (data.badge ? ' badge' : '') + (icon && pill ? ' with-icon' : '') + (isUnreached(n) ? ' unreached' : '') + mark(n.entries);
      nodes.push({ group: 'nodes', data: data, classes: cls });
      parentOf[n.id] = parent;
      if (open) pads[n.id] = { x: (n.kind === 'class' ? 10 : 12) + 2, y: (n.kind === 'class' ? 10 : 12) + 24 };
      else info[n.id] = { w: data.w || (round ? Math.max(60, Math.max.apply(null, label.split('\n').map(function (t) { return textWidth(t, 14); })) + 8) : 60),
        h: round ? 94 : pill ? 26 : n.kind === 'project' ? 40 : n.kind === 'entry' ? 30 : 34 };
      if (open) kids.forEach(function (k) { add(state.nodes[k], n.id); });
      else { var c = columnOf(n); (layers[c] = layers[c] || []).push(n); }
    };
    // Anropen i en grupp ligger i en egen yta med områdets namn, i fast ordning (sökväg, metod).
    var callGroup = {}, order = {}, fixed = {}, seq = 0;
    state.groups.forEach(function (g) {
      var keys = g.entries.map(entryKey), gid = 'cg:' + g.label;
      keys.forEach(function (k) { order['in:' + k] = seq++; });
      if (!g.label || g.single) return;
      nodes.push({ group: 'nodes', data: { id: gid, label: g.label, color: COLORS.entry }, classes: 'group t-callgroup' + mark(keys) });
      pads[gid] = { x: 16, y: 40 };
      fixed[gid] = true;
      keys.forEach(function (k) { callGroup['in:' + k] = gid; });
    });
    state.view.nodes.forEach(function (n) {
      if (n.kind === 'entry') add(n, callGroup[n.id] || null);
      else if (n.kind === 'integration') add(n, null);
      else if (n.kind === 'project') add(n, 'system');
    });
    state.view.links.forEach(function (k) {
      var a = visibleOf(k.from), b = visibleOf(k.to);
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
      calls: map.entryPoints.map(function (e) { return 'in:' + entryKey(e); }), order: order, fixed: fixed, groupOf: callGroup });
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
        return { group: 'edges', data: { id: e.id, source: e.a, target: e.b, color: colorOf(state.nodes[e.b].kind) }, classes: mark(e.entries).trim() };
      })).concat(bands.map(function (b, i) {
        return { group: 'edges', data: { id: 'band:' + i, source: b[0], target: b[1], color: '#ffffff', len: b[2] + 2 }, classes: 'band' };
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
      if (!lr && !li && state.nodes[ids[0]] && state.nodes[ids[0]].kind === 'entry') {
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

  var CY_STYLE = [
    { selector: 'node', style: { label: 'data(label)', 'font-family': FONT, 'font-size': 14, color: '#1b2430', 'text-wrap': 'wrap',
      'text-max-width': 150, 'line-height': 1.25, 'min-zoomed-font-size': 6 } },
    // Klasser, projekt och anrop: avlånga runda noder med ikonen till vänster och namnet inuti.
    { selector: 'node.long', style: { shape: 'round-rectangle', 'corner-radius': 17, width: 'data(w)', height: 34, 'background-color': '#ffffff',
      'border-width': 2, 'border-color': 'data(color)', 'background-image': 'data(icon)', 'background-width': 18, 'background-height': 18,
      'background-position-x': 12, 'background-position-y': '50%', 'text-valign': 'center', 'text-halign': 'center', 'text-margin-x': 12,
      'text-max-width': 400 } },
    { selector: 'node.long.t-project', style: { height: 40, 'corner-radius': 20, 'background-color': '#f1f6fd', 'font-weight': 600 } },
    { selector: 'node.long.t-entry', style: { height: 30, 'corner-radius': 15, 'background-color': '#eef1f5', 'font-size': 13 } },
    // Integrationer: stora runda ikonnoder med namnet under, på en ljus bakgrund så att det går att läsa över pilarna.
    { selector: 'node.round', style: { shape: 'ellipse', width: 60, height: 60, 'background-color': '#eef7ee', 'border-width': 2.5,
      'border-color': 'data(color)', 'background-image': 'data(icon)', 'background-width': 28, 'background-height': 28, 'text-valign': 'bottom',
      'text-margin-y': 6, 'text-background-color': '#fcfcfb', 'text-background-opacity': 0.9, 'text-background-padding': 2,
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
    { selector: 'node.pill', style: { shape: 'round-rectangle', width: 'data(w)', height: 26, 'background-color': '#ffffff', 'border-width': 1.5,
      'border-color': 'data(color)', 'font-size': 13, 'text-valign': 'center', 'text-halign': 'center', 'text-max-width': 400 } },
    { selector: 'node.pill.with-icon', style: { 'background-image': 'data(icon)', 'background-width': 16, 'background-height': 16,
      'background-position-x': 8, 'background-position-y': 5, 'text-margin-x': 10 } },
    // Utfällda delar: mjuka ytor med namnet ovanför.
    { selector: 'node.group', style: { shape: 'round-rectangle', 'corner-radius': 18, 'background-color': 'data(color)', 'background-opacity': 0.06,
      'border-width': 1.5, 'border-color': 'data(color)', 'border-opacity': 0.35, padding: 12, 'text-valign': 'top', 'text-halign': 'center',
      'text-margin-y': -4, 'font-weight': 600, color: '#1b2430', 'compound-sizing-wrt-labels': 'include', 'background-image': 'none' } },
    { selector: 'node.t-project.group', style: { 'corner-radius': 24, 'background-opacity': 0.07 } },
    { selector: 'node.t-class.group', style: { 'background-color': '#ffffff', 'background-opacity': 0.9, padding: 10, 'corner-radius': 14, 'border-opacity': 0.6 } },
    { selector: 'node.t-integration.group', style: { 'background-opacity': 0.08, 'border-opacity': 0.55 } },
    // Systemgränsen: allt inom den är systemets egen kod.
    { selector: 'node.system', style: { shape: 'round-rectangle', 'corner-radius': 36, 'background-opacity': 0, 'border-width': 1.5,
      'border-style': 'dashed', 'border-color': '#94a3b8', padding: 18, 'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': -6,
      color: '#536071', 'font-size': 14, 'compound-sizing-wrt-labels': 'include' } },
    { selector: 'edge', style: { width: 1.6, 'curve-style': 'bezier', 'line-color': 'data(color)', 'target-arrow-color': 'data(color)',
      'target-arrow-shape': 'triangle', 'arrow-scale': 0.85, opacity: 0.75 } },
    // Det som ingen ingång når: dämpat och streckat (streck, inte bara blekare, så att det syns även utan att skilja på ljushet).
    { selector: 'node.unreached', style: { opacity: 0.6, 'border-style': 'dashed', 'background-color': '#f4f5f7' } },
    { selector: 'node.group.unreached', style: { 'background-color': '#f4f5f7', 'background-opacity': 0.5 } },
    { selector: '.dimmed', style: { opacity: 0.18 } },
    { selector: 'edge.path', style: { width: 3, opacity: 1 } },
    { selector: 'node.long.path, node.round.path', style: { 'border-width': 3.5 } },
    // Pulsen längs vägen och grannarna när muspekaren står på en nod.
    { selector: '.pulse', style: { 'underlay-color': 'data(color)', 'underlay-opacity': 0.35, 'underlay-padding': 7, 'underlay-shape': 'ellipse' } },
    { selector: 'edge.pulse', style: { width: 6, 'underlay-padding': 5 } },
    { selector: '.faded', style: { opacity: 0.12 } },
    // Hjälpmedel för layouten som inte ska synas: fjädrarna mellan anropen in och ankarna mellan lagren.
    { selector: 'edge.band', style: { opacity: 0, events: 'no' } },
    { selector: 'node.anchor', style: { width: 1, height: 1, opacity: 0, events: 'no', label: '' } }
  ];

  // fresh: lägg ut allt från början (första gången, ny riktning eller detaljnivå); annars flyttas det som redan står bara så
  // mycket som behövs (och då räcker en snabbare genomräkning). Etiketterna under integrationerna räknas inte in i storleken,
  // annars blir kartan glest utspridd. Uppifrån och ned får lagren breda ut sig mer (lägre gravitation), annars blir kartan
  // smal och hög.
  function fcose(g, fresh, fit) {
    var lr = state.dir !== 'TB';
    return {
      name: 'fcose', quality: fresh ? 'proof' : 'default', randomize: false, animate: motion() && !fit && !fresh, animationDuration: 450,
      animationEasing: 'ease-in-out-cubic', fit: !!fit, padding: 30, nodeDimensionsIncludeLabels: false, packComponents: false, tile: false,
      // Ankarna mellan lagren ska bara hålla isär lagren, inte knuffa på noderna.
      nodeRepulsion: function (n) { return n.hasClass('anchor') ? 1 : lr ? 6000 : 9000; }, edgeElasticity: 0.45,
      // Anropen in står nära sin controller; övriga pilar får lite mer luft.
      idealEdgeLength: function (e) { return e.hasClass('band') ? e.data('len') : e.source().hasClass('t-entry') ? 25 : 40; },
      nestingFactor: 0.05,
      gravity: lr ? 0.3 : 0.15, gravityCompound: 1.0, gravityRangeCompound: 1, numIter: 2500, nodeSeparation: 45,
      relativePlacementConstraint: g.rules, alignmentConstraint: g.align || undefined
    };
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
      cy = state.cy = cytoscape({ container: view, style: CY_STYLE, elements: [], minZoom: 0.2, maxZoom: 2.5,
        userZoomingEnabled: false, boxSelectionEnabled: false, autounselectify: true });
      cy.on('tap', 'node', function (ev) { tapNode(ev.target.id()); });
      cy.on('tap', function (ev) { if (ev.target === cy) hideCard(); });
      cy.on('mouseover', 'node', function (ev) { view.style.cursor = 'pointer'; view.title = ev.target.hasClass('unreached') ? 'Nås inte från någon ingång' : ''; hover(ev.target); });
      cy.on('mouseout', 'node', function () { view.style.cursor = ''; view.title = ''; hover(null); });
      cy.on('viewport', placeCard);
      cy.on('position', 'node', placeCard);
      // Rullhjulet zoomar bara med Ctrl (⌘ på Mac); annars rullar sidan som vanligt.
      view.addEventListener('wheel', function (ev) { cy.userZoomingEnabled(ev.ctrlKey || ev.metaKey); }, true);
      if (window.ResizeObserver) new ResizeObserver(function () {
        cy.resize();
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
    runLayout(g, after, first || !!fresh, first, !first && fresh ? before : null);
  }
  // Var en ny nod börjar: där närmaste förälder eller hopfälld del stod (at: vilken del), eller mitt bland delarna den ersätter.
  function startOf(id, before) {
    for (var n = state.nodes[id]; n; n = n.parent ? state.nodes[n.parent] : null)
      if (before[n.id]) return { x: before[n.id].x, y: before[n.id].y, at: n.id };
    var sum = { x: 0, y: 0 }, count = 0, stack = (state.kids[id] || []).slice();
    while (stack.length) {
      var k = stack.pop();
      if (before[k]) { sum.x += before[k].x; sum.y += before[k].y; count++; } else stack = stack.concat(state.kids[k] || []);
    }
    return count ? { x: sum.x / count, y: sum.y / count, at: '*' + id } : null;
  }
  // before: var noderna stod innan, när allt läggs ut på nytt (de glider då dit från där de stod).
  function runLayout(g, after, fresh, fit, before) {
    var view = $('map-view'), cy = state.cy;
    if (state.layout) state.layout.stop();
    view.removeAttribute('data-ready');
    var layout = state.layout = cy.layout(fcose(g, fresh, fit));
    var done = function () {
      if (state.layout !== layout) return;
      state.unfitted = !view.clientWidth || !view.clientHeight;
      view.setAttribute('data-ready', 'true');
      if (after) after();
    };
    layout.one('layoutstop', function () {
      if (state.layout !== layout) return;
      if (!before || !motion()) return done();
      // Ny utläggning av allt: den är räknad från startplatsen, men noderna går mjukt dit från där de stod.
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
  function fitTo(selector, done) {
    var cy = state.cy;
    if (!cy) return;
    var eles = cy.elements(selector).not('.anchor');
    if (!eles.length) eles = cy.elements().not('.anchor');
    var bb = eles.boundingBox(), w = cy.width() - 60, h = cy.height() - 40;
    var zoom = Math.max(cy.minZoom(), Math.min(1.2, w / Math.max(1, bb.w), h / Math.max(1, bb.h)));
    var pan = { x: cy.width() / 2 - zoom * (bb.x1 + bb.w / 2), y: cy.height() / 2 - zoom * (bb.y1 + bb.h / 2) };
    if (motion()) cy.animate({ zoom: zoom, pan: pan }, { duration: 450, easing: 'ease-in-out-cubic', complete: done });
    else { cy.viewport({ zoom: zoom, pan: pan }); if (done) done(); }
  }
  // Vägen för det valda anropet, annars hela kartan.
  function fitView(done) { fitTo(state.entry ? '.path' : '*', done); }

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
    var n = state.nodes[id];
    if (!n) return;
    // Ett anrop väljs genom att klicka på dess nod; ett klick till på samma nod visar hela systemet igen.
    if (n.kind === 'entry') { var e = entryByKey(n.name); choose(e && state.entry === e ? null : e); return; }
    if (state.kids[id]) { toggle(id); return; }
    showCard(id);
  }

  // "OrderService.PlaceAsync", "ShopDatabase: läsa Customers" eller "POST /orders".
  function nameOf(id) {
    var n = state.nodes[id];
    if (!n) return id;
    var parent = n.parent && state.nodes[n.parent];
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
