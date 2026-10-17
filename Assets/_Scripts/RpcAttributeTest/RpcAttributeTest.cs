using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

// Test of Netcode for Entities' code-generated RPCs: [CustomMessage] structs and [RPC] methods.
// Every receive is recorded in RpcTestLog so a test script can read what arrived where.
// See Documentation/UnifiedNetcode-GameObjects.md, "Code-generated RPCs".

/// <summary>[CustomMessage] without a Handle method: received messages are read with Netcode.RPC.Query.</summary>
[CustomMessage(SendDirection.ClientToServer)]
public partial struct RpcTestPolledMessage
{
    public int Value;
}

/// <summary>[CustomMessage] with a Handle method: Handle runs automatically on the receiver, and Query never sees the message.</summary>
[CustomMessage(SendDirection.ClientToServer)]
public partial struct RpcTestHandledMessage
{
    public int Value;

    void Handle() => RpcTestLog.Add($"RpcTestHandledMessage.Handle Value={Value}");
}

/// <summary>Static [RPC] methods. Calling one sends it; the body runs on the receiving side.</summary>
public partial class RpcTestStaticMethods
{
    [RPC(SendDirection.ClientToServer)]
    public static void ClientToServer(int value, float number, Vector3 vector, FixedString64Bytes text)
    {
        RpcTestLog.Add($"RpcTestStaticMethods.ClientToServer value={value} number={number} vector={vector} text={text}");
    }

    [RPC(SendDirection.ServerToClient)]
    public static void ServerToClient(int value)
    {
        RpcTestLog.Add($"RpcTestStaticMethods.ServerToClient value={value}");
    }
}

/// <summary>Records each received test RPC.</summary>
[NoAutoStaticsCleanup] // reset manually in ResetStatics
public static class RpcTestLog
{
    static readonly List<string> s_Entries = new();

    public static IReadOnlyList<string> Entries => s_Entries;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => s_Entries.Clear();

    public static void Add(string entry)
    {
        s_Entries.Add($"[frame {Time.frameCount}] {entry}");
        Debug.Log($"[RpcTest] {entry}");
    }

    /// <summary>Returns the entries recorded so far and clears them.</summary>
    public static string Drain()
    {
        var text = s_Entries.Count == 0 ? "(none)" : string.Join("\n", s_Entries);
        s_Entries.Clear();
        return text;
    }
}
