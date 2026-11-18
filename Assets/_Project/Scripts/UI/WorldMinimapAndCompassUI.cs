using UnityEngine;
using UnityEngine.UI;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

namespace CoralArchipelago.UI
{
    /// <summary>
    /// Open-world 2.000m x 2.000m Minimap and Top-Center Compass Bar for the Mobile HUD.
    /// Procedurally renders the 5-biome height/shoreline terrain map, tracks the local player's
    /// position & heading, and pins the 5 continent landmarks (Altar Camp, Rainforest, Crystal Canyon,
    /// Mangrove Swamp, Volcanic Temple).
    /// </summary>
    public class WorldMinimapAndCompassUI : MonoBehaviour
    {
        private struct ContinentLandmark
        {
            public string Name;
            public Vector2 WorldXZ;
        }

        private static readonly ContinentLandmark[] Landmarks =
        {
            new ContinentLandmark { Name = "Altar Camp", WorldXZ = new Vector2(0f, 0f) },
            new ContinentLandmark { Name = "Rainforest", WorldXZ = new Vector2(-420f, 40f) },
            new ContinentLandmark { Name = "Crystal Canyon", WorldXZ = new Vector2(380f, 40f) },
            new ContinentLandmark { Name = "Mangrove Swamp", WorldXZ = new Vector2(260f, -260f) },
            new ContinentLandmark { Name = "Volcanic Temple", WorldXZ = new Vector2(0f, 480f) }
        };

        private static readonly string[] CardinalLabels = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        [Header("World Scale")]
        [SerializeField] private float worldSize = 2000f;
        [SerializeField] private float maxMountainHeight = 135f;
        [SerializeField] private int mapTextureResolution = 128;

        [Header("Minimap UI References")]
        [SerializeField] private RawImage minimapMapImage;
        [SerializeField] private RectTransform playerMarkerRect;
        [SerializeField] private RectTransform[] landmarkMarkerRects;
        [SerializeField] private Text coordsAndBiomeText;

        [Header("Compass Bar References")]
        [SerializeField] private Text compassHeadingText;

