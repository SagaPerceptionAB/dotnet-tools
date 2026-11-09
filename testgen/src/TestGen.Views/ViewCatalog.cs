using TestGen.Core.Views;

namespace TestGen.Views;

/// <summary>Vyerna i en app, sorterade på rutt och källfil, med varningarna som hittades på vägen.</summary>
public sealed record ViewCatalog(IReadOnlyList<View> Views, IReadOnlyList<string> Warnings);
