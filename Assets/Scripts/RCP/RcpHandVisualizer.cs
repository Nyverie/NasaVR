using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using XRLabs.Interactions;

namespace NasaVR.RCP
{
    /// <summary>
    /// Visualiza ambas manos en Quest/OpenXR (26 articulaciones vía XRHandSubsystem cuando hay Hand Tracking,
    /// o marcadores de controlador cuando se usan mandos Touch), sincroniza los HandTriggers
    /// y habilita toque físico directo (poke) y pellizco (pinch) para operar 100% sin mandos.
    /// </summary>
    public class RcpHandVisualizer : MonoBehaviour
    {
        private sealed class HandRigVisual
        {
            public readonly GameObject Root;
            public readonly Dictionary<XRHandJointID, Transform> Joints = new();
            public readonly Dictionary<XRHandJointID, Renderer> Renderers = new();
            public GameObject ControllerMarker;
            public bool WasPinching;
            public float NextInteractTime;
            public TouchableHotspot DwellingHotspot;
            public float DwellStartTime;

            public HandRigVisual(GameObject root)
            {
                Root = root;
            }
        }

        private static readonly XRHandJointID[] VisibleJointIds =
        {
            XRHandJointID.Wrist,
            XRHandJointID.Palm,
            XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
            XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
            XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
            XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
            XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip
        };

        private sealed class HandLaserState
        {
            public NearFarInteractor Interactor;
            public LineRenderer LineRenderer;
            public CurveVisualController CurveVisual;
            public bool IsLeft;
            public bool IsMuted;
        }

        private const float PhysicalPokeHotspotRadius = 0.09f;
        private const float PhysicalPokeDwellSeconds = 0.16f;
        private const float PhysicalPokeCooldownSeconds = 0.65f;
        private const float PhysicalUiTouchDepth = 0.035f;
        private const float ChestLaserMuteRadius = 0.36f;
        private const float ChestLaserUnmuteRadius = 0.44f;

        private XRHandSubsystem _handSubsystem;
        private HandRigVisual _leftVisual;
        private HandRigVisual _rightVisual;
        private Transform _leftController;
        private Transform _rightController;
        private Transform _leftHandTrigger;
        private Transform _rightHandTrigger;
        private Transform _xrOriginTransform;
        private Transform _chestTarget;
        private RcpPatientLocator _patientLocator;
        private RcpMetronome _metronome;
        private Material _jointMat;
        private Material _tipMat;
        private Material _pinchTipMat;

        private readonly List<HandLaserState> _handLasers = new();
        private readonly List<TouchableHotspot> _hotspots = new();
        private readonly List<Button> _hudButtons = new();
        private float _lastCacheRefresh = -10f;

        private void Awake()
        {
            _xrOriginTransform = transform;
            _leftController = FindDeepChild(transform, "Left Controller");
            _rightController = FindDeepChild(transform, "Right Controller");
            _leftHandTrigger = FindDeepChild(FindDeepChild(transform, "Grupo mano izquierda"), "HandTrigger");
            _rightHandTrigger = FindDeepChild(FindDeepChild(transform, "Grupo mano derecha"), "HandTrigger");

            _jointMat = CreateVisualMaterial(new Color(0.15f, 0.85f, 0.95f, 0.78f), 0.35f);
            _tipMat = CreateVisualMaterial(new Color(0.22f, 1.0f, 0.68f, 0.92f), 0.70f);
            _pinchTipMat = CreateVisualMaterial(new Color(0.45f, 1.0f, 0.35f, 1.0f), 1.45f);

            _leftVisual = CreateHandVisual("VisualHand_Left", _leftController);
            _rightVisual = CreateHandVisual("VisualHand_Right", _rightController);
            CacheHandLasers();
        }

        private void OnDestroy()
        {
            if (_jointMat != null) Destroy(_jointMat);
            if (_tipMat != null) Destroy(_tipMat);
            if (_pinchTipMat != null) Destroy(_pinchTipMat);
        }

