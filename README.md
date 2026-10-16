# Shape of Dreams Monster Database and Practice Overlay Mod

This is a practice helper for the video game [Shape of Dreams](https://store.steampowered.com/app/2444750/Shape_of_Dreams/).

Shape of Dreams is an action roguelike.
Although in low difficulty, you can beat the game mostly relying on reasonable builds, higher difficulties do require stronger ability to dodge enemy moves.
To help practice, this repo provides two functionalities:
1. A datamined boss move database. It's mainly about how each move is cast, such as cooldown, triggering event, etc., rather than the damage or attack range.
2. A simple overlay mod. It shows the boss's name and what move it's currently using, all using the same name as stored in the database. So, you can type in the exact name as shown in the mod to find the boss and the move. The mod also encapsulates a simple command to help you find a certain boss quickly.

## Installation
The database can be directly read from within this repo. No installation is required.  

To install the mod, copy the entire `modding/BossPracticeOverlay/` folder into your game's mod folder (`Shape of Dreams/Mods`).
Then, from the in-game settings, turn on developer mode. You interact with this mod via the developer console.

## Usage
To use the database, navigate to `datamining/processed_data/out`.
Here you can find many folders, each in correspondence to one boss.
Within each folder, there're multiple json files, each in correspondence to one difficulty+variant combination. (In hard mode, some bosses will randomly turn into its hard variant).
Although they are json files, they are in fact designed to be human-readable.

To use the mod's overlay function, just install the mod.
When you enter a boss arena, the overlay will start to work.

To use the mod's teleport function, press F1 and \` to open the console.
First use `JumpToBossList` command, which will output the names of all the bosses available.
Then, pick one boss name and use `JumpToBoss <BossName>` command.
This will also automatically turn on a practice-friendly HP lock: you won't die (mostly), but still see the on-hit effect and the health bar moving, so that you could know if you're hit.
You can teleport to new bosses without quiting the current run. You can also teleport to a new boss without defeating the current boss.
If you want to disable HP lock, simply end the run and start a new run.

## Maintenance
For maintenance purpose, you need to additionally install:
```bash
# make sure you're using python>=3.10
pip3 install UnityPy

# make sure you have dotnet>=10
dotnet tool install --global ilspycmd
```

To update the database in case the game gets new bosses in the future, run:
```bash
python3 datamining/run_all.py "<GameRootFolder>"
```

To update the boss list, which the mod relies on, in case the game gets new bosses in the future, use `BuildBossSceneMap` command in the console **at the main menu**.
You will then find a file at `Mods/BossPracticeOverlay/about/boss_scenes.json`.
Copy the file into this repo's corresponding location.

Ideally, updating the boss-scene file is enough to keep the mod updated for new patches.
But if you really need to compile the mod locally, you need to run:
```bash
cd modding\BossPracticeOverlay
dotnet build -c Release -p:GameManaged="path/to/steamapps/common/Shape of Dreams/Shape of Dreams_Data/Managed"
```
