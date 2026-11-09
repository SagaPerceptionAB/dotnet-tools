# 0005. Säkerhetsanalys utan CodeQL

## Status
Ersatt av [0009](0009-codeql-in-the-public-repository.md). Antagen 2026-11-08.

## Beslut
Repot använder .NETs säkerhetsregler (`AnalysisModeSecurity=Recommended`), NuGetAudit som eget CI-jobb (`audit`) och Dependabot. CodeQL och dependency-review-action används inte.

## Varför
Repot är privat och saknar licens för GitHub Code Security. CodeQL och dependency-review fungerar inte utan den.

## Följder
- CodeQL läggs till om licensen skaffas.
- En ny sårbarhetsvarning (NU1901-NU1904) stoppar inte bygget. `audit`-jobbet fäller i stället.
