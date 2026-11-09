using Microsoft.Playwright;
using TestGen.Browser.Forms;
using TestGen.Core.Views;

namespace TestGen.Browser;

/// <summary>Fyller i och skickar formulär på en sida.</summary>
public static class FormDriver
{
    /// <summary>Fyller i fälten med de giltiga värdena i planen. Fält utan värde lämnas.</summary>
    public static Task FillValidAsync(IPage page, FormPlan plan) =>
        FillAsync(page, plan.Fields.Where(f => f.Valid is not null).Select(f => (f.Field, f.Valid!)));

    /// <summary>Fyller i fälten med de angivna värdena, i den ordning de kommer. Skickar inte formuläret.</summary>
    public static async Task FillAsync(IPage page, IEnumerable<(Field Field, FieldValue Value)> values)
    {
        foreach (var (field, value) in values)
        {
            var locator = page.Resolve(field.Locator);
            switch (field.Type)
            {
                case FieldType.Select: await locator.SelectOptionAsync(value.Text); break;
                case FieldType.Checkbox: await locator.SetCheckedAsync(value.Checked); break;
                case FieldType.Radio: await locator.And(page.Locator($"[value=\"{value.Text.Replace("\"", "\\\"")}\"]")).CheckAsync(); break;
                case FieldType.Hidden: break;
                default: await locator.FillAsync(value.Text); break;
            }
        }
    }

    /// <summary>
    /// Klickar på knappen och väntar tills sidan är klar (navigering klar och nätverket stilla). Med <paramref name="browserValidation"/> false
    /// stängs webbläsarens egen kontroll (required, type=email, min/max) av först, annars stoppar den formuläret innan appen hunnit visa sina egna fel.
    /// </summary>
    public static async Task SubmitAsync(IPage page, Form form, bool browserValidation = true)
    {
        var submit = page.Resolve(form.Submit);
        if (!browserValidation) await submit.EvaluateAsync("b => { if (b.form) b.form.noValidate = true; }");
        await submit.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Tar bort maxlength från fältet. Webbläsaren klipper annars en för lång text när den fylls i, och då går det inte att pröva
    /// att appen eller API:et själv avvisar den.
    /// </summary>
    public static Task AllowTooLongAsync(ILocator field) => field.EvaluateAsync("e => e.removeAttribute('maxlength')");

    /// <summary>
    /// Elementen som räknas som ett fel appen visar: <c>role="alert"</c>, utom bekräftelser (bootstrap-klasserna alert-success och alert-info).
    /// </summary>
    public static ILocator ErrorAlerts(IPage page) => page.Locator("[role=alert]:not(.alert-success):not(.alert-info)");

    /// <summary>
    /// Är fältet markerat som ogiltigt: aria-invalid="true", klassen invalid (Blazor), klassen input-validation-error (Razor Pages),
    /// eller anser webbläsaren att värdet inte är giltigt (validity.valid)? Väntar högst <paramref name="timeout"/> (standard 5 s) på att sidan svarar.
    /// </summary>
    public static async Task<bool> IsInvalidAsync(ILocator field, TimeSpan? timeout = null)
    {
        const string script = "e => e.getAttribute('aria-invalid') === 'true' || e.classList.contains('invalid') || e.classList.contains('input-validation-error') || (!!e.validity && !e.validity.valid)";
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                if (await field.EvaluateAsync<bool>(script)) return true;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
                // Sidan laddades om medan fältet lästes: försök igen.
            }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(50);
        }
    }
}
