# SmallCityMastodonBot
The repository for code and data file backing the [@SmallTownUSA@en.osm.town](https://en.osm.town/@SmallTownUSA) account. Takes a json file of OpenStreetMap place nodes and a Mastodon token and finds a random unmapped town to post about. The post includes an image that is the current state of Carto rendering at the time of posting.

If you'd like to see something added to the posts please let me know!

## Mapping
As with all of OSM, map whatever you like! Add buildings, check roadways for alignment to aerial imagery, draw landuse areas, use street side imagery to add detail etc. If you want to make it easier to other folks to collaborate or appreciate your great work feel free to add #UnmappedSmallTownUSA to your changsets.

## Bot replies
If you do some mapping and would like the bot to generate an updated screen shot, respond to the top level town post (ex: [here](https://en.osm.town/@SmallTownUSA/110663015761350007)) with any message that contains "I mapped it!".

## Current settings
The bot is set to automatically run once a day around noon PT. It will post a new town and then scan for any replies that are necessary. I plan to have the reply portion run more frequently but it's not up yet. If it's not responded to you after a day or so please open an issue with a link to your reply.

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
