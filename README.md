# Open Configuration

Shared API for Vintage Story mods to handle configuration files (`ModConfig/.../*.json`)
without repeating the loading boilerplate in every mod.

## Installing in another mod

1. Declare the dependency in the consumer mod's `modinfo.json`:

```json
"dependencies": {
    "game": "1.21.0",
    "openconfiguration": ""
}
```

## Basic usage

Define a simple class with the fields and their defaults (no `static`, no dictionary):

```csharp
public class BaseConfig
{
    public bool enableWhitelist = false;
    public bool enableBlacklist = true;
    public int increaseStatsEveryDownHeight = 10;
    public double lifeStatsIncreaseEveryHeight = 0.1;
    public List<string> homeSyntaxes = ["home"];
    public Dictionary<string, double> whitelistDistance = [];
}
```

Load it during `AssetsLoaded` (or wherever the mod already called `UpdateBaseConfigurations`):

```csharp
using OpenConfiguration;

public static class Configuration
{
    public static ModLogger Logger;
    public static BaseConfig Base = new();

    public static void Load(ICoreAPI api)
    {
        Logger ??= new ModLogger(api.Logger, "RPGDifficulty");
        Base = ConfigManager.LoadModConfig<BaseConfig>(api, "RPGDifficulty", "base", Logger);
    }
}
```

And use it normally: `Configuration.Base.enableWhitelist`, `Configuration.Base.homeSyntaxes`, etc.

To save it back (e.g. after a runtime change):

```csharp
ConfigManager.SaveModConfig(api, "RPGDifficulty", "base", Configuration.Base, Configuration.Logger);
```

## Server → client sync

```csharp
// Server side (e.g. inside StartServerSide)
Configuration.Base = ConfigManager.LoadSyncedModConfig<BaseConfig>(serverApi, "RPGDifficulty", "base", Configuration.Logger);

// Client side (e.g. inside StartClientSide)
Configuration.Base = ConfigManager.LoadSyncedModConfig<BaseConfig>(clientApi, "RPGDifficulty", "base",
    onSynced: config => Configuration.Logger.Log("Received server config"));
```

Both sides must use the same `modFolderName`/`configName` pair (or `relativeDirectory`/`configName` if you
use `LoadSynced` directly), that pair is the sync key that matches a server config to its client counterpart.
Nothing is read from disk on the client: the returned instance only holds real values after the server's
packet arrives, so code that depends on it should run from the `onSynced` callback rather than right after
the `LoadSynced` call.

Under the hood, every mod using `LoadSynced`/`RegisterSync` shares a single network channel
(`ConfigSync`/`ConfigSyncPacket`, channel id `"openconfiguration"`) registered once by this mod's own
`OpenConfigurationModSystem`. The server keeps a `key -> serialize` map; once a player finishes joining
(`IPlayer` becomes `PlayerNowPlaying`), it serializes and sends one packet per registered key, addressed
only to that player. The client keeps a `key -> apply` map and, on every incoming packet, looks up the
handler for that packet's key and applies its JSON. This is also the mechanism behind:

- `RegisterSync(ICoreServerAPI, key, Func<string>)` / `RegisterSync(ICoreClientAPI, key, Action<string>)` —
  the raw building block `LoadSynced` is built on, for configs that aren't a plain `Load<T>` model.
- `RegisterStaticFieldSync(ICoreServerAPI, key, Type)` / `RegisterStaticFieldSync(ICoreClientAPI, key, Type, ...)` —
  for mods that keep their config as loose static fields on a class instead of an instance; it syncs the
  class's static primitive/string/`Dictionary<string, double>` fields as a single JSON object keyed by field name.

## Logging (`ModLogger`)

```csharp
var logger = new ModLogger(api.Logger, "RPGDifficulty");
logger.Log("normal message");
logger.LogWarn("warning");
logger.LogError("error");
logger.LogDebug("only shows up if ExtendedLoggingEnabled = true");
```

`ModLogger` is an instance (not static): each mod creates its own, since the Open Configuration DLL itself
is loaded only once in the process and shared across every mod that depends on it.