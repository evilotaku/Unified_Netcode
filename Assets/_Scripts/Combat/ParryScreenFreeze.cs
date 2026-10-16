using System.Collections;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// The full-screen freeze frame shown on a parry. It captures the current frame, holds it on screen under the
    /// ParryScreenFreeze shader's effects, then fades back to the live view.
    /// It only covers the screen: the game, and netcode prediction, keep running underneath, so nothing has to catch
    /// up afterwards. Call <see cref="Play"/>; the overlay is created on first use.
    /// </summary>
    public class ParryScreenFreeze : MonoBehaviour
    {
        static ParryScreenFreeze s_Instance;

        Canvas m_Canvas;
        UnityEngine.UI.RawImage m_Image;
        Material m_Material;
        Material m_Source;
        RenderTexture m_Frame;
        Coroutine m_Running;

        static readonly int CenterId = Shader.PropertyToID("_Center");
        static readonly int ElapsedId = Shader.PropertyToID("_Elapsed");
        static readonly int StrengthId = Shader.PropertyToID("_Strength");
        static readonly int AspectId = Shader.PropertyToID("_Aspect");
        static readonly int FlipYId = Shader.PropertyToID("_FlipY");

        /// <summary>
        /// Freezes the screen for <paramref name="hold"/> seconds, then fades back over <paramref name="fade"/> seconds.
        /// </summary>
        /// <param name="material">A material using the Hidden/Combat/ParryScreenFreeze shader; its colors style the effect.</param>
        /// <param name="worldPoint">Where the burst centers, or null for the middle of the screen.</param>
        public static void Play(Material material, Vector3? worldPoint, float hold, float fade)
        {
            if (material == null) return;
            if (s_Instance == null)
            {
                var go = new GameObject("ParryScreenFreeze");
                DontDestroyOnLoad(go);
                s_Instance = go.AddComponent<ParryScreenFreeze>();
            }
            s_Instance.Begin(material, worldPoint, hold, fade);
        }

        void Awake()
        {
            // Non-interactive overlay above all other UI. No GraphicRaycaster, so it never blocks input.
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = 32000;

            var imageObject = new GameObject("Frame", typeof(RectTransform));
            imageObject.transform.SetParent(transform, false);
            var rect = (RectTransform)imageObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            m_Image = imageObject.AddComponent<UnityEngine.UI.RawImage>();
            m_Image.raycastTarget = false;
            m_Image.enabled = false;
        }

        void Begin(Material material, Vector3? worldPoint, float hold, float fade)
        {
            if (m_Source != material)
            {
                if (m_Material != null) Destroy(m_Material);
                m_Source = material;
                m_Material = new Material(material);
                m_Image.material = m_Material;
            }

            var center = new Vector2(0.5f, 0.5f);
            var camera = Camera.main;
            if (worldPoint.HasValue && camera != null)
            {
                var viewport = camera.WorldToViewportPoint(worldPoint.Value);
                if (viewport.z > 0f) center = new Vector2(Mathf.Clamp01(viewport.x), Mathf.Clamp01(viewport.y));
            }

            if (m_Running != null) StopCoroutine(m_Running);
            m_Running = StartCoroutine(Run(center, Mathf.Max(0f, hold), Mathf.Max(0.01f, fade)));
        }

        IEnumerator Run(Vector2 center, float hold, float fade)
        {
            // Capture a frame without the overlay in it, at the end of the frame when it's fully rendered.
            m_Image.enabled = false;
            yield return new WaitForEndOfFrame();

            int width = Screen.width, height = Screen.height;
            if (m_Frame == null || m_Frame.width != width || m_Frame.height != height)
            {
                if (m_Frame != null) m_Frame.Release();
                m_Frame = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "ParryFreezeFrame" };
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(m_Frame);

            m_Image.texture = m_Frame;
            m_Material.SetVector(CenterId, center);
            m_Material.SetFloat(AspectId, (float)width / height);
            m_Material.SetFloat(FlipYId, SystemInfo.graphicsUVStartsAtTop ? 1f : 0f);
            m_Image.enabled = true;

            // Unscaled time, so the effect plays out the same whatever Time.timeScale is.
            for (float elapsed = 0f; elapsed < hold + fade; elapsed += Time.unscaledDeltaTime)
            {
                m_Material.SetFloat(ElapsedId, elapsed);
                m_Material.SetFloat(StrengthId, elapsed < hold ? 1f : 1f - (elapsed - hold) / fade);
                yield return null;
            }

            m_Image.enabled = false;
            m_Running = null;
        }

        void OnDestroy()
        {
            if (m_Frame != null) m_Frame.Release();
            if (m_Material != null) Destroy(m_Material);
            if (s_Instance == this) s_Instance = null;
        }
    }
}
