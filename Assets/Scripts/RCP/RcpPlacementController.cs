using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XRLabs.Core;
using XRLabs.Interactions;

namespace NasaVR.RCP
{
    /// <summary>
    /// Permite colocar y reorientar el grupo [Colocacion] sobre el paciente o maniquí real
    /// en Passthrough, además de resaltar visualmente el hotspot activo de cada paso.
    /// </summary>
    public class RcpPlacementController : MonoBehaviour
    {
        [Header("Colocación")]
        [SerializeField] private float defaultDistance = 1.05f;
        [SerializeField] private float defaultVerticalOffsetFromHead = -0.55f;
        [SerializeField] private bool placeOnStartInXr = true;

        private readonly Dictionary<string, Renderer[]> _targetRenderers = new();
        private readonly Dictionary<Renderer, Color> _baseColors = new();
        private StepSystem _steps;
        private RcpMetronome _metronome;
        private string _activeTargetId;

        private static readonly Color ActiveColor = new(0.12f, 0.85f, 0.90f, 1f);
        private static readonly Color PulseEmission = new(0.10f, 0.70f, 0.85f, 1f);
        private static readonly Color InactiveColor = new(0.32f, 0.42f, 0.50f, 0.55f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private void Awake()
        {
            CacheTargets();
        }

        private void Start()
        {
            if (placeOnStartInXr && XRSettings.isDeviceActive)
            {
                PlaceInFrontOfUser();
            }

            _metronome = FindFirstObjectByType<RcpMetronome>();
            if (SimulationManager.Instance != null && SimulationManager.Instance.Steps != null)
            {
                Bind(SimulationManager.Instance.Steps);
            }
        }

        private void OnDestroy()
        {
            if (_steps != null)
            {
                _steps.StepStarted -= HandleStepStarted;
            }
        }

        public void Bind(StepSystem steps)
        {
            if (_steps != null)
            {
                _steps.StepStarted -= HandleStepStarted;
            }

            _steps = steps;
            if (_steps == null) return;

            _steps.StepStarted += HandleStepStarted;
            if (_steps.CurrentStep != null)
            {
                UpdateHighlight(_steps.CurrentStep);
            }
        }

        /// <summary>
        /// Ubica [Colocacion] frente a la mirada horizontal del usuario para alinear los puntos
        /// sobre el paciente o maniquí real en Passthrough.
        /// </summary>
        public void PlaceInFrontOfUser()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 flatForward = cam.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.001f)
            {
                flatForward = Vector3.forward;
            }
            flatForward.Normalize();

            Vector3 targetPos = cam.transform.position + flatForward * defaultDistance;
            targetPos.y = Mathf.Max(0.15f, cam.transform.position.y + defaultVerticalOffsetFromHead);

            // Los marcadores heredados están en Z negativo respecto a [Colocacion]; ajustamos el pivote para quedar delante.
            transform.position = targetPos - Quaternion.LookRotation(flatForward, Vector3.up) * new Vector3(0f, 0.2f, -0.6f);
            transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
        }

        private void Update()
        {
            if (string.IsNullOrEmpty(_activeTargetId)) return;
            if (!_targetRenderers.TryGetValue(_activeTargetId, out var renderers)) return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4.5f);
            if (_metronome != null && _metronome.IsActive)
            {
                pulse = Mathf.Max(pulse * 0.35f, _metronome.BeatPulse);
            }

            Color emission = PulseEmission * (0.6f + pulse * 1.6f);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mat = r.material;
                if (mat == null) continue;
                if (mat.HasProperty(EmissionColorId))
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor(EmissionColorId, emission);
                }
            }
        }

        private void HandleStepStarted(StepDefinition step, int index, int total)
        {
            UpdateHighlight(step);
        }

        private void UpdateHighlight(StepDefinition step)
        {
            _activeTargetId = step != null ? step.pointAtTargetId : null;

            var activeSet = new HashSet<string>();
            if (step != null && step.activeTargetIds != null)
            {
                foreach (var id in step.activeTargetIds)
                {
                    if (!string.IsNullOrEmpty(id)) activeSet.Add(id);
                }
            }
            if (!string.IsNullOrEmpty(_activeTargetId)) activeSet.Add(_activeTargetId);

            foreach (var kvp in _targetRenderers)
            {
                bool isActive = activeSet.Contains(kvp.Key);
                foreach (var r in kvp.Value)
                {
                    if (r == null) continue;
                    var mat = r.material;
                    if (mat == null) continue;

                    Color targetColor = isActive ? ActiveColor : InactiveColor;
                    if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, targetColor);
                    if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, targetColor);

                    if (!isActive && mat.HasProperty(EmissionColorId))
                    {
                        mat.SetColor(EmissionColorId, Color.black);
                    }
                }
            }
        }

        private void CacheTargets()
        {
            _targetRenderers.Clear();
            _baseColors.Clear();

            foreach (var target in GetComponentsInChildren<SceneTarget>(true))
            {
                if (target == null || string.IsNullOrEmpty(target.TargetId)) continue;
                var renderers = new List<Renderer>();
                foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.GetComponent<TMPro.TMP_Text>() != null) continue;
                    renderers.Add(r);
                    if (r.sharedMaterial != null && r.sharedMaterial.HasProperty(BaseColorId))
                    {
                        _baseColors[r] = r.sharedMaterial.GetColor(BaseColorId);
                    }
                }

                _targetRenderers[target.TargetId] = renderers.ToArray();
            }

            // Asegura que todos los hotspots permitan toque directo y tengan collider suficiente para manos/rayo.
            foreach (var hotspot in GetComponentsInChildren<TouchableHotspot>(true))
            {
                if (hotspot == null) continue;
                var col = hotspot.GetComponent<Collider>();
                if (col == null)
                {
                    var sphere = hotspot.gameObject.AddComponent<SphereCollider>();
                    sphere.radius = 0.09f;
                }
            }
        }
    }
}
