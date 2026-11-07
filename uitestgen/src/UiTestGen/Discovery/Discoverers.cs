namespace UiTestGen.Discovery;

/// <summary>Upptäckarna som körs av <c>uitestgen discover</c>, i den ordning de körs.</summary>
public static class Discoverers
{
    public static IReadOnlyList<IViewDiscoverer> All() => [new BlazorDiscoverer(), new RazorPagesDiscoverer(), new MvcDiscoverer(), new SpaDiscoverer()];
}
