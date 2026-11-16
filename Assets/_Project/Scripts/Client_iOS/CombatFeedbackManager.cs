using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using CoralArchipelago.Networking;

namespace CoralArchipelago.ClientIOS
{
    /// <summary>
    /// Handles Apple Taptic Engine haptics, quick target scale-punch reactions,
    /// and lightweight pooled floating damage/resource popups on iOS & Editor clients.
    /// </summary>
    public class CombatFeedbackManager : MonoBehaviour
    {
        public static CombatFeedbackManager Instance { get; private set; }

        private class FloatingPopup
        {
            public Vector3 WorldPosition;
            public string Text;
            public Color Color;
            public float Age;
            public float Lifetime;
        }

        [SerializeField] private float popupLifetime = 1.05f;
        [SerializeField] private float popupRiseSpeed = 1.35f;

        private readonly List<FloatingPopup> _activePopups = new List<FloatingPopup>();
        private readonly HashSet<Transform> _activePunches = new HashSet<Transform>();
        private GUIStyle _popupStyle;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void _TriggeriOSHapticFeedback(int style);
#endif

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                Destroy(gameObject);
                return;
            }

            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            for (int i = _activePopups.Count - 1; i >= 0; i--)
            {
                FloatingPopup p = _activePopups[i];
                p.Age += Time.deltaTime;
                p.WorldPosition += Vector3.up * (popupRiseSpeed * Time.deltaTime);

                if (p.Age >= p.Lifetime)
                {
                    _activePopups.RemoveAt(i);
                }
            }
        }

        private void OnGUI()
        {
            if (_activePopups.Count == 0 || Camera.main == null)
                return;

            if (_popupStyle == null)
            {
                _popupStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 24,
                    fontStyle = FontStyle.Bold
                };
            }

            Camera cam = Camera.main;
            for (int i = 0; i < _activePopups.Count; i++)
            {
                FloatingPopup p = _activePopups[i];
                Vector3 screenPos = cam.WorldToScreenPoint(p.WorldPosition);
                if (screenPos.z <= 0f)
                    continue;

                float alpha = 1f - Mathf.Clamp01(p.Age / p.Lifetime);
                Color c = p.Color;
                c.a = alpha;
                _popupStyle.normal.textColor = c;

                float guiY = Screen.height - screenPos.y;
                Rect rect = new Rect(screenPos.x - 110f, guiY - 22f, 220f, 44f);
                GUI.Label(rect, p.Text, _popupStyle);
            }
        }

        public void TriggerHaptic(int style = 0)
        {
#if UNITY_IOS && !UNITY_EDITOR
            _TriggeriOSHapticFeedback(Mathf.Clamp(style, 0, 2));
#endif
        }

        public void SpawnWorldPopup(Vector3 worldPosition, string text, Color color)
        {
            if (string.IsNullOrEmpty(text))
                return;

            _activePopups.Add(new FloatingPopup
            {
                WorldPosition = worldPosition,
                Text = text,
                Color = color,
                Age = 0f,
                Lifetime = popupLifetime
            });
        }

        public void PlayTargetPunch(Transform target)
        {
            if (target == null || _activePunches.Contains(target))
                return;

            StartCoroutine(PunchRoutine(target));
        }

        private IEnumerator PunchRoutine(Transform target)
        {
            _activePunches.Add(target);
            Vector3 originalScale = target.localScale;
            Vector3 peakScale = originalScale * 1.14f;

            float elapsed = 0f;
            const float duration = 0.14f;
            while (elapsed < duration && target != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float curve = Mathf.Sin(t * Mathf.PI);
                target.localScale = Vector3.Lerp(originalScale, peakScale, curve);
                yield return null;
            }

            if (target != null)
                target.localScale = originalScale;

            _activePunches.Remove(target);
        }
    }
}