        private void Update()
        {
            if (_handSubsystem == null || !_handSubsystem.running)
            {
                _handSubsystem = RcpHandInputHelper.GetRunningHandSubsystem();
            }

            if (Time.time - _lastCacheRefresh > 2.5f)
            {
                RefreshInteractablesCache();
            }

            bool leftHandTracked = false;
            bool rightHandTracked = false;

            if (_handSubsystem != null && _handSubsystem.running)
            {
                leftHandTracked = UpdateTrackedHand(_handSubsystem.leftHand, _leftVisual, _leftHandTrigger);
                rightHandTracked = UpdateTrackedHand(_handSubsystem.rightHand, _rightVisual, _rightHandTrigger);
            }

            if (_leftVisual.Root != null) _leftVisual.Root.SetActive(leftHandTracked);
            if (_rightVisual.Root != null) _rightVisual.Root.SetActive(rightHandTracked);

            if (_leftVisual.ControllerMarker != null)
            {
                _leftVisual.ControllerMarker.SetActive(!leftHandTracked && _leftController != null);
            }
            if (_rightVisual.ControllerMarker != null)
            {
                _rightVisual.ControllerMarker.SetActive(!rightHandTracked && _rightController != null);
            }

            UpdateProximityLaserMute(leftHandTracked, rightHandTracked);
        }

