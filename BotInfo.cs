
public class BotConfigFile
{
    public Botinfo[] botInfo { get; set; } = null!;
}

public class Botinfo
{
    public string botUrl { get; set; } = "";
    public string botDomain { get; set; } = "";
    public string botName { get; set; } = "";
    public string townFile { get; set; } = "";
    public Overpassquery[] overpassQuery { get; set; } = null!;
    public Posttext postText { get; set; } = null!;
    public Monthlyposttext? monthlyPostText { get; set; }
}

/// <summary>
/// Localized templates for the monthly retrospective post. Placeholders are string.Format
/// indexes; see MonthlyRetrospective for what each one receives. A bot without this section
/// skips the monthly post.
/// </summary>
public class Monthlyposttext
{
    public string culture { get; set; } = "en-US";
    public string language { get; set; } = "en";
    public string headerText { get; set; } = "";       // {0}=month name
    public string introText { get; set; } = "";        // {0}=month name, {1}=towns featured, {2}=towns mapped
    public string buildingsLine { get; set; } = "";    // {0}=buildings added
    public string? roadsLine { get; set; }             // {0}=roads reviewed (omit for bots without a tiger:reviewed query)
    public string changesetsLine { get; set; } = "";   // {0}=changesets, {1}=mappers
    public string mostImprovedText { get; set; } = "";
    public string starLine { get; set; } = "";         // {0}=town, {1}=state, {2}=buildings added
    public string thanksText { get; set; } = "";
    public string hashtags { get; set; } = "";
    public string imageAltText { get; set; } = "";     // {0}=town, {1}=state, {2}=buildings added, {3}=month name
}

public class Posttext
{
    public string greetingText { get; set; } = "";
    public string populationText { get; set; } = "";
    public string mapLinkText { get; set; } = "";
}

public class Overpassquery
{
    public string featureTag { get; set; } = "";
    public string queryType { get; set; } = "";
    public int countMaximum { get; set; }
    public string radiusInMeters { get; set; } = "";
    public string message { get; set; } = "";
}
