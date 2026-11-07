namespace SystemMap.AspNetCore;

/// <summary>Inställningar för <c>MapSystemMap</c>.</summary>
public sealed class SystemMapOptions
{
    /// <summary>
    /// Visa kartan även utanför Development när adressen saknar krav på inloggning. Standard är nej: då ger adressen 404.
    /// </summary>
    public bool AllowWithoutAuthorization { get; set; }

    /// <summary>Mappen där bygget lagt kartan (systemmap.html och map.json, eller dev.json). Standard: systemmap i appens mapp.</summary>
    public string Directory { get; set; } = Path.Combine(AppContext.BaseDirectory, "systemmap");
}