        private Texture2D _generatedMapTexture;
        private float _textRefreshTimer;

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                Destroy(this);
                return;
            }
        }

        private void Start()
        {
            BuildMinimapOverviewTexture();
            PositionLandmarkMarkers();
            UpdateTelemetry(forceTextUpdate: true);
        }

        private void OnDestroy()
        {
            if (_generatedMapTexture != null)
            {
                Destroy(_generatedMapTexture);
                _generatedMapTexture = null;
            }
        }

        private void Update()
        {
            UpdateTelemetry(forceTextUpdate: false);
        }

        private void BuildMinimapOverviewTexture()
        {
            if (minimapMapImage == null)
                return;

            int size = Mathf.Clamp(mapTextureResolution, 64, 256);
            _generatedMapTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            float halfWorld = worldSize * 0.5f;
            float waterLevel = IslandGraphicsUpgrader.Instance != null ? IslandGraphicsUpgrader.Instance.waterLevel : -2.2f;
            float mapWidth = IslandGraphicsUpgrader.Instance != null ? IslandGraphicsUpgrader.Instance.worldSize : worldSize;
            float peakHeight = IslandGraphicsUpgrader.Instance != null ? IslandGraphicsUpgrader.Instance.maxMountainHeight : maxMountainHeight;
            worldSize = mapWidth;
            maxMountainHeight = peakHeight;
            halfWorld = worldSize * 0.5f;

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) / size;
                float wz = (v - 0.5f) * worldSize;

                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float wx = (u - 0.5f) * worldSize;

                    IslandGraphicsUpgrader.EvaluateWorldBiomeAndHeightStatic(
                        wx, wz, worldSize, maxMountainHeight,
                        out IslandGraphicsUpgrader.BiomeType biome,
                        out Color vertexColor);
                    float h = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(wx, wz, worldSize, maxMountainHeight);

                    Color c;
                    if (h <= waterLevel)
                    {
                        float shallowBlend = Mathf.Clamp01((h - (-3.5f)) / Mathf.Max(0.1f, waterLevel - (-3.5f)));
                        c = Color.Lerp(new Color(0.03f, 0.14f, 0.28f), new Color(0.10f, 0.54f, 0.62f), shallowBlend);
                    }
                    else
                    {
                        float hStep = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(wx + 12f, wz + 12f, worldSize, maxMountainHeight);
                        float shade = Mathf.Clamp(1.0f + (h - hStep) * 0.045f, 0.72f, 1.25f);
                        c = vertexColor * shade;

                        if (biome == IslandGraphicsUpgrader.BiomeType.CrystalCanyon)
                        {
                            c = Color.Lerp(c, new Color(0.18f, 0.78f, 0.96f), 0.25f);
                        }
                        else if (biome == IslandGraphicsUpgrader.BiomeType.VolcanicSummit && h > 95f)
                        {
                            c = Color.Lerp(c, new Color(0.95f, 0.42f, 0.14f), 0.35f);
                        }
                    }

                    c.a = 0.95f;
                    pixels[y * size + x] = c;
                }
            }

            _generatedMapTexture.SetPixels32(pixels);
            _generatedMapTexture.Apply(false, true);
            minimapMapImage.texture = _generatedMapTexture;
        }

        private void PositionLandmarkMarkers()
        {
            if (landmarkMarkerRects == null)
                return;

            int count = Mathf.Min(landmarkMarkerRects.Length, Landmarks.Length);
            for (int i = 0; i < count; i++)
            {
                RectTransform pin = landmarkMarkerRects[i];
                if (pin == null)
                    continue;

                Vector2 uv = WorldToMapUV(Landmarks[i].WorldXZ.x, Landmarks[i].WorldXZ.y);
                pin.anchorMin = uv;
                pin.anchorMax = uv;
                pin.anchoredPosition = Vector2.zero;
            }
        }

        private void UpdateTelemetry(bool forceTextUpdate)
        {
            Vector3 worldPos;
            float yawDegrees;

            if (NetworkPlayerController.LocalPlayer != null)
            {
                Transform playerTf = NetworkPlayerController.LocalPlayer.transform;
                worldPos = playerTf.position;
                yawDegrees = Camera.main != null
                    ? Camera.main.transform.eulerAngles.y
                    : playerTf.eulerAngles.y;
            }
            else if (Camera.main != null)
            {
                worldPos = Camera.main.transform.position;
                yawDegrees = Camera.main.transform.eulerAngles.y;
            }
            else
            {
                worldPos = Vector3.zero;
                yawDegrees = 0f;
            }

            if (playerMarkerRect != null)
            {
                Vector2 uv = WorldToMapUV(worldPos.x, worldPos.z);
                playerMarkerRect.anchorMin = uv;
                playerMarkerRect.anchorMax = uv;
                playerMarkerRect.anchoredPosition = Vector2.zero;
                playerMarkerRect.localRotation = Quaternion.Euler(0f, 0f, -yawDegrees);
            }

            _textRefreshTimer += Time.unscaledDeltaTime;
            if (!forceTextUpdate && _textRefreshTimer < 0.12f)
                return;
            _textRefreshTimer = 0f;

            if (coordsAndBiomeText != null)
            {
                IslandGraphicsUpgrader.EvaluateWorldBiomeAndHeightStatic(
                    worldPos.x, worldPos.z, worldSize, maxMountainHeight,
                    out IslandGraphicsUpgrader.BiomeType biome,
                    out _);

                coordsAndBiomeText.text = $"({Mathf.RoundToInt(worldPos.x)}, {Mathf.RoundToInt(worldPos.z)}) | {FormatBiomeName(biome)}";
            }

            if (compassHeadingText != null)
            {
                float normalizedYaw = Mathf.Repeat(yawDegrees, 360f);
                int cardinalIdx = Mathf.RoundToInt(normalizedYaw / 45f) % 8;
                string cardinal = CardinalLabels[cardinalIdx];

                Vector2 fwdXZ = new Vector2(Mathf.Sin(normalizedYaw * Mathf.Deg2Rad), Mathf.Cos(normalizedYaw * Mathf.Deg2Rad));
                Vector2 playerXZ = new Vector2(worldPos.x, worldPos.z);

                int bestIdx = 0;
                float bestScore = float.MinValue;
                float bestDist = 0f;

                for (int i = 0; i < Landmarks.Length; i++)
                {
                    Vector2 delta = Landmarks[i].WorldXZ - playerXZ;
                    float dist = delta.magnitude;
                    float alignment = dist > 5f ? Vector2.Dot(fwdXZ, delta / dist) : 0.85f;
                    if (alignment > bestScore)
                    {
                        bestScore = alignment;
                        bestIdx = i;
                        bestDist = dist;
                    }
                }

                compassHeadingText.text = $"{cardinal} {Mathf.RoundToInt(normalizedYaw):000}\u00b0   |   Ahead: {Landmarks[bestIdx].Name} ({Mathf.RoundToInt(bestDist)}m)";
            }
        }

        private Vector2 WorldToMapUV(float worldX, float worldZ)
        {
            float safeSize = Mathf.Max(1f, worldSize);
            float u = Mathf.Clamp01(worldX / safeSize + 0.5f);
            float v = Mathf.Clamp01(worldZ / safeSize + 0.5f);
            return new Vector2(u, v);
        }

        private static string FormatBiomeName(IslandGraphicsUpgrader.BiomeType biome)
        {
            switch (biome)
            {
                case IslandGraphicsUpgrader.BiomeType.StarterCoralBay: return "Altar Coral Bay";
                case IslandGraphicsUpgrader.BiomeType.AncientRainforest: return "Rainforest";
                case IslandGraphicsUpgrader.BiomeType.CrystalCanyon: return "Crystal Canyon";
                case IslandGraphicsUpgrader.BiomeType.MangroveSwamp: return "Mangrove Swamp";
                case IslandGraphicsUpgrader.BiomeType.VolcanicSummit: return "Volcanic Summit";
                default: return "Coral Continent";
            }
        }
    }
}
