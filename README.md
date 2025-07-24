
# SmallCityMastodonBot

This repository contains the code, datafiles, and github actions to run various regional SmallTownBot accounts:
*  [@SmallTownUSA@en.osm.town](https://en.osm.town/@SmallTownUSA) 
* [@SmallTownCanada@en.osm.town](https://en.osm.town/@SmallTownCanada) 

Once per day, these bots find an undermapped town in their given region and post about it on the [OSM mastodon instance](https://en.osm.town).  


## Mapping

As with all of OSM, map whatever you like! Add buildings, check roadways for alignment to aerial imagery, draw landuse areas, use street side imagery to add detail etc. If you want to make it easier to other folks to collaborate or appreciate your great work feel free to add appropriate hash tags to your changsets. (ex: #UnmappedSmallTownUSA, #UnmappedSmallTownCA)
  
## Adding a region
If you would like to create a new regional SmallTownBot account, please do! You'll want to make sure your region has data in OSM using the common `place` and `population` tags.

Here's the steps to get it set up:
* Create a mostodon account, all of the current bots are on https://en.osm.town
* Optional: add your version of the bot with flag avatar, the template is in the repo under images
* Contact me and let me know what the account API key is so I can add it to the repository secrets
* Generate a regional datafile of towns using Overpass. It should look like townsList.json
* Add this file to the repo as townsList_REGION.json
* Add a section to SmallCityBotConfig.json. Your bot must have a unique name.
* Test run the bot (see below)
* Add a run command for your bot in .github/workflows/daily-run.yml

### Testing 
The bot is set up to run in VS Code and should execute everything except posting to Mastodon. To run one of the bots, change `args` in .vscode/launch.json. The first argument `12345` is the signal to the bot that you are running in test mode. You should see a bunch of output to the console to demonstrate that the bot is working and completed execution.


## More info
Check out the [wiki](https://github.com/watmildon/SmallCityMastodonBot/wiki).
