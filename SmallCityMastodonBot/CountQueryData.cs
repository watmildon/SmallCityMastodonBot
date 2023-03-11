
using Newtonsoft.Json;

public class CountQueryData
{
    public float version { get; set; }
    public string generator { get; set; }
    public Osm3s osm3s { get; set; }
    public Element[] elements { get; set; }
}