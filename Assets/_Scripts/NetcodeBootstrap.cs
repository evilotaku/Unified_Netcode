using Unity.Entities;
using Unity.Netcode;

// Disabled so NGO's UnifiedBootstrap is the only bootstrap (unified/hybrid mode). Remove the attribute to restore.
[DisableBootstrapOverrides]
public class NetcodeBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        AutoConnectPort = 0;
               
        return base.Initialize(defaultWorldName);
    }
}