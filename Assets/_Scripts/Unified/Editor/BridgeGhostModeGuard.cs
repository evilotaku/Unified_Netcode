using System;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// NGO's NetworkObjectBridge.OnValidate forces GhostObject.SupportedGhostModes to Interpolated whenever the
/// prefab is validated (including while it is being saved, so the serialized value can't be fixed on disk).
/// That breaks predicted bridged prefabs such as FPS_Player (DefaultGhostMode OwnerPredicted).
/// This restores SupportedGhostModes to All on the loaded prefab for any bridged prefab whose default mode
/// isn't Interpolated, when entering Play mode (before NGO creates its worlds and registers ghost prefabs)
/// and before a player build. It also turns off the host interpolation smoothing the bridge enables for Rigidbodies.
/// The Inspector may still show Interpolated in Edit mode; that's expected.
/// Remove once the bridge stops overriding the ghost modes.
/// </summary>
[InitializeOnLoad]
class BridgeGhostModeGuard : IPreprocessBuildWithReport
{
    static readonly Type s_BridgeType = typeof(NetworkObject).Assembly.GetType("Unity.Netcode.NetworkObjectBridge");

    static BridgeGhostModeGuard()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode)
                FixAll();
        };
    }

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report) => FixAll();

    internal static void FixAll()
    {
        if (s_BridgeType == null) return;

        foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab == null || prefab.GetComponent(s_BridgeType) == null) continue;
            if (!prefab.TryGetComponent<GhostObject>(out var ghost)) continue;
            if (ghost.DefaultGhostMode == GhostMode.Interpolated) continue;

            ghost.SupportedGhostModes = GhostModeMask.All;
            // The bridge also turns on host interpolation smoothing for prefabs with a Rigidbody. On a Single World host
            // that overwrites the predicted transform with the interpolated pose every frame, discarding rotation set in PredictionUpdate.
            ghost.SingleWorldHostInterpolationSmoothing = SingleWorldHostInterpolationMode.Disabled;
        }
    }
}
