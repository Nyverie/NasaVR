using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using XRLabs.MenuXRI;

namespace NasaVR.RCP
{
    /// <summary>
    /// Coordina el encendido real entre XRLabsRig (Quest / Quest Link) y FlatCamera (Desktop).
    /// A diferencia de PlatformBootstrapper (que solo evalúa en Awake -100), monitorea la inicialización
    /// de OpenXR/XRManagement en múltiples cuadros, garantiza las cámaras de Passthrough y alinea
    /// [Colocacion], [RCP_HUD] y [Coach] frente al usuario tras el asentamiento del tracking.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class RcpRigCoordinator : MonoBehaviour
    {
        [SerializeField] private GameObject xrRigRoot;
        [SerializeField] private GameObject flatRigRoot;
        [SerializeField] private RcpPassthroughSetup passthroughSetup;
        [SerializeField] private RcpPlacementController placementController;
        [SerializeField] private RcpExperienceHUD experienceHud;
        [SerializeField] private Transform coachRoot;

        private bool? _lastXrState;
        private bool _pendingInitialPlacement = true;
        private bool _initialPlacementCompleted;
        private int _xrFramesAfterActivation;
        private bool? _forcedXrForTests;

        public GameObject XrRigRoot => xrRigRoot;
        public GameObject FlatRigRoot => flatRigRoot;
        public bool IsUsingXrRig => xrRigRoot != null && xrRigRoot.activeSelf;

        private void Awake()
        {
            ResolveDependencies();
            ApplyRigState(IsXrRuntimeActive(), forcePlacement: true);
        }

        private void Start()
        {
            ApplyRigState(IsXrRuntimeActive(), forcePlacement: false);
        }

        private void Update()
        {
            bool xrActive = IsXrRuntimeActive();
            if (!_lastXrState.HasValue || _lastXrState.Value != xrActive)
            {
                ApplyRigState(xrActive, forcePlacement: true);
            }

            if (_pendingInitialPlacement && !_initialPlacementCompleted)
            {
                if (!xrActive)
                {
                    PlaceExperienceInFrontOfUser();
                    _pendingInitialPlacement = false;
                    _initialPlacementCompleted = true;
                }
                else
                {
                    _xrFramesAfterActivation++;
                    var aligner = xrRigRoot != null ? xrRigRoot.GetComponent<RigHeadingAligner>() : null;
                    bool aligned = aligner != null && aligner.Alignments > 0;
                    if ((aligned && _xrFramesAfterActivation >= 2) || _xrFramesAfterActivation >= 12)
                    {
                        PlaceExperienceInFrontOfUser();
                        _pendingInitialPlacement = false;
                        _initialPlacementCompleted = true;
                    }
                }
            }
        }

        public void ConfigureReferences(
            GameObject xrRig,
            GameObject flatRig,
            RcpPassthroughSetup passthrough,
            RcpPlacementController placement,
            RcpExperienceHUD hud,
            Transform coach)
        {
            xrRigRoot = xrRig;
            flatRigRoot = flatRig;
            passthroughSetup = passthrough;
            placementController = placement;
            experienceHud = hud;
            coachRoot = coach;
        }

        /// <summary>Permite simular modo XR o Flat en pruebas automatizadas PlayMode.</summary>
        public void ForceModeForTests(bool xrActive)
        {
            _forcedXrForTests = xrActive;
            ApplyRigState(xrActive, forcePlacement: true);
            PlaceExperienceInFrontOfUser();
            _pendingInitialPlacement = false;
        }

        public static bool IsXrRuntimeActive()
        {
            if (XRSettings.isDeviceActive) return true;

            var xrSettings = XRGeneralSettings.Instance;
            if (xrSettings != null && xrSettings.Manager != null && xrSettings.Manager.activeLoader != null)
            {
                return true;
            }

            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            for (int i = 0; i < displays.Count; i++)
            {
                if (displays[i] != null && displays[i].running)
                {
                    return true;
                }
            }

            return false;
        }

        private void ResolveDependencies()
        {
            if (passthroughSetup == null) passthroughSetup = FindFirstObjectByType<RcpPassthroughSetup>();
            if (placementController == null) placementController = FindFirstObjectByType<RcpPlacementController>();
            if (experienceHud == null) experienceHud = FindFirstObjectByType<RcpExperienceHUD>();
        }

        private void ApplyRigState(bool xrActive, bool forcePlacement)
        {
            if (_forcedXrForTests.HasValue) xrActive = _forcedXrForTests.Value;

            _lastXrState = xrActive;

            if (xrRigRoot != null)
            {
                xrRigRoot.SetActive(xrActive);
                if (xrActive && xrRigRoot.GetComponent<RcpHandVisualizer>() == null)
                {
                    xrRigRoot.AddComponent<RcpHandVisualizer>();
                }
            }

            if (flatRigRoot != null)
            {
                flatRigRoot.SetActive(!xrActive);
            }

            passthroughSetup?.RefreshCameras(xrActive);

            if (forcePlacement && !_initialPlacementCompleted)
            {
                _pendingInitialPlacement = true;
                _xrFramesAfterActivation = 0;
            }
        }

        public void PlaceExperienceInFrontOfUser()
        {
            ResolveDependencies();
            placementController?.PlaceInFrontOfUser();
        }
    }
}
