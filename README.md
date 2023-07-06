# SmallCityMastodonBot
The repository for code and data file backing the [@SmallTownUSA@en.osm.town](https://en.osm.town/@SmallTownUSA) account. Takes a json file of OpenStreetMap place nodes and a Mastodon token and finds a random unmapped town to post about.

If you'd like to see something added to the posts please let me know!

## Mapping
As with all of OSM, map whatever you like! Add buildings, check roadways for alignment to aerial imagery, draw landuse areas, use street side imagery to add detail etc. If you want to make it easier to other folks to collaborate or appreciate your great work feel free to add #UnmappedSmallTownUSA to your changsets.

## Bot replies
If you do some mapping and would like the bot to generate an updated screen shot respond to the top level town post (ex: [here](https://en.osm.town/@SmallTownUSA/110663015761350007)) with any message that contains "I mapped it!".

Currently, there's a issue getting the OSM tileserver to produce fresh tiles. You can use your browser to get the tile server to generate new tiles by doing a no-cache refresh (this is ctrl+F5 on Windows, shift+command+R on Mac). Wheverever you see is what the bot will pull. When the bot runs it will pull a new image and reply to your post.

## Current settings
The bot is set to automatically run once a day around noon PT. It will post a new town and then scan for any replies that are necessary. If it's not responded to you after a day or so please open an issue with a link to your reply.

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
