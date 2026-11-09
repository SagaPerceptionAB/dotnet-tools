# 0003. Ingen bakåtkompatibilitet

## Status
Antagen 2026-11-08.

## Beslut
Namn och format byts direkt. Maskinläsbara koder, JSON-nycklar och flaggor blir engelska utan alias, migrering eller ny huvudversion.

## Varför
Allt är förhandsversion utan användare. Lager för bakåtkompatibilitet skulle bara vara kod att underhålla.

## Följder
- Alla JSON-format har `schemaVersion` (1) från start.
- Den här ADR:n ses över när första externa användaren finns.
