using ProtoBuf;

namespace OpenConfiguration;

[ProtoContract]
internal class ModConfigSavePacket
{
    [ProtoMember(1)] public string Folder = "";
    [ProtoMember(2)] public string FileName = "";
    [ProtoMember(3)] public string Content = "";
}
