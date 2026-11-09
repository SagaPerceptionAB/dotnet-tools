# 0004. Tillgänglighetstester är valbara

## Status
Antagen 2026-11-08.

## Beslut
Tillgänglighetskontrollen (axe-core, WCAG 2.1 AA) är avstängd som standard och slås på i `testgen.yaml`.

## Varför
Alla tjänster behöver den inte, och den gör testkörningarna längre. Licensen för axe-core (MPL-2.0) listas bland tredjepartslicenserna.

## Följder
- Verktygets egna webbgränssnitt och HTML-rapporter följer WCAG 2.1 AA och kontrolleras alltid.
