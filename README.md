# Queued Construction for SE
Queued Construction is a BepInEx plugin for the game Solar Expanse that allows you to queue the construction of facilities, even when all of the required resources to build it aren't currently present at the body. It also greatly optimizes the game's construction behavior, collapsing all construction for a given building into a single panel which shows the amount of buildings scheduled/currently being built.

Pre-plan a Mars base by queuing all of the constructions you need to make, then see all of the resources you need to ship at a glance.

# Example
<img width="377" height="227" alt="image" src="https://github.com/user-attachments/assets/21b3ccd1-0d49-435b-abf8-1221c172db07" />

When insufficient resources are present to build all queued constructions, the amount needed is now visible in the resources tab. If the needed resources are available to be purchased on the market, the stock game popup will appear offering for you to buy the needed resources.

# Installation

1. This plugin uses BepInEx 5.4 to inject code into the Solar Expanse exe. Install it here: https://docs.bepinex.dev/articles/user_guide/installation/index.html
2. Once BepInEx is installed, run it once to generate the /plugins/ folder.
3. Download the latest release of this mod.
4. Move the contents of the downloaded /plugins/ folder into BepInEx/plugins
