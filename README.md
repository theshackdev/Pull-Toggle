<img src="icon.png" alt="" width="128" align="right">

# Pull Toggle

**[Download on Thunderstore](https://thunderstore.io/c/valheim/p/TheShackDev/Pull_Toggle/)** · version 1.0.1 · part of [The Shack Pack](https://thunderstore.io/c/valheim/p/TheShackDev/The_Shack_Pack/)

Per-chest and per-machine control over automatic resource pulling, for **[OttoFuel](https://thunderstore.io/c/valheim/p/potto007/OttoFuel/)** and **[CraftFromContainers](https://thunderstore.io/c/valheim/p/UnderHeiz/CraftFromContainers/)**.

## The problem

OttoFuel refills fires, torches, smelters, kilns and ovens from nearby chests. CraftFromContainers builds, crafts and cooks using nearby chests. Both take from **every** chest in range and feed **every** machine in range. You can't keep one chest's wood for building, or stop one kiln from burning through your stock.

## What it does

**Chests:**
- Open a chest and click the **Pulling: ON / OFF** button above the chest window. You can also press **I** with it open, or while looking at it.
- **OFF:** neither mod takes anything from that chest. Your own Quick Stack, Restock and Take All still work.

**Machines** (fires, torches, braziers, hearths, smelters, kilns, blast furnaces, windmills, spinning wheels, refineries, ovens, shield generators and cooking stations):
- Look at it and press **I**.
- **OFF:** it isn't refilled or loaded from chests. Filling it by hand still works.

**For both:**
- The hover text shows the current state: **[I] Pulling: ON / OFF**.
- The setting is saved on the object in the world, so every player sees the same thing and it survives restarts.
- Only players with ward access can change it.
- Everything starts **ON**, so nothing changes until someone switches something off.

## Notes

- **Client-side.** Every player should have it so everyone's game respects the switches. The pack includes it. The server doesn't need it.
- Works with either or both of OttoFuel and CraftFromContainers.
- The key can be changed in F1 (Pull Toggle → Toggle Key).
- If an update to either mod renames what this hooks into, it logs a warning and skips that part, rather than breaking anything.
- Tested with OttoFuel 1.7.1, CraftFromContainers 4.0.70, BepInExPack 5.4.2351, Valheim 1.0.17.

## Credits

All credit for the pulling itself goes to potto007 (OttoFuel) and aedenthorn / UnderHeiz (CraftFromContainers). This mod only adds the switches.

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (6.0 or newer) and Valheim. The project compiles against your own copies of the game's DLLs and BepInEx; none of them are in this repo.

```bash
dotnet build -c Release \
  -p:ValheimManaged="/path/to/Valheim/valheim_Data/Managed" \
  -p:BepInExCore="/path/to/BepInEx/core"
```

`BepInExCore` is the `BepInEx/core` folder of any BepInEx install or r2modman profile. Without the `-p:` options the project looks for a Linux Steam install and an r2modman profile named "The Shack Pack Test Environment". The DLL lands in `bin/Release/`.

This is the source of version 1.0.1 on Thunderstore.

## License

MIT, see [LICENSE](LICENSE).

*This mod was written with the help of an AI assistant (Claude).*
