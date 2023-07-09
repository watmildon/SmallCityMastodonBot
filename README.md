# SmallCityMastodonBot
The repository for code and data file backing the [@SmallTownUSA@en.osm.town](https://en.osm.town/@SmallTownUSA) account. Takes a json file of OpenStreetMap place nodes and a Mastodon token and finds a random unmapped town to post about. The post includes an image that is the current state of Carto rendering at the time of posting.

All of the cool things the bot does have come from community ideas. Please let me know what else you'd like to see!

## Mapping
As with all of OSM, map whatever you like! Add buildings, check roadways for alignment to aerial imagery, draw landuse areas, use street side imagery to add detail etc. If you want to make it easier to other folks to collaborate or appreciate your great work feel free to add #UnmappedSmallTownUSA to your changsets.

## Bot replies
If you do some mapping and would like the bot to generate an updated screen shot, respond to the top level town post (ex: [here](https://en.osm.town/@SmallTownUSA/110663015761350007)) with any message that contains "I mapped it!". 

The bot tries to get updated tiles from the OSM tileservers but sometimes may need a little help. If you discover that the bot hasn't managed to pull new tiles you can help it along:
* navigate to the town on osm.org
* scroll to zoom 16
* reload the page without caching (ctrl+f5 on Windows, shift+command+r on Mac)
* post a new "I mapped it!" reply

## Current settings
The bot is set to post a new town once a day around noon PT. It will scan for posts needing replies every hour. If you discover that either of these is not happening, please file a ticket here or ping [me on Mastodon](https://en.osm.town/@watmildon).

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
