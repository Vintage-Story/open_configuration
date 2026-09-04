using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenConfiguration;

/// <summary>
/// Loads and saves strongly-typed JSON configuration files under the game's data path, replacing the
/// hand-rolled Dictionary&lt;string, object&gt; + TryGetValue/cast boilerplate previously copy-pasted
/// into every mod's Configuration.cs.
/// </summary>
/// <remarks>
/// Usage: define a plain class with public fields/properties carrying their default values (the same way
/// mods already declare their config fields today), then call <see cref="Load{T}"/> once during
/// AssetsLoaded. Missing keys in the user's file are backfilled with defaults and the file is rewritten;
/// fields with an incompatible JSON type are logged and left at their default; a file that fails to parse
/// entirely is preserved alongside a freshly written default file instead of being silently discarded.
/// </remarks>
public static class ConfigManager
{
    /// <summary>
    /// Loads &lt;configName&gt;.json from &lt;api.DataBasePath&gt;/&lt;relativeDirectory&gt;, creating the
    /// directory/file with default values from <typeparamref name="T"/> if either is missing. Keys present
    /// in <typeparamref name="T"/> but absent from the file are added back into it with their default value.
    /// </summary>
    /// <typeparam name="T">Config model with a parameterless constructor; its field initializers are the defaults.</typeparam>
    /// <param name="api">Any ICoreAPI, used for the data path and to read a fallback asset default.</param>
    /// <param name="relativeDirectory">Path relative to the data folder, e.g. "ModConfig/AFKModule/config".</param>
    /// <param name="configName">File name without extension, e.g. "base".</param>
    /// <param name="logger">Optional logger; defaults to <see cref="ModLogger.None"/> (silent).</param>
    /// <param name="defaultAsset">
    /// Optional mod asset location (e.g. "afkmodule:config/base.json") used instead of <c>new T()</c> to seed
    /// a brand-new file, for mods that ship their defaults as an asset rather than as field initializers.
    /// </param>
    public static T Load<T>(
        ICoreAPI api,
        string relativeDirectory,
        string configName,
        ModLogger? logger = null,
        string? defaultAsset = null
    ) where T : class, new()
    {
        logger ??= ModLogger.None;
        string directoryPath = Path.Combine(api.DataBasePath, relativeDirectory);
        string configPath = Path.Combine(directoryPath, $"{configName}.json");

        T BuildDefault()
        {
            if (defaultAsset != null)
            {
                T? fromAsset = api.Assets.Get(new AssetLocation(defaultAsset))?.ToObject<T>();
                if (fromAsset != null) return fromAsset;
                logger.LogError($"Cannot load default asset '{defaultAsset}', falling back to type defaults");
            }
            return new T();
        }

        void WriteDefault(T defaultConfig)
        {
            try
            {
                Directory.CreateDirectory(directoryPath);
                File.WriteAllText(configPath, JsonConvert.SerializeObject(defaultConfig, Formatting.Indented));
            }
            catch (Exception ex)
            {
                logger.LogError($"Cannot write default config to {configPath}: {ex.Message}");
            }
        }

        if (!File.Exists(configPath))
        {
            logger.Log($"Configuration '{configName}' not found, creating {configPath} with default values");
            T defaultConfig = BuildDefault();
            WriteDefault(defaultConfig);
            return defaultConfig;
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            logger.LogError($"Cannot read {configPath}: {ex.Message}. Using default values");
            return BuildDefault();
        }

        JObject fileObject;
        try
        {
            fileObject = JObject.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogError($"Configuration '{configName}' is not valid JSON ({ex.Message}), recreating it from defaults");
            BackupBrokenFile(configPath, logger);
            T defaultConfig = BuildDefault();
            WriteDefault(defaultConfig);
            return defaultConfig;
        }

        T result = new();
        Populate(fileObject.ToString(), result, configName, logger);

        BackfillMissingKeys(fileObject, result, configPath, configName, logger);

        return result;
    }

