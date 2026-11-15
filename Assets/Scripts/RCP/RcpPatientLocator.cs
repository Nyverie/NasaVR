using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using XRLabs.Core;

namespace NasaVR.RCP
{
    /// <summary>
    /// Paso 0 (Passthrough AR): permite ubicar al paciente / maniquí sobre el piso real
    /// apuntando con la mirada o controlador antes de fijar todo el kit de RCP ([Colocacion], HUD y Coach).
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

        private void Awake()
        {
            if (placementController == null) placementController = GetComponent<RcpPlacementController>();
            if (experienceHud == null) experienceHud = FindFirstObjectByType<RcpExperienceHUD>();
            if (stepSystem == null) stepSystem = FindFirstObjectByType<StepSystem>();
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

            UpdateFloorTargetPose();

            // En Editor/Desktop permite confirmar rápido con Espacio o Enter.
            if (!XRSettings.isDeviceActive && Keyboard.current != null &&
                (Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.enterKey.wasPressedThisFrame))
            {
                ConfirmPlacement();
                return;
            }

            // En XR permite confirmar con Gatillo/Select tras un breve respiro de 0.35s.
            if (XRSettings.isDeviceActive && Time.time - _enteredPlacementAt > 0.35f)
            {
                if (IsAnyXrSelectPressedThisFrame())
                {
                    ConfirmPlacement();
                }
            }
        }

        public void BeginPlacement()
        {
            IsPlacingPatient = true;
            _enteredPlacementAt = Time.time;

            if (floorReticle != null) floorReticle.gameObject.SetActive(true);

            // Detiene la voz actual mientras el usuario alinea la silueta sobre el paciente en el piso.
            VOManager.Instance?.StopAll();
            experienceHud?.SetPlacementModeState(true);
            UpdateFloorTargetPose();
        }

        public void ConfirmPlacement()
        {
            if (!IsPlacingPatient && HasConfirmedPlacement) return;

            IsPlacingPatient = false;
            HasConfirmedPlacement = true;

            if (floorReticle != null) floorReticle.gameObject.SetActive(false);
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
            if (floorReticle != null) floorReticle.gameObject.SetActive(false);
            experienceHud?.SetPlacementModeState(false);
        }

        private void UpdateFloorTargetPose()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 flatForward = cam.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            flatForward.Normalize();

            // Piso real: en XR FloorLevel es y = 0; en Editor sin suelo raycast usamos cam.y - 1.15m.
            float floorY = XRSettings.isDeviceActive
                ? defaultFloorY
                : Mathf.Max(0f, cam.transform.position.y - 1.05f);

            Vector3 targetPoint = cam.transform.position + flatForward * 1.15f;
            targetPoint.y = floorY;

            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, floorRayMask, QueryTriggerInteraction.Ignore))
            {
                if (!hit.transform.IsChildOf(transform))
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

            transform.position = new Vector3(
                cam.transform.position.x + dir.x * dist,
                targetPoint.y + 0.08f, // Pecho ligeramente elevado sobre el piso/colchoneta
                cam.transform.position.z + dir.y * dist);
            transform.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y), Vector3.up);
        }

        private static bool IsAnyXrSelectPressedThisFrame()
        {
            var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, devices);
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool trigger) && trigger)
                    return true;
            }
            return false;
        }
    }
}
