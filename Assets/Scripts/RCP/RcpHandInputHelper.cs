using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace NasaVR.RCP
{
    /// <summary>
    /// Utilidad compartida para detectar gatillo de mando o pellizco (pinch) y poses de manos
    /// vía OpenXR XRHandSubsystem, permitiendo operar 100% sin mandos en Quest.
    /// </summary>
    public static class RcpHandInputHelper
    {
        private static readonly List<XRHandSubsystem> Subsystems = new();
        private static readonly List<UnityEngine.XR.InputDevice> ControllerDevices = new();

        private static bool _wasLeftPinching;
        private static bool _wasRightPinching;
        private static bool _wasAnyTriggerDown;

        private const float PinchEnterThreshold = 0.025f;
        private const float PinchExitThreshold = 0.036f;

        public static XRHandSubsystem GetRunningHandSubsystem()
        {
            Subsystems.Clear();
            SubsystemManager.GetSubsystems(Subsystems);
            for (int i = 0; i < Subsystems.Count; i++)
            {
                if (Subsystems[i] != null && Subsystems[i].running)
                {
                    return Subsystems[i];
                }
            }
            return null;
        }

        public static bool IsHandPinching(XRHand hand)
        {
            if (!hand.isTracked) return false;
            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            if (!thumb.TryGetPose(out Pose thumbPose) || !index.TryGetPose(out Pose indexPose))
                return false;

            return Vector3.Distance(thumbPose.position, indexPose.position) <= PinchEnterThreshold;
        }

        /// <summary>
        /// Devuelve true únicamente en el flanco de subida del gatillo de controlador o pellizco con manos.
        /// </summary>
        public static bool WasSelectPressedThisFrame()
        {
            bool triggerNow = false;
            ControllerDevices.Clear();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, ControllerDevices);
            for (int i = 0; i < ControllerDevices.Count; i++)
            {
                if (ControllerDevices[i].TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool pressed) && pressed)
                {
                    triggerNow = true;
                    break;
                }
            }

            bool triggerPressedEdge = triggerNow && !_wasAnyTriggerDown;
            _wasAnyTriggerDown = triggerNow;

            bool pinchEdge = false;
            var handSys = GetRunningHandSubsystem();
            if (handSys != null)
            {
                pinchEdge |= UpdatePinchEdge(handSys.leftHand, ref _wasLeftPinching);
                pinchEdge |= UpdatePinchEdge(handSys.rightHand, ref _wasRightPinching);
            }
            else
            {
                _wasLeftPinching = false;
                _wasRightPinching = false;
            }

            return triggerPressedEdge || pinchEdge;
        }

        private static bool UpdatePinchEdge(XRHand hand, ref bool wasPinching)
        {
            if (!hand.isTracked)
            {
                wasPinching = false;
                return false;
            }

            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            if (!thumb.TryGetPose(out Pose thumbPose) || !index.TryGetPose(out Pose indexPose))
            {
                wasPinching = false;
                return false;
            }

            float dist = Vector3.Distance(thumbPose.position, indexPose.position);
            if (!wasPinching && dist <= PinchEnterThreshold)
            {
                wasPinching = true;
                return true;
            }

            if (wasPinching && dist >= PinchExitThreshold)
            {
                wasPinching = false;
            }
            return false;
        }
    }
}
