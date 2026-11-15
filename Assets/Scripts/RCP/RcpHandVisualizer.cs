using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace NasaVR.RCP
{
    /// <summary>
    /// Visualiza ambas manos en Quest/OpenXR (26 articulaciones vía XRHandSubsystem cuando hay Hand Tracking,
    /// o marcadores de controlador cuando se usan mandos Touch) y mantiene sincronizados los HandTriggers
    /// para tocar hotspots físicos en Passthrough.
    /// </summary>
    public class RcpHandVisualizer : MonoBehaviour
    {
        private sealed class HandRigVisual
        {
            public readonly GameObject Root;
            public readonly Dictionary<XRHandJointID, Transform> Joints = new();
            public readonly Dictionary<XRHandJointID, Renderer> Renderers = new();
            public GameObject ControllerMarker;

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

        private readonly List<XRHandSubsystem> _subsystems = new();
        private XRHandSubsystem _handSubsystem;
        private HandRigVisual _leftVisual;
        private HandRigVisual _rightVisual;
        private Transform _leftController;
        private Transform _rightController;
        private Transform _leftHandTrigger;
        private Transform _rightHandTrigger;
        private Transform _xrOriginTransform;
        private Material _jointMat;
        private Material _tipMat;

        private void Awake()
        {
            _xrOriginTransform = transform;
            _leftController = FindDeepChild(transform, "Left Controller");
            _rightController = FindDeepChild(transform, "Right Controller");
            _leftHandTrigger = FindDeepChild(FindDeepChild(transform, "Grupo mano izquierda"), "HandTrigger");
            _rightHandTrigger = FindDeepChild(FindDeepChild(transform, "Grupo mano derecha"), "HandTrigger");

            _jointMat = CreateVisualMaterial(new Color(0.15f, 0.85f, 0.95f, 0.78f), 0.35f);
            _tipMat = CreateVisualMaterial(new Color(0.22f, 1.0f, 0.68f, 0.92f), 0.70f);

            _leftVisual = CreateHandVisual("VisualHand_Left", _leftController);
            _rightVisual = CreateHandVisual("VisualHand_Right", _rightController);
        }

        private void OnDestroy()
        {
            if (_jointMat != null) Destroy(_jointMat);
            if (_tipMat != null) Destroy(_tipMat);
        }

        private void Update()
        {
            RefreshSubsystem();

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
        }

        private void RefreshSubsystem()
        {
            if (_handSubsystem != null && _handSubsystem.running) return;
            _subsystems.Clear();
            SubsystemManager.GetSubsystems(_subsystems);
            for (int i = 0; i < _subsystems.Count; i++)
            {
                if (_subsystems[i] != null && _subsystems[i].running)
                {
                    _handSubsystem = _subsystems[i];
                    return;
                }
            }
        }

        private bool UpdateTrackedHand(XRHand hand, HandRigVisual visual, Transform handTrigger)
        {
            if (!hand.isTracked) return false;

            for (int i = 0; i < VisibleJointIds.Length; i++)
            {
                XRHandJointID id = VisibleJointIds[i];
                XRHandJoint joint = hand.GetJoint(id);
                if (!visual.Joints.TryGetValue(id, out var jointTf)) continue;

                if (joint.TryGetPose(out Pose localPose))
                {
                    // Las poses de XRHand vienen en el espacio local del XROrigin.
                    jointTf.position = _xrOriginTransform.TransformPoint(localPose.position);
                    jointTf.rotation = _xrOriginTransform.rotation * localPose.rotation;
                    if (visual.Renderers.TryGetValue(id, out var r)) r.enabled = true;
                }
                else
                {
                    if (visual.Renderers.TryGetValue(id, out var r)) r.enabled = false;
                }
            }

            // Sincroniza HandTrigger con la yema del índice (o palma) para activar TouchableHotspot al tocar con las manos.
            if (handTrigger != null)
            {
                var tip = hand.GetJoint(XRHandJointID.IndexTip);
                var palm = hand.GetJoint(XRHandJointID.Palm);
                if (tip.TryGetPose(out Pose tipPose))
                {
                    handTrigger.position = _xrOriginTransform.TransformPoint(tipPose.position);
                }
                else if (palm.TryGetPose(out Pose palmPose))
                {
                    handTrigger.position = _xrOriginTransform.TransformPoint(palmPose.position);
                }
            }

            return true;
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
