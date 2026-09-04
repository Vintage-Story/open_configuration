using ProtoBuf;

namespace OpenConfiguration;

/// <summary>Wire format for <see cref="ConfigSync"/>: a JSON blob addressed by an arbitrary sync key.</summary>
[ProtoContract]
internal class ConfigSyncPacket
{
    [ProtoMember(1)]
    public string Key = "";

    [ProtoMember(2)]
    public string Json = "";
}
