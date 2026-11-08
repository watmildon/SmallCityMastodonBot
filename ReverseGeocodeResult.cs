
public class ReverseGeocodeResult
{
    public int place_id { get; set; }
    public string licence { get; set; } = "";
    public string osm_type { get; set; } = "";
    public int osm_id { get; set; }
    public string lat { get; set; } = "";
    public string lon { get; set; } = "";
    public string display_name { get; set; } = "";
    public Address address { get; set; } = null!;
    public string[] boundingbox { get; set; } = null!;
}

public class Address
{
    public string state { get; set; } = "";
    // Some first-level regions come back as "territory" instead of "state"
    // (e.g. Australia's Northern Territory and Australian Capital Territory).
    public string territory { get; set; } = "";
    public string ISO31662lvl4 { get; set; } = "";
    public string country { get; set; } = "";
    public string country_code { get; set; } = "";
}
