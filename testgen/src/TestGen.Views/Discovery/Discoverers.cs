namespace TestGen.Views.Discovery;

/// <summary>Upptäckarna som körs av <c>testgen views</c>, i den ordning de körs.</summary>
public static class Discoverers
{
    public static IReadOnlyList<IViewDiscoverer> All() => [new BlazorDiscoverer(), new RazorPagesDiscoverer(), new MvcDiscoverer(), new SpaDiscoverer()];
}
