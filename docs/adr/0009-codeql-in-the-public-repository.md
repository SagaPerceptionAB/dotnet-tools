# 0009. CodeQL i det publika repot

## Status
Antagen 2026-11-09. Ersätter 0005.

## Beslut
CI har ett `codeql`-jobb för C#. Det kompletterar .NETs säkerhetsregler (`AnalysisModeSecurity=Recommended`), NuGetAudit (jobbet `audit`) och Dependabot.

## Varför
0005 valde bort CodeQL eftersom repot var privat och saknade licens för GitHub Code Security. Repot är publikt, där CodeQL är gratis.

## Följder
- Resultatet visas under *Security > Code scanning*. Jobbet bygger solutionen för hand (`build-mode: manual`).
- En ny sårbarhetsvarning (NU1901-NU1904) stoppar fortfarande inte bygget. `audit`-jobbet fäller i stället.
