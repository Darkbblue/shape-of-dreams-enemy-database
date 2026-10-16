**WARNING: still under development. I won't have the time for testing until tonight.**

This is a practice helper for the video game Shape of Dreams.

Shape of Dreams is an action roguelike.
Although in low difficulty, you can beat the game mostly relying on reasonable builds, higher difficulties do require stronger ability to dodge enemy moves.
To help practice, this repo provides two functionalities:
1. A datamined boss move database. It's mainly about how each move is cast, such as cooldown, triggering event, etc., rather than the damage or attack range.
2. A simple overlay mod. It shows the boss's name and what move it's currently using, all using the same name as stored in the database. So, you can type in the exact name as shown in the mod to find the boss and the move. The mod also encapsulates a simple command to help you find a certain boss quickly.

## Installation
The database can be directly read from within this repo. No installation is required.  

To install the mod, copy the entire `modding/BossPracticeOverlay/` folder into your game's mod folder (`Shape of Dreams/Mods`).

## Usage
To use the database, navigate to `datamining/processed_data/out`.
Here you can find many folders, each in correspondence to one boss.
Within each folder, there're multiple json files, each in correspondence to one difficulty+variant combination. (In hard mode, some bosses will randomly turn into its hard variant).
Although they are json files, they are in fact designed to be human-readable.

To use the mod's overlay function, just install the mod and press F1.
When you enter a boss arena, the overlay will start to work.

To use the mod's teleport function, press \` to open the console.
First use `JumpToBoss` command, which will output the names of all the bosses available.
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

To maintain the database, run:
```bash
python3 datamining/run_all.py "<GameRootFolder>"
```

To compile the mod locally, you need to run:
```bash
cd modding\BossPracticeOverlay
dotnet build -c Release -p:GameManaged="path/to/steamapps/common/Shape of Dreams/Shape of Dreams_Data/Managed"
```
