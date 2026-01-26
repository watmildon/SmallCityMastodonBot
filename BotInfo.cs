
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
