using Microsoft.Playwright;
using TestGen.Core.Views;

namespace TestGen.Browser;

/// <summary>Översätter lokaliserarna i views.json till Playwright.</summary>
public static class Locators
{
    public static ILocator Resolve(this IPage page, Locator locator) => locator.Kind switch
    {
        LocatorKind.Label => page.GetByLabel(locator.Value, new() { Exact = true }),
        LocatorKind.Role => page.GetByRole(Role(locator.Value), new() { Name = locator.Name, Exact = locator.Name is null ? null : true }),
        LocatorKind.TestId => page.GetByTestId(locator.Value),
        _ => page.Locator(locator.Value),
    };

    private static AriaRole Role(string value) =>
        Enum.TryParse<AriaRole>(value.Replace("-", ""), ignoreCase: true, out var role)
            ? role
            : throw new ArgumentException($"Okänd roll i lokaliseraren: {value}.");
}
