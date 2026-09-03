using Vintagestory.API.Common;

namespace OpenConfiguration;

/// <summary>
/// Small per-mod logging wrapper around <see cref="ILogger"/> that prefixes every line with the mod's name
/// and gates debug output behind <see cref="ExtendedLoggingEnabled"/>. Replaces the "Debug" static class
/// copy-pasted into every mod's Initialization.cs.
/// </summary>
/// <remarks>
/// Instances are not shared/static: this library is loaded once into the game process but used by many
/// different mods, each of which must create its own <see cref="ModLogger"/> so prefixes and the extended
/// logging flag don't leak between mods.
/// </remarks>
public class ModLogger
{
    /// <summary>A logger that discards everything. Useful as a default when no logging is desired.</summary>
    public static readonly ModLogger None = new(null, "");

    private readonly ILogger? logger;
    private readonly string prefix;

    /// <summary>When false, <see cref="LogDebug"/> calls are silently dropped.</summary>
    public bool ExtendedLoggingEnabled { get; set; }

    public ModLogger(ILogger? logger, string modName, bool extendedLoggingEnabled = false)
    {
        this.logger = logger;
        prefix = string.IsNullOrEmpty(modName) ? "" : $"[{modName}] ";
        ExtendedLoggingEnabled = extendedLoggingEnabled;
    }

    public void Log(string message) => logger?.Log(EnumLogType.Notification, prefix + message);

    public void LogDebug(string message)
    {
        if (ExtendedLoggingEnabled) logger?.Log(EnumLogType.Debug, prefix + message);
    }

    public void LogWarn(string message) => logger?.Log(EnumLogType.Warning, prefix + message);

    public void LogError(string message) => logger?.Log(EnumLogType.Error, prefix + message);
}