    /// <summary>Merges <paramref name="json"/> onto <paramref name="target"/> in place, keeping any field the JSON doesn't cover at its current value.</summary>
    private static void Populate<T>(string json, T target, string configName, ModLogger logger) where T : notnull
    {
        JsonSerializerSettings populateSettings = new()
        {
            // Without this, lists/dictionaries already holding default values get the loaded items
            // appended to them instead of replaced (Newtonsoft's default Reuse behavior for collections).
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Error = (_, args) =>
            {
                logger.LogError($"Configuration '{configName}': key '{args.ErrorContext.Path}' is invalid ({args.ErrorContext.Error.Message}), keeping default value");
                args.ErrorContext.Handled = true;
            }
        };
        JsonConvert.PopulateObject(json, target, populateSettings);
    }

    /// <summary>Convenience overload for the common "ModConfig/&lt;modFolderName&gt;/config/&lt;configName&gt;.json" layout.</summary>
    public static T LoadModConfig<T>(
        ICoreAPI api,
        string modFolderName,
        string configName,
        ModLogger? logger = null,
        string? defaultAsset = null
    ) where T : class, new()
        => Load<T>(api, $"ModConfig/{modFolderName}/config", configName, logger, defaultAsset);

    /// <summary>Serializes <paramref name="config"/> to &lt;api.DataBasePath&gt;/&lt;relativeDirectory&gt;/&lt;configName&gt;.json.</summary>
    public static void Save<T>(ICoreAPI api, string relativeDirectory, string configName, T config, ModLogger? logger = null)
    {
        logger ??= ModLogger.None;
        string directoryPath = Path.Combine(api.DataBasePath, relativeDirectory);
        string configPath = Path.Combine(directoryPath, $"{configName}.json");
        try
        {
            Directory.CreateDirectory(directoryPath);
            File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
        catch (Exception ex)
        {
            logger.LogError($"Cannot save config to {configPath}: {ex.Message}");
        }
    }

    /// <summary>Convenience overload for the common "ModConfig/&lt;modFolderName&gt;/config/&lt;configName&gt;.json" layout.</summary>
    public static void SaveModConfig<T>(ICoreAPI api, string modFolderName, string configName, T config, ModLogger? logger = null)
        => Save(api, $"ModConfig/{modFolderName}/config", configName, config, logger);

    /// <summary>
    /// Registers <paramref name="serialize"/> to run whenever a player finishes joining, pushing its result to
    /// that player over the shared OpenConfiguration network channel under <paramref name="key"/>. Pair with a
    /// client-side <see cref="RegisterSync(ICoreClientAPI, string, Action{string})"/> using the same key.
    /// </summary>
    /// <remarks>
    /// Low-level building block for mods whose configuration isn't (yet) a plain <see cref="Load{T}"/> model —
    /// e.g. one assembled by hand from several sources. Mods using <see cref="Load{T}"/> should prefer
    /// <see cref="LoadSynced{T}(ICoreServerAPI, string, string, ModLogger?, string?)"/> instead.
    /// </remarks>
    /// <param name="key">Arbitrary identifier for this config, unique across every mod (e.g. "&lt;modid&gt;:&lt;name&gt;").</param>
    public static void RegisterSync(ICoreServerAPI api, string key, Func<string> serialize) => ConfigSync.RegisterServerProvider(key, serialize);

    /// <summary>
    /// Registers <paramref name="apply"/> to run whenever a packet for <paramref name="key"/> arrives from the
    /// server over the shared OpenConfiguration network channel. Pair with a server-side
    /// <see cref="RegisterSync(ICoreServerAPI, string, Func{string})"/> using the same key.
    /// </summary>
    /// <remarks>
    /// Low-level building block for mods whose configuration isn't (yet) a plain <see cref="Load{T}"/> model.
    /// Mods using <see cref="Load{T}"/> should prefer <see cref="LoadSynced{T}(ICoreClientAPI, string, string, Action{T}?, ModLogger?)"/> instead.
    /// </remarks>
    /// <param name="key">Arbitrary identifier for this config, matching the key used on the server.</param>
    public static void RegisterSync(ICoreClientAPI api, string key, Action<string> apply) => ConfigSync.RegisterClientHandler(key, apply);

    /// <summary>
    /// Server-side counterpart of <see cref="LoadSynced{T}(ICoreClientAPI, string, string, Action{T}?, ModLogger?)"/>.
    /// Loads the config exactly like <see cref="Load{T}"/>, then registers it to be pushed to every client as
    /// they join, so client code can rely on the server's values instead of its own local file.
    /// </summary>
    public static T LoadSynced<T>(
        ICoreServerAPI api,
        string relativeDirectory,
        string configName,
        ModLogger? logger = null,
        string? defaultAsset = null
    ) where T : class, new()
    {
        T config = Load<T>(api, relativeDirectory, configName, logger, defaultAsset);
        RegisterSync(api, SyncKey(relativeDirectory, configName), () => JsonConvert.SerializeObject(config));
        return config;
    }

    /// <summary>Convenience overload for the common "ModConfig/&lt;modFolderName&gt;/config/&lt;configName&gt;.json" layout.</summary>
    public static T LoadSyncedModConfig<T>(
        ICoreServerAPI api,
        string modFolderName,
        string configName,
        ModLogger? logger = null,
        string? defaultAsset = null
    ) where T : class, new()
        => LoadSynced<T>(api, $"ModConfig/{modFolderName}/config", configName, logger, defaultAsset);

    /// <summary>
    /// Client-side counterpart of <see cref="LoadSynced{T}(ICoreServerAPI, string, string, ModLogger?, string?)"/>.
    /// Returns a <typeparamref name="T"/> instance seeded with its type defaults; the same instance is then
    /// updated in place (fields already covered by the server's config get overwritten) once the server's
    /// value arrives, right after this client finishes joining. Nothing is read from the local disk, since the
    /// server's file is authoritative.
    /// </summary>
    /// <param name="onSynced">Optional callback invoked, with the same instance, right after it is updated.</param>
    public static T LoadSynced<T>(
        ICoreClientAPI api,
        string relativeDirectory,
        string configName,
        Action<T>? onSynced = null,
        ModLogger? logger = null
    ) where T : class, new()
    {
        logger ??= ModLogger.None;
        T config = new();
        RegisterSync(api, SyncKey(relativeDirectory, configName), json =>
        {
            Populate(json, config, configName, logger);
            onSynced?.Invoke(config);
        });
        return config;
    }

    /// <summary>Convenience overload for the common "ModConfig/&lt;modFolderName&gt;/config/&lt;configName&gt;.json" layout.</summary>
    public static T LoadSyncedModConfig<T>(
        ICoreClientAPI api,
        string modFolderName,
        string configName,
        Action<T>? onSynced = null,
        ModLogger? logger = null
    ) where T : class, new()
        => LoadSynced<T>(api, $"ModConfig/{modFolderName}/config", configName, onSynced, logger);

    private static string SyncKey(string relativeDirectory, string configName) => $"{relativeDirectory}/{configName}";

    private static void BackfillMissingKeys<T>(JObject fileObject, T defaults, string configPath, string configName, ModLogger logger) where T : notnull
    {
        JObject defaultObject = JObject.FromObject(defaults);
        bool changed = false;
        foreach (JProperty defaultProperty in defaultObject.Properties())
        {
            if (fileObject.ContainsKey(defaultProperty.Name)) continue;

            logger.LogWarn($"Configuration '{configName}': key '{defaultProperty.Name}' missing, adding it with its default value");
            fileObject[defaultProperty.Name] = defaultProperty.Value;
            changed = true;
        }

        if (!changed) return;

        try
        {
            File.WriteAllText(configPath, fileObject.ToString(Formatting.Indented));
        }
        catch (Exception ex)
        {
            logger.LogError($"Cannot save updated config to {configPath}: {ex.Message}");
        }
    }

    private static void BackupBrokenFile(string configPath, ModLogger logger)
    {
        try
        {
            string backupPath = Path.Combine(
                Path.GetDirectoryName(configPath) ?? "",
                $"{Path.GetFileNameWithoutExtension(configPath)}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}.json"
            );
            File.Copy(configPath, backupPath, overwrite: true);
            logger.LogWarn($"Broken configuration preserved at {backupPath}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Cannot back up broken config {configPath}: {ex.Message}");
        }
    }
}
