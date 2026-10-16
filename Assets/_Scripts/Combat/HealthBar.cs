using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Shows a <see cref="Health"/>: a bar over other players' heads, facing the camera, and a bar at the bottom of the
    /// screen for the local player, with a respawn countdown while dead. A light trail shows recent damage draining.
    /// Cosmetic, clients only. It makes its own sprites, so it needs no assets.
    /// Put this on the player ghost prefab next to <see cref="Health"/>.
    /// </summary>
    public class HealthBar : MonoBehaviour
    {
        public Health Health;

        [Header("Over the head (other players)")]
        [Tooltip("Height of the bar above the player's root.")]
        public float Height = 2.15f;
        public Vector2 Size = new(0.8f, 0.08f);
        [Tooltip("Show the bar at full health too. Off: it appears once the player is hurt.")]
        public bool ShowWhenFull;
        [Tooltip("Bars farther than this from the camera are hidden.")]
        public float MaxDistance = 25f;

        [Header("On screen (local player)")]
        public bool ShowLocalBar = true;
        [Tooltip("Width and height in pixels at 1080p; scaled with the screen height.")]
        public Vector2 LocalSize = new(360f, 18f);

        [Header("Colors")]
        public Color FullColor = new(0.3f, 0.9f, 0.3f, 1f);
        public Color LowColor = new(0.95f, 0.2f, 0.15f, 1f);
        public Color TrailColor = new(1f, 0.95f, 0.8f, 0.9f);
        public Color BackgroundColor = new(0f, 0f, 0f, 0.6f);
        [Tooltip("Seconds before the damage trail starts draining, and how fast it drains (fraction per second).")]
        public float TrailDelay = 0.4f;
        public float TrailSpeed = 0.8f;

        static Sprite s_Sprite;
        static Material s_Material;
        static Texture2D s_White;

        FPSAnimatorGhost m_Animator;
        Transform m_Root;
        SpriteRenderer m_Background, m_Trail, m_Fill;
        float m_TrailFraction = 1f;
        float m_TrailWait;
        float m_LastFraction = 1f;
        bool m_Initialized;

        void Update()
        {
            if (!m_Initialized && !Initialize()) return;

            float fraction = Health.Fraction;
            if (fraction < m_LastFraction) m_TrailWait = TrailDelay;
            m_LastFraction = fraction;
            if (m_TrailWait > 0f) m_TrailWait -= Time.deltaTime;
            else m_TrailFraction = Mathf.MoveTowards(m_TrailFraction, fraction, TrailSpeed * Time.deltaTime);
            if (m_TrailFraction < fraction) m_TrailFraction = fraction;
        }

        void LateUpdate()
        {
            if (!m_Initialized) return;
            var camera = Camera.main;
            bool show = !IsLocalPlayer && !Health.IsDead.Value && camera != null &&
                        (ShowWhenFull || Health.Fraction < 1f || m_TrailFraction > Health.Fraction) &&
                        (camera.transform.position - transform.position).sqrMagnitude <= MaxDistance * MaxDistance;
            m_Root.gameObject.SetActive(show);
            if (!show) return;

            // Face the same way as the camera, so the bar reads flat on screen.
            m_Root.SetPositionAndRotation(transform.position + Vector3.up * Height, camera.transform.rotation);
            SetSegment(m_Trail, m_TrailFraction, TrailColor, -0.001f);
            SetSegment(m_Fill, Health.Fraction, BarColor(Health.Fraction), -0.002f);
        }

        void SetSegment(SpriteRenderer segment, float fraction, Color color, float towardCamera)
        {
            // The sprite is pivoted on its left edge, so scaling x fills from the left.
            segment.transform.localPosition = new Vector3(-Size.x * 0.5f, 0f, towardCamera);
            segment.transform.localScale = new Vector3(Size.x * fraction, Size.y, 1f);
            segment.color = color;
        }

        Color BarColor(float fraction) => Color.Lerp(LowColor, FullColor, Mathf.InverseLerp(0.2f, 0.7f, fraction));

        void OnGUI()
        {
            if (!m_Initialized || !ShowLocalBar || !IsLocalPlayer) return;

            float scale = Screen.height / 1080f;
            float width = LocalSize.x * scale, height = LocalSize.y * scale;
            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - 40f * scale, width, height);
            DrawRect(rect, BackgroundColor);
            DrawRect(new Rect(rect.x, rect.y, width * m_TrailFraction, height), TrailColor);
            DrawRect(new Rect(rect.x, rect.y, width * Health.Fraction, height), BarColor(Health.Fraction));

            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.RoundToInt(14f * scale),
                fontStyle = FontStyle.Bold,
            };
            style.normal.textColor = Color.white;
            GUI.Label(rect, $"{Mathf.CeilToInt(Health.Current.Value)} / {Mathf.CeilToInt(Health.MaxHealth)}", style);

            if (Health.IsDead.Value)
            {
                style.fontSize = Mathf.RoundToInt(36f * scale);
                string text = Health.RespawnDelay >= 0f
                    ? $"Defeated\nRespawning in {Mathf.CeilToInt(Health.RespawnRemaining.Value)}"
                    : "Defeated";
                GUI.Label(new Rect(0f, Screen.height * 0.3f, Screen.width, 120f * scale), text, style);
            }
        }

        static void DrawRect(Rect rect, Color color)
        {
            if (s_White == null)
            {
                s_White = new Texture2D(1, 1) { name = "HealthBarWhite" };
                s_White.SetPixel(0, 0, Color.white);
                s_White.Apply();
            }
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, s_White);
            GUI.color = previous;
        }

        bool Initialize()
        {
            if (Health == null) Health = GetComponent<Health>();
            if (Health == null) { enabled = false; return false; }
            var ghost = Health.Ghost;
            if (ghost == null || !(ghost.IsClient || ghost.IsServer)) return false; // not spawned yet
            if (!ghost.IsClient) { enabled = false; return false; } // server-only copy: nothing to show

            m_Animator = GetComponent<FPSAnimatorGhost>();
            m_Root = new GameObject("HealthBar").transform;
            m_Root.SetParent(transform, false);
            m_Background = CreateSegment("Background", 0);
            m_Background.transform.localPosition = new Vector3(-Size.x * 0.5f, 0f, 0f);
            m_Background.transform.localScale = new Vector3(Size.x, Size.y, 1f);
            m_Background.color = BackgroundColor;
            m_Trail = CreateSegment("Trail", 1);
            m_Fill = CreateSegment("Fill", 2);
            m_TrailFraction = m_LastFraction = Health.Fraction;
            m_Initialized = true;
            return true;
        }

        bool IsLocalPlayer => m_Animator != null && m_Animator.IsLocalOwner;

        SpriteRenderer CreateSegment(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_Root, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = GetSprite();
            if (s_Material == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader != null) s_Material = new Material(shader) { name = "HealthBar" };
            }
            if (s_Material != null) renderer.sharedMaterial = s_Material;
            renderer.sortingOrder = order;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        static Sprite GetSprite()
        {
            if (s_Sprite != null) return s_Sprite;
            var texture = new Texture2D(4, 4) { name = "HealthBar", filterMode = FilterMode.Point };
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            // 1 unit across, pivoted on the left edge.
            s_Sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0f, 0.5f), 4f);
            s_Sprite.name = "HealthBar";
            return s_Sprite;
        }
    }
}
