using System.Collections.Generic;

namespace OpenConfiguration;

internal sealed class ModConfigIndex
{
    // folder name → file name (no .json extension) → raw JSON content
    public Dictionary<string, Dictionary<string, string>> Mods { get; set; } = new();
}
