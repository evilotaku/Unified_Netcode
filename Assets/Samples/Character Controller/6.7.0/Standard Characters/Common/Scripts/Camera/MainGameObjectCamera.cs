using UnityEngine;
using Unity.Scripting.LifecycleManagement;

public partial class MainGameObjectCamera : MonoBehaviour
{
    [AutoStaticsCleanup]
    public static Camera Instance;

    void Awake()
    {
        Instance = GetComponent<Camera>();
    }
}
