using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
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
        [SerializeField] private float defaultDistance = 0.58f;
        [SerializeField] private float defaultVerticalOffsetFromHead = -0.82f;
        [SerializeField] private Transform coachTransform;
        [SerializeField] private RcpExperienceHUD experienceHud;
        [SerializeField] private RcpCompressionMonitor compressionMonitor;

        private readonly Dictionary<string, Renderer[]> _targetRenderers = new();
        private readonly Dictionary<string, Transform> _targetTransforms = new();
        private readonly Dictionary<Renderer, Color> _baseColors = new();
        private readonly HashSet<string> _hoveredTargets = new();
        private StepSystem _steps;
        private RcpMetronome _metronome;
        private string _activeTargetId;

        private static readonly Color ActiveColor = new(0.12f, 0.85f, 0.90f, 1f);
        private static readonly Color PulseEmission = new(0.10f, 0.70f, 0.85f, 1f);
        private static readonly Color AlertPulseEmission = new(1.0f, 0.28f, 0.10f, 1f);
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
            _metronome = FindFirstObjectByType<RcpMetronome>();
            if (experienceHud == null) experienceHud = FindFirstObjectByType<RcpExperienceHUD>();
            if (compressionMonitor == null) compressionMonitor = FindFirstObjectByType<RcpCompressionMonitor>();
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
        /// Ubica [Colocacion], [RCP_HUD] y [Coach] directamente frente al usuario (a nivel de piso/superficie)
        /// para alinear sobre el paciente real o maniquí en Passthrough.
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
            targetPos.y = XRSettings.isDeviceActive
                ? 0.08f
                : Mathf.Max(0.12f, cam.transform.position.y + defaultVerticalOffsetFromHead);

            transform.position = targetPos;
            transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);

            NotifyPlacementConfirmed();
        }

        /// <summary>Alinea Coach (a nivel de piso junto al paciente) y HUD tras confirmar posición del paciente.</summary>
        public void NotifyPlacementConfirmed()
        {
            if (coachTransform != null)
            {
                Vector3 coachBasePos = transform.position - transform.right * 0.56f + transform.forward * 0.14f;
                coachBasePos.y = Mathf.Max(0f, transform.position.y - 0.08f);
                coachTransform.position = coachBasePos;

                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 toCam = cam.transform.position - coachBasePos;
                    toCam.y = 0f;
                    if (toCam.sqrMagnitude > 0.001f)
                    {
                        coachTransform.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                    }
                }
            }

            if (experienceHud == null) experienceHud = FindFirstObjectByType<RcpExperienceHUD>();
            experienceHud?.SnapInFrontOfCamera();
        }

        private void Update()
        {
            if (string.IsNullOrEmpty(_activeTargetId)) return;
            if (!_targetRenderers.TryGetValue(_activeTargetId, out var renderers)) return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4.5f);
            bool alert = compressionMonitor != null &&
                         (compressionMonitor.CurrentState == CompressionQualityState.HandsAwayFromChest ||
                          compressionMonitor.CurrentState == CompressionQualityState.HandsIdleOnChest);

            if (_metronome != null && _metronome.IsActive)
            {
                pulse = Mathf.Max(pulse * 0.35f, _metronome.BeatPulse);
            }

            foreach (var kvp in _targetTransforms)
            {
                if (kvp.Value == null) continue;
                bool isActive = kvp.Key == _activeTargetId;
                bool isHovered = _hoveredTargets.Contains(kvp.Key);
                float targetScale = isHovered ? 1.14f : (isActive ? 1f + 0.06f * pulse : 1f);
                kvp.Value.localScale = Vector3.Lerp(kvp.Value.localScale, Vector3.one * targetScale, Time.deltaTime * 12f);
            }

            Color baseGlow = alert ? AlertPulseEmission : PulseEmission;
            Color emission = baseGlow * (0.6f + pulse * 1.8f);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mat = r.material;
                if (mat == null) continue;
                if (mat.HasProperty(EmissionColorId))
                {
                    mat.EnableKeyword("_EMISSION");
                    Color boost = _hoveredTargets.Contains(_activeTargetId) ? emission * 1.35f : emission;
                    mat.SetColor(EmissionColorId, boost);
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

                    Color baseCol = _baseColors.TryGetValue(r, out var orig) ? orig : ActiveColor;
                    Color targetColor = isActive
                        ? baseCol
                        : new Color(baseCol.r * 0.45f, baseCol.g * 0.45f, baseCol.b * 0.45f, Mathf.Min(baseCol.a, 0.72f));
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
            _targetTransforms.Clear();
            _baseColors.Clear();
            _hoveredTargets.Clear();

            foreach (var target in GetComponentsInChildren<SceneTarget>(true))
            {
                if (target == null || string.IsNullOrEmpty(target.TargetId)) continue;
                string id = target.TargetId;
                _targetTransforms[id] = target.transform;

                var interactable = target.GetComponent<XRSimpleInteractable>();
                if (interactable != null)
                {
                    interactable.hoverEntered.AddListener(_ => _hoveredTargets.Add(id));
                    interactable.hoverExited.AddListener(_ => _hoveredTargets.Remove(id));
                }

                var renderers = new List<Renderer>();
                foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.GetComponent<TMPro.TMP_Text>() != null || r.gameObject.name == "PillBg") continue;
                    renderers.Add(r);
                    if (r.sharedMaterial != null && r.sharedMaterial.HasProperty(BaseColorId))
                    {
                        _baseColors[r] = r.sharedMaterial.GetColor(BaseColorId);
                    }
                }

                _targetRenderers[id] = renderers.ToArray();
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
