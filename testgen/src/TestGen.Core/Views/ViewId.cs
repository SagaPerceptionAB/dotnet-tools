namespace TestGen.Core.Views;

/// <summary>Stabilt id för en vy: <c>ui:</c> och ruttmallen, t.ex. <c>ui:/products/create</c>.</summary>
public static class ViewId
{
    public const string Prefix = "ui:";

    public static string Of(string route) => Prefix + route;
}
