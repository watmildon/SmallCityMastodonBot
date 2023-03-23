# SmallCityMastodonBot
The repository for code and data file backing the [@SmallTownUSA@en.osm.town](https://en.osm.town/@SmallTownUSA) account. Takes a json file of OpenStreetMap place nodes and a Mastodon token and finds a random unmapped town to post about.

If you'd like to see something added to the posts please let me know!

## Development Notes
The overpass query to generate the townsList.json looks like:
```
[out:json][timeout:60];
{{geocodeArea:"United States"}}->.a;
(
  node["population"]["place"](if:(t["population"]<"1000"))(area.a);
);
out body;
>;
```
