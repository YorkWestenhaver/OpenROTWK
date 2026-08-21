using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("DROPPED-R15; census §3.3: three carriers (GateProxy, ArnorGateProxy, " +
    "IsengardOrthancDoorProxy) and every reference that would instantiate them is commented " +
    "out (';Proxy = IsengardOrthancDoorProxy' in ereborbuildings.ini:6401 and " +
    "bluemountains.ini:4958, '//Proxy = IsengardOrthancDoorProxy' in toweroforthanc.ini:601). " +
    "Not on any map, skirmish or campaign. Parse retained.")]
public class GateProxyBehaviorModuleData : BehaviorModuleData
{
    internal static GateProxyBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<GateProxyBehaviorModuleData> FieldParseTable = new IniParseTable<GateProxyBehaviorModuleData>();

}
