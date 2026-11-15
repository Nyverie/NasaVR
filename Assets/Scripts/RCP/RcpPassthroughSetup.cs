using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace NasaVR.RCP
{
    /// <summary>
    /// Configura el Passthrough de Meta Quest y las cámaras de la escena para AR transparente.
    /// En el visor XR deja el fondo de cámara transparente (alpha 0) con post-proceso apagado
    /// y activa OVRManager + OVRPassthroughLayer en modo Underlay. En modo plano (Editor) usa
    /// un fondo oscuro neutro para facilitar pruebas sin visor.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class RcpPassthroughSetup : MonoBehaviour
    {
        [SerializeField] private bool enablePassthroughOnDevice = true;
        [SerializeField] private Color editorFallbackBackground = new(0.08f, 0.10f, 0.14f, 1f);

        private void Awake()
        {
            ConfigureCameras();
            ConfigureMetaPassthrough();
        }

        private void Start()
        {
            // Reaplica tras PlatformBootstrapper por si activó el rig XR en Awake.
            ConfigureCameras();
        }

        private void ConfigureCameras()
        {
            bool xrActive = XRSettings.isDeviceActive;
            bool useTransparent = enablePassthroughOnDevice && (xrActive || !Application.isEditor);

            foreach (var cam in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (cam == null) continue;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = useTransparent
                    ? new Color(0f, 0f, 0f, 0f)
                    : editorFallbackBackground;

                var urpData = cam.GetComponent<UniversalAdditionalCameraData>();
                if (urpData != null)
                {
                    urpData.renderPostProcessing = false;
                }
            }
        }

        private void ConfigureMetaPassthrough()
        {
#if META_XR_SDK
            if (!enablePassthroughOnDevice) return;

            var ovrManager = GetComponent<OVRManager>() ?? FindFirstObjectByType<OVRManager>();
            if (ovrManager == null)
            {
                ovrManager = gameObject.AddComponent<OVRManager>();
            }

            ovrManager.isInsightPassthroughEnabled = true;
            ovrManager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;

            var passthroughLayer = GetComponent<OVRPassthroughLayer>() ?? FindFirstObjectByType<OVRPassthroughLayer>();
            if (passthroughLayer == null)
            {
                passthroughLayer = gameObject.AddComponent<OVRPassthroughLayer>();
            }

#pragma warning disable CS0618
            passthroughLayer.overlayType = OVROverlay.OverlayType.Underlay;
#pragma warning restore CS0618
            passthroughLayer.textureOpacity = 1f;
            passthroughLayer.hidden = false;
#endif
        }
    }
}
