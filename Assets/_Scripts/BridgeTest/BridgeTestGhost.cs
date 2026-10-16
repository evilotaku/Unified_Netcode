using Unity.Netcode;
using UnityEngine;

/// <summary>
/// N4E half of the bridge test prefab: the server moves the object in a circle (the GhostObject
/// replicates the transform) and cycles a hue that is replicated as a GhostField.
/// </summary>
public partial class BridgeTestGhost : GhostBehaviour
{
    public GhostField<float> Hue;

    public float Radius = 2f;
    public float Speed = 1f;

    Renderer m_Renderer;
    MaterialPropertyBlock m_Block;
    Vector3 m_Center;

    void Start()
    {
        m_Renderer = GetComponentInChildren<Renderer>();
        m_Block = new MaterialPropertyBlock();
        m_Center = transform.position;
    }

    void Update()
    {
        if (IsServer)
        {
            float angle = Time.time * Speed;
            transform.position = m_Center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Radius;
            Hue.Value = Mathf.Repeat(Time.time * 0.1f, 1f);
        }

        if (m_Renderer == null) return;
        m_Block.SetColor("_BaseColor", Color.HSVToRGB(Hue.Value, 0.8f, 1f));
        m_Renderer.SetPropertyBlock(m_Block);
    }
}