        private bool UpdateTrackedHand(XRHand hand, HandRigVisual visual, Transform handTrigger)
        {
            if (!hand.isTracked)
            {
                visual.WasPinching = false;
                visual.DwellingHotspot = null;
                return false;
            }

            for (int i = 0; i < VisibleJointIds.Length; i++)
            {
                XRHandJointID id = VisibleJointIds[i];
                XRHandJoint joint = hand.GetJoint(id);
                if (!visual.Joints.TryGetValue(id, out var jointTf)) continue;

                if (joint.TryGetPose(out Pose localPose))
                {
                    jointTf.position = _xrOriginTransform.TransformPoint(localPose.position);
                    jointTf.rotation = _xrOriginTransform.rotation * localPose.rotation;
                    if (visual.Renderers.TryGetValue(id, out var r)) r.enabled = true;
                }
                else
                {
                    if (visual.Renderers.TryGetValue(id, out var r)) r.enabled = false;
                }
            }

            bool isPinching = RcpHandInputHelper.IsHandPinching(hand);
            bool pinchEdge = isPinching && !visual.WasPinching;
            visual.WasPinching = isPinching;

            if (visual.Renderers.TryGetValue(XRHandJointID.IndexTip, out var indexRend) && indexRend != null)
                indexRend.sharedMaterial = isPinching ? _pinchTipMat : _tipMat;
            if (visual.Renderers.TryGetValue(XRHandJointID.ThumbTip, out var thumbRend) && thumbRend != null)
                thumbRend.sharedMaterial = isPinching ? _pinchTipMat : _tipMat;

            Vector3 tipWorld = Vector3.zero;
            bool hasTipWorld = false;
            Vector3 wristWorld = Vector3.zero;
            bool hasWristWorld = false;

            if (hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tipPose))
            {
                tipWorld = _xrOriginTransform.TransformPoint(tipPose.position);
                hasTipWorld = true;
            }
            else if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose))
            {
                tipWorld = _xrOriginTransform.TransformPoint(palmPose.position);
                hasTipWorld = true;
            }

            if (hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose))
            {
                wristWorld = _xrOriginTransform.TransformPoint(wristPose.position);
                hasWristWorld = true;
            }

            if (handTrigger != null && hasTipWorld)
            {
                handTrigger.position = tipWorld;
            }

            if (hasTipWorld)
            {
                ProcessBareHandInteractions(visual, tipWorld, wristWorld, hasWristWorld, pinchEdge);
            }

            return true;
        }

        private void ProcessBareHandInteractions(HandRigVisual visual, Vector3 tipWorld, Vector3 wristWorld, bool hasWrist, bool pinchEdge)
        {
            if (Time.time < visual.NextInteractTime) return;

            // 1. Toque físico directo a botones del HUD sin necesidad de rayo ni mando.
            for (int i = 0; i < _hudButtons.Count; i++)
            {
                var btn = _hudButtons[i];
                if (btn == null || !btn.gameObject.activeInHierarchy || !btn.interactable) continue;
                var rt = btn.GetComponent<RectTransform>();
                if (rt == null) continue;

                Vector3 localTip = rt.InverseTransformPoint(tipWorld);
                float worldPerLocalZ = Mathf.Max(0.0001f, rt.lossyScale.z);
                float depthMeters = Mathf.Abs(localTip.z) * worldPerLocalZ;
                if (depthMeters <= PhysicalUiTouchDepth && rt.rect.Contains(new Vector2(localTip.x, localTip.y)))
                {
                    btn.onClick.Invoke();
                    visual.NextInteractTime = Time.time + PhysicalPokeCooldownSeconds;
                    return;
                }
            }

            // Durante Paso 0 (ubicar paciente) aún no se activan hotspots clínicos.
            if (_patientLocator != null && _patientLocator.IsPlacingPatient) return;

            // 2. Toque físico directo con el dedo sobre hotspots 3D (requiere contacto breve para no disparar por accidente).
            TouchableHotspot touchingHotspot = null;
            for (int i = 0; i < _hotspots.Count; i++)
            {
                var hs = _hotspots[i];
                if (hs == null || !hs.gameObject.activeInHierarchy) continue;
                if (Vector3.Distance(tipWorld, hs.transform.position) <= PhysicalPokeHotspotRadius)
                {
                    touchingHotspot = hs;
                    break;
                }
            }

            if (touchingHotspot != null)
            {
                if (visual.DwellingHotspot != touchingHotspot)
                {
                    visual.DwellingHotspot = touchingHotspot;
                    visual.DwellStartTime = Time.time;
                }
                else if (Time.time - visual.DwellStartTime >= PhysicalPokeDwellSeconds)
                {
                    touchingHotspot.Touch();
                    visual.DwellingHotspot = null;
                    visual.NextInteractTime = Time.time + PhysicalPokeCooldownSeconds;
                    return;
                }
            }
            else
            {
                visual.DwellingHotspot = null;
            }

            // 3. Respaldo de pellizco (pinch) apuntando desde la mano cuando no hay mandos.
            if (pinchEdge && hasWrist)
            {
                Vector3 aimDir = tipWorld - wristWorld;
                if (aimDir.sqrMagnitude > 0.0004f &&
                    Physics.Raycast(tipWorld, aimDir.normalized, out RaycastHit hit, 3.5f))
                {
                    var hotspot = hit.collider.GetComponentInParent<TouchableHotspot>();
                    if (hotspot != null)
                    {
                        hotspot.Touch();
                        visual.NextInteractTime = Time.time + PhysicalPokeCooldownSeconds;
                    }
                }
            }
        }

        private void RefreshInteractablesCache()
        {
            _lastCacheRefresh = Time.time;
            if (_patientLocator == null) _patientLocator = FindFirstObjectByType<RcpPatientLocator>();
            if (_metronome == null) _metronome = FindFirstObjectByType<RcpMetronome>();

            _hotspots.Clear();
            _hotspots.AddRange(FindObjectsByType<TouchableHotspot>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            for (int i = 0; i < _hotspots.Count; i++)
            {
                if (_hotspots[i] != null && _hotspots[i].name == "Objeto_pecho")
                {
                    _chestTarget = _hotspots[i].transform;
                    break;
                }
            }

            _hudButtons.Clear();
            var hud = FindFirstObjectByType<RcpExperienceHUD>();
            if (hud != null)
            {
                _hudButtons.AddRange(hud.GetComponentsInChildren<Button>(true));
            }
        }

        private void CacheHandLasers()
        {
            _handLasers.Clear();
            foreach (var nf in GetComponentsInChildren<NearFarInteractor>(true))
            {
                if (nf == null) continue;
                bool isLeft = nf.name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                              (nf.transform.parent != null && nf.transform.parent.name.IndexOf("izquierda", System.StringComparison.OrdinalIgnoreCase) >= 0);

                _handLasers.Add(new HandLaserState
                {
                    Interactor = nf,
                    LineRenderer = nf.GetComponentInChildren<LineRenderer>(true),
                    CurveVisual = nf.GetComponentInChildren<CurveVisualController>(true),
                    IsLeft = isLeft
                });
            }
        }

        private void UpdateProximityLaserMute(bool leftHandTracked, bool rightHandTracked)
        {
            bool placing = _patientLocator != null && _patientLocator.IsPlacingPatient;
            for (int i = 0; i < _handLasers.Count; i++)
            {
                var state = _handLasers[i];
                if (state.Interactor == null) continue;

                bool shouldMute = false;
                if (!placing && _chestTarget != null)
                {
                    Vector3 handPos = GetHandPositionForSide(state.IsLeft, leftHandTracked, rightHandTracked, state.Interactor.transform.position);
                    float dist = Vector3.Distance(handPos, _chestTarget.position);
                    float threshold = state.IsMuted ? ChestLaserUnmuteRadius : ChestLaserMuteRadius;
                    if (_metronome != null && _metronome.IsActive) threshold += 0.05f;
                    shouldMute = dist <= threshold;
                }

                if (shouldMute == state.IsMuted) continue;
                state.IsMuted = shouldMute;
                state.Interactor.enableFarCasting = !shouldMute;
                if (state.CurveVisual != null) state.CurveVisual.enabled = !shouldMute;
                if (state.LineRenderer != null) state.LineRenderer.enabled = !shouldMute;
            }
        }

        private Vector3 GetHandPositionForSide(bool isLeft, bool leftTracked, bool rightTracked, Vector3 fallback)
        {
            HandRigVisual visual = isLeft ? _leftVisual : _rightVisual;
            bool tracked = isLeft ? leftTracked : rightTracked;
            if (tracked && visual != null && visual.Joints.TryGetValue(XRHandJointID.Palm, out var palm) && palm != null)
            {
                return palm.position;
            }
            Transform ctrl = isLeft ? _leftController : _rightController;
            return ctrl != null ? ctrl.position : fallback;
        }

        private HandRigVisual CreateHandVisual(string rootName, Transform controllerAnchor)
        {
            var visual = new HandRigVisual(new GameObject(rootName));
            visual.Root.transform.SetParent(transform, false);

            for (int i = 0; i < VisibleJointIds.Length; i++)
            {
                XRHandJointID id = VisibleJointIds[i];
                bool isTip = id == XRHandJointID.IndexTip || id == XRHandJointID.ThumbTip;
                bool isPalm = id == XRHandJointID.Palm || id == XRHandJointID.Wrist;

                var jointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                jointGo.name = id.ToString();
                jointGo.transform.SetParent(visual.Root.transform, false);
                var col = jointGo.GetComponent<Collider>();
                if (col != null) Destroy(col);

                float scale = isPalm ? 0.030f : (isTip ? 0.017f : 0.013f);
                jointGo.transform.localScale = Vector3.one * scale;

                var rend = jointGo.GetComponent<Renderer>();
                rend.sharedMaterial = isTip ? _tipMat : _jointMat;

                visual.Joints[id] = jointGo.transform;
                visual.Renderers[id] = rend;
            }

            visual.Root.SetActive(false);

            if (controllerAnchor != null)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = rootName + "_ControllerMarker";
                marker.transform.SetParent(controllerAnchor, false);
                marker.transform.localPosition = new Vector3(0f, 0f, 0.04f);
                marker.transform.localScale = new Vector3(0.032f, 0.032f, 0.06f);
                var col = marker.GetComponent<Collider>();
                if (col != null) Destroy(col);
                marker.GetComponent<Renderer>().sharedMaterial = _tipMat;
                visual.ControllerMarker = marker;
            }

            return visual;
        }

        private static Material CreateVisualMaterial(Color color, float emissionIntensity)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", color * emissionIntensity);
            }
            return mat;
        }

        private static Transform FindDeepChild(Transform parent, string childName)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName) return child;
                Transform nested = FindDeepChild(child, childName);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
