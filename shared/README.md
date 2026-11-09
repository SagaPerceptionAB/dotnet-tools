# Gemensam kod

Intern kod som testgen och systemmap delar. Den ges inte ut som eget paket.

- `CommandLine`: flaggor, felmeddelanden på enkel svenska och exitkoder för kommandona. Bakas in i `SagaPerception.TestGen.NUnit` som `SagaPerception.CommandLine`, och i verktygen.
- `tests/CommandLine.Tests`: testerna för den.
