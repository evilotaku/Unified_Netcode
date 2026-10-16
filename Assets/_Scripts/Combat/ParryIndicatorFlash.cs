using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// A star-shaped glint that pops up and fades out at a point on an attacker, facing the camera. Spawned by
    /// <see cref="CombatEffects"/> as the parry cue when no Parry Indicator Prefab is set. It makes its own sprite, so
    /// it needs no assets.
    /// </summary>
    public class ParryIndicatorFlash : MonoBehaviour
    {
        static Sprite s_Sprite;
        static Material s_Material;

        Transform m_Follow;
        Vector3 m_Offset;
        float m_Size;
        float m_Duration;
        float m_Age;
        Color m_Color;
        SpriteRenderer m_Renderer;

        /// <summary>Shows a flash of <paramref name="size"/> meters at <paramref name="follow"/>'s position plus <paramref name="offset"/>, following it.</summary>
        public static ParryIndicatorFlash Spawn(Transform follow, Vector3 offset, Color color, float size, float duration)
        {
            var go = new GameObject("ParryIndicatorFlash");
            var flash = go.AddComponent<ParryIndicatorFlash>();
            flash.m_Follow = follow;
            flash.m_Offset = offset;
            flash.m_Color = color;
            flash.m_Size = size;
            flash.m_Duration = Mathf.Max(duration, 0.01f);

            flash.m_Renderer = go.AddComponent<SpriteRenderer>();
            flash.m_Renderer.sprite = GetSprite();
            var material = GetMaterial();
            if (material != null) flash.m_Renderer.sharedMaterial = material;
            flash.m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            flash.m_Renderer.receiveShadows = false;
            flash.LateUpdate();
            return flash;
        }

        void LateUpdate()
        {
            m_Age += Time.deltaTime;
            float t = m_Age / m_Duration;
            if (t >= 1f || m_Follow == null)
            {
                Destroy(gameObject);
                return;
            }

            transform.position = m_Follow.position + m_Offset;
            var camera = Camera.main;
            if (camera != null)
            {
                // Face the camera, and nudge toward it so the attacker's own body doesn't hide the glint.
                var toCamera = camera.transform.position - transform.position;
                transform.position += toCamera.normalized * Mathf.Min(0.15f, toCamera.magnitude * 0.5f);
                transform.rotation = Quaternion.LookRotation(-toCamera, camera.transform.up);
            }

            // Pop up fast, then shrink and fade, with a slight spin.
            float grow = Mathf.Clamp01(t / 0.2f);
            float scale = m_Size * Mathf.Lerp(0.3f, 1f, 1f - (1f - grow) * (1f - grow)) * Mathf.Lerp(1f, 0.6f, t);
            transform.localScale = new Vector3(scale, scale, scale);
            transform.Rotate(0f, 0f, t * 45f, Space.Self);
            var color = m_Color;
            color.a *= 1f - t * t;
            m_Renderer.color = color;
        }

        static Material GetMaterial()
        {
            if (s_Material == null)
            {
                // Unlit and always included, so it renders in any pipeline without lights.
                var shader = Shader.Find("Sprites/Default");
                if (shader != null) s_Material = new Material(shader) { name = "ParryIndicatorFlash" };
            }
            return s_Material;
        }

        static Sprite GetSprite()
        {
            if (s_Sprite != null) return s_Sprite;

            // A soft round glow with four thin rays.
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ParryIndicatorFlash",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float glow = Mathf.Clamp01(1f - r / 0.6f);
                    glow = Mathf.Sqrt(glow) * 0.7f;
                    float core = Mathf.Clamp01((0.25f - r) / 0.08f);
                    float rays = Mathf.Max(Ray(u, v), Ray(v, u)) * Mathf.Clamp01(1f - r);
                    float a = Mathf.Clamp01(glow + core + rays);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            // 128 pixels per unit: the sprite is 1 m across at scale 1.
            s_Sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            s_Sprite.name = "ParryIndicatorFlash";
            return s_Sprite;
        }

        /// <summary>A thin ray along the <paramref name="along"/> axis, fading toward its tips.</summary>
        static float Ray(float along, float across)
        {
            float width = 0.07f * (1f - Mathf.Abs(along));
            return width <= 0f ? 0f : Mathf.Clamp01(1f - Mathf.Abs(across) / width);
        }
    }
}
