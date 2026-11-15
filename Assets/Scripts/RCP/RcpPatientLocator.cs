using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using XRLabs.Core;

namespace NasaVR.RCP
{
    /// <summary>
    /// Paso 0 (Passthrough AR): mantiene [Colocacion] y el HUD quietos en el piso sin seguir la cámara.
    /// Solo proyecta el reticle del piso adonde apunta el usuario y fija el kit al confirmar.
    /// </summary>
    public class RcpPatientLocator : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private RcpPlacementController placementController;
        [SerializeField] private RcpExperienceHUD experienceHud;
        [SerializeField] private StepSystem stepSystem;
        [SerializeField] private Transform floorReticle;

        [Header("Ubicación en piso")]
        [SerializeField] private bool startInPlacementMode = true;
        [SerializeField] private float defaultFloorY = 0f;
        [SerializeField] private float maxRayDistance = 4.5f;
        [SerializeField] private float minHorizontalDistance = 0.55f;
        [SerializeField] private float maxHorizontalDistance = 2.60f;
        [SerializeField] private LayerMask floorRayMask = ~0;

        public bool IsPlacingPatient { get; private set; }
        public bool HasConfirmedPlacement { get; private set; }

        private float _enteredPlacementAt;
        private Vector3 _pendingTargetPosition;
        private Quaternion _pendingTargetRotation;
        private bool _hasPendingTarget;
        private Vector3 _reticleDefaultLocalPos;

        private void Awake()
        {
            if (placementController == null) placementController = GetComponent<RcpPlacementController>();
            if (experienceHud == null) experienceHud = FindFirstObjectByType<RcpExperienceHUD>();
            if (stepSystem == null) stepSystem = FindFirstObjectByType<StepSystem>();
            if (floorReticle != null) _reticleDefaultLocalPos = floorReticle.localPosition;
        }

        private void Start()
        {
            if (startInPlacementMode && !HasConfirmedPlacement)
            {
                BeginPlacement();
            }
            else if (floorReticle != null)
            {
                floorReticle.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (!IsPlacingPatient) return;

            UpdateFloorReticlePose();

            if (!XRSettings.isDeviceActive && Keyboard.current != null &&
                (Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.enterKey.wasPressedThisFrame))
            {
                ConfirmPlacement();
                return;
            }

            if (XRSettings.isDeviceActive && Time.time - _enteredPlacementAt > 0.45f)
            {
                if (RcpHandInputHelper.WasSelectPressedThisFrame())
                {
                    ConfirmPlacement();
                }
            }
        }

        public void BeginPlacement()
        {
            IsPlacingPatient = true;
            _enteredPlacementAt = Time.time;
            _hasPendingTarget = false;

            if (floorReticle != null)
            {
                // Desacoplamos temporalmente solo el reticle para que el cuerpo y el HUD nunca persigan la cámara.
                floorReticle.SetParent(null, true);
                floorReticle.gameObject.SetActive(true);
            }

            VOManager.Instance?.StopAll();
            experienceHud?.SetPlacementModeState(true);
            placementController?.SetAllHotspotsInteractive(false);
            UpdateFloorReticlePose();
        }

        public void ConfirmPlacement()
        {
            if (!IsPlacingPatient && HasConfirmedPlacement) return;

            IsPlacingPatient = false;
            HasConfirmedPlacement = true;

            if (_hasPendingTarget)
            {
                transform.position = _pendingTargetPosition;
                transform.rotation = _pendingTargetRotation;
            }

            if (floorReticle != null)
            {
                floorReticle.SetParent(transform, false);
                floorReticle.localPosition = _reticleDefaultLocalPos;
                floorReticle.localRotation = Quaternion.identity;
                floorReticle.gameObject.SetActive(false);
            }

            experienceHud?.SetPlacementModeState(false);
            placementController?.NotifyPlacementConfirmed();

            if (stepSystem == null) stepSystem = FindFirstObjectByType<StepSystem>();
            if (stepSystem != null)
            {
                stepSystem.StartSteps(XRLabs.Core.SimulationMode.Guided);
            }
        }

        /// <summary>Desactiva el Paso 0 para pruebas automatizadas que inician los pasos directo.</summary>
        public void SkipPlacementForTests()
        {
            IsPlacingPatient = false;
            HasConfirmedPlacement = true;
            if (floorReticle != null)
            {
                floorReticle.SetParent(transform, false);
                floorReticle.localPosition = _reticleDefaultLocalPos;
                floorReticle.gameObject.SetActive(false);
            }
            experienceHud?.SetPlacementModeState(false);
        }

        private void UpdateFloorReticlePose()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 flatForward = cam.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            flatForward.Normalize();

            float floorY = XRSettings.isDeviceActive
                ? defaultFloorY
                : Mathf.Max(0f, cam.transform.position.y - 1.05f);

            Vector3 targetPoint = cam.transform.position + flatForward * 0.90f;
            targetPoint.y = floorY;

            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, floorRayMask, QueryTriggerInteraction.Ignore))
            {
                if (!hit.transform.IsChildOf(transform) && (floorReticle == null || !hit.transform.IsChildOf(floorReticle)))
                {
                    targetPoint = hit.point;
                }
            }
            else if (Mathf.Abs(cam.transform.forward.y) > 0.05f)
            {
                float t = (floorY - cam.transform.position.y) / cam.transform.forward.y;
                if (t > 0.35f && t < maxRayDistance)
                {
                    targetPoint = cam.transform.position + cam.transform.forward * t;
                    targetPoint.y = floorY;
                }
            }

            Vector3 delta = targetPoint - cam.transform.position;
            Vector2 horiz = new Vector2(delta.x, delta.z);
            float dist = Mathf.Clamp(horiz.magnitude, minHorizontalDistance, maxHorizontalDistance);
            Vector2 dir = horiz.sqrMagnitude > 0.0001f ? horiz.normalized : new Vector2(flatForward.x, flatForward.z);

            _pendingTargetPosition = new Vector3(
                cam.transform.position.x + dir.x * dist,
                targetPoint.y + 0.08f,
                cam.transform.position.z + dir.y * dist);
            _pendingTargetRotation = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y), Vector3.up);
            _hasPendingTarget = true;

            if (floorReticle != null)
            {
                floorReticle.position = new Vector3(_pendingTargetPosition.x, targetPoint.y + 0.012f, _pendingTargetPosition.z);
                floorReticle.rotation = _pendingTargetRotation;
            }
        }
    }
}
