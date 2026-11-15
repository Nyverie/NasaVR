using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;
using XRLabs.Core;

namespace NasaVR.RCP
{
    public enum CompressionQualityState
    {
        Inactive,
        InPositionGoodRate,
        TooSlow,
        TooFast,
        HandsIdleOnChest,
        HandsAwayFromChest
    }

    /// <summary>
    /// Valida durante las fases de compresión (p6-p8) que las manos o mandos del usuario
    /// estén cerca del área del pecho (Objeto_pecho) y realizando compresiones verticales al ritmo (100-120 LPM).
    /// Emite estado para el HUD y alertas visuales/sonoras si el usuario se detiene o se aleja.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class RcpCompressionMonitor : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private Transform chestTarget;
        [SerializeField] private Transform rigRoot;
        [SerializeField] private RcpMetronome metronome;

        [Header("Umbrales de validación")]
        [SerializeField] private float chestZoneRadius = 0.28f;
        [SerializeField] private float minStrokeAmplitudeMeters = 0.018f;
        [SerializeField] private float minStrokeIntervalSeconds = 0.28f;
        [SerializeField] private float maxIdleSeconds = 2.4f;
        [SerializeField] private float maxAwaySeconds = 1.8f;
        [SerializeField] private float alertCooldownSeconds = 3.5f;
        [SerializeField] private float targetMinBpm = 95f;
        [SerializeField] private float targetMaxBpm = 125f;

        public CompressionQualityState CurrentState { get; private set; } = CompressionQualityState.Inactive;
        public float MeasuredBpm { get; private set; }
        public float ClosestDistanceMeters { get; private set; } = float.PositiveInfinity;
        public bool HasHandsInZone { get; private set; }

        private readonly List<XRHandSubsystem> _handSubsystems = new();
        private readonly List<float> _strokeTimestamps = new();
        private XRHandSubsystem _handSubsystem;
        private Transform _leftController;
        private Transform _rightController;
        private AudioSource _alertSource;
        private AudioClip _alertClip;

        private bool _trackingDownstroke;
        private float _peakY;
        private float _troughY;
        private float _lastStrokeAt = -10f;
        private float _outOfZoneSince = -1f;
        private float _inZoneEnteredAt = -1f;
        private float _lastAlertAt = -10f;
        private float _activeSince = -1f;
        private float? _forcedDistanceForTests;

        private void Awake()
        {
            _alertSource = GetComponent<AudioSource>();
            _alertSource.playOnAwake = false;
            _alertSource.spatialBlend = 0f;
            _alertSource.volume = 0.38f;
            _alertClip = CreateAlertChime();
        }

        private void Start()
        {
            if (metronome == null) metronome = FindFirstObjectByType<RcpMetronome>();
            ResolveControllerTransforms();
        }

        private void Update()
        {
            bool active = metronome != null && metronome.IsActive && chestTarget != null;
            if (!active)
            {
                if (CurrentState != CompressionQualityState.Inactive)
                {
                    ResetTracking();
                }
                return;
            }

            if (_activeSince < 0f)
            {
                _activeSince = Time.time;
                _inZoneEnteredAt = Time.time;
            }

            // En Desktop permite simular compresiones con la tecla C para pruebas rápidas.
            if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
            {
                RegisterCompressionStroke(Time.time);
            }

            bool foundInput = TryGetClosestHandOrController(out Vector3 closestPos, out float dist);
            ClosestDistanceMeters = _forcedDistanceForTests ?? dist;
            HasHandsInZone = (foundInput || _forcedDistanceForTests.HasValue) && ClosestDistanceMeters <= chestZoneRadius;

            if (!HasHandsInZone)
            {
                _inZoneEnteredAt = -1f;
                if (_outOfZoneSince < 0f) _outOfZoneSince = Time.time;

                if (Time.time - _activeSince > 1.2f && Time.time - _outOfZoneSince >= maxAwaySeconds)
                {
                    CurrentState = CompressionQualityState.HandsAwayFromChest;
                    TryPlayAlertBeep();
                }
                else
                {
                    CurrentState = CompressionQualityState.HandsAwayFromChest;
                }
                PruneOldStrokes();
                return;
            }

            _outOfZoneSince = -1f;
            if (_inZoneEnteredAt < 0f) _inZoneEnteredAt = Time.time;

            if (foundInput)
            {
                SampleVerticalStroke(closestPos.y, Time.time);
            }

            PruneOldStrokes();
            UpdateQualityClassification();
        }

        public void Configure(Transform chest, Transform rig, RcpMetronome met)
        {
            chestTarget = chest;
            rigRoot = rig;
            metronome = met;
            ResolveControllerTransforms();
        }

        /// <summary>Registra un golpe de compresión (usado también en tests PlayMode).</summary>
        public void SimulateCompressionPulseForTests(float? overrideDistanceMeters = 0.05f)
        {
            _forcedDistanceForTests = overrideDistanceMeters;
            HasHandsInZone = overrideDistanceMeters.HasValue && overrideDistanceMeters.Value <= chestZoneRadius;
            RegisterCompressionStroke(Time.time);
            UpdateQualityClassification();
        }

        public void ForceDistanceForTests(float? distanceMeters)
        {
            _forcedDistanceForTests = distanceMeters;
        }

        private void ResetTracking()
        {
            CurrentState = CompressionQualityState.Inactive;
            MeasuredBpm = 0f;
            ClosestDistanceMeters = float.PositiveInfinity;
            HasHandsInZone = false;
            _strokeTimestamps.Clear();
            _trackingDownstroke = false;
            _outOfZoneSince = -1f;
            _inZoneEnteredAt = -1f;
            _activeSince = -1f;
        }

        private void SampleVerticalStroke(float y, float now)
        {
            if (!_trackingDownstroke)
            {
                if (_peakY == 0f || y > _peakY) _peakY = y;
                if (_peakY - y >= minStrokeAmplitudeMeters)
                {
                    _trackingDownstroke = true;
                    _troughY = y;
                }
            }
            else
            {
                if (y < _troughY) _troughY = y;
                if (y - _troughY >= minStrokeAmplitudeMeters * 0.65f)
                {
                    if (now - _lastStrokeAt >= minStrokeIntervalSeconds)
                    {
                        RegisterCompressionStroke(now);
                    }
                    _trackingDownstroke = false;
                    _peakY = y;
                }
            }
        }

        private void RegisterCompressionStroke(float now)
        {
            _lastStrokeAt = now;
            _strokeTimestamps.Add(now);
            PruneOldStrokes();
        }

        private void PruneOldStrokes()
        {
            float cutoff = Time.time - 3.0f;
            while (_strokeTimestamps.Count > 0 && _strokeTimestamps[0] < cutoff)
            {
                _strokeTimestamps.RemoveAt(0);
            }

            if (_strokeTimestamps.Count >= 2)
            {
                float span = _strokeTimestamps[^1] - _strokeTimestamps[0];
                MeasuredBpm = span > 0.05f ? ((_strokeTimestamps.Count - 1) / span) * 60f : 0f;
            }
            else
            {
                MeasuredBpm = 0f;
            }
        }

        private void UpdateQualityClassification()
        {
            float timeSinceLastStroke = Time.time - _lastStrokeAt;
            float timeInZone = _inZoneEnteredAt >= 0f ? Time.time - _inZoneEnteredAt : 0f;

            if (timeSinceLastStroke > maxIdleSeconds)
            {
                CurrentState = CompressionQualityState.HandsIdleOnChest;
                if (timeInZone > maxIdleSeconds && Time.time - _activeSince > 2.0f)
                {
                    TryPlayAlertBeep();
                }
                return;
            }

            if (_strokeTimestamps.Count < 2)
            {
                CurrentState = CompressionQualityState.InPositionGoodRate;
                return;
            }

            if (MeasuredBpm < targetMinBpm)
            {
                CurrentState = CompressionQualityState.TooSlow;
                TryPlayAlertBeep();
            }
            else if (MeasuredBpm > targetMaxBpm)
            {
                CurrentState = CompressionQualityState.TooFast;
                TryPlayAlertBeep();
            }
            else
            {
                CurrentState = CompressionQualityState.InPositionGoodRate;
            }
        }

        private bool TryGetClosestHandOrController(out Vector3 closestPos, out float closestDist)
        {
            closestPos = Vector3.zero;
            closestDist = float.PositiveInfinity;
            bool any = false;
            Vector3 chestPos = chestTarget.position;

            RefreshHandSubsystem();
            if (_handSubsystem != null && _handSubsystem.running && rigRoot != null)
            {
                any |= TryEvaluateHand(_handSubsystem.leftHand, chestPos, ref closestPos, ref closestDist);
                any |= TryEvaluateHand(_handSubsystem.rightHand, chestPos, ref closestPos, ref closestDist);
            }

            if (_leftController != null && _leftController.gameObject.activeInHierarchy)
            {
                float d = Vector3.Distance(_leftController.position, chestPos);
                if (d < closestDist) { closestDist = d; closestPos = _leftController.position; }
                any = true;
            }

            if (_rightController != null && _rightController.gameObject.activeInHierarchy)
            {
                float d = Vector3.Distance(_rightController.position, chestPos);
                if (d < closestDist) { closestDist = d; closestPos = _rightController.position; }
                any = true;
            }

            return any;
        }

        private bool TryEvaluateHand(XRHand hand, Vector3 chestPos, ref Vector3 bestPos, ref float bestDist)
        {
            if (!hand.isTracked) return false;
            var palm = hand.GetJoint(XRHandJointID.Palm);
            var tip = hand.GetJoint(XRHandJointID.IndexTip);
            Pose pose;
            if (!palm.TryGetPose(out pose) && !tip.TryGetPose(out pose)) return false;

            Vector3 world = rigRoot.TransformPoint(pose.position);
            float d = Vector3.Distance(world, chestPos);
            if (d < bestDist)
            {
                bestDist = d;
                bestPos = world;
            }
            return true;
        }

        private void RefreshHandSubsystem()
        {
            if (_handSubsystem != null && _handSubsystem.running) return;
            _handSubsystems.Clear();
            SubsystemManager.GetSubsystems(_handSubsystems);
            for (int i = 0; i < _handSubsystems.Count; i++)
            {
                if (_handSubsystems[i] != null && _handSubsystems[i].running)
                {
                    _handSubsystem = _handSubsystems[i];
                    return;
                }
            }
        }

        private void ResolveControllerTransforms()
        {
            if (rigRoot == null) return;
            _leftController = FindDeepChild(rigRoot, "Left Controller");
            _rightController = FindDeepChild(rigRoot, "Right Controller");
        }

        private void TryPlayAlertBeep()
        {
            if (_alertSource == null || _alertClip == null) return;
            if (Time.time - _lastAlertAt < alertCooldownSeconds) return;
            _lastAlertAt = Time.time;
            _alertSource.PlayOneShot(_alertClip);
        }

        private static Transform FindDeepChild(Transform parent, string name)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name == name) return c;
                var nested = FindDeepChild(c, name);
                if (nested != null) return nested;
            }
            return null;
        }

        private static AudioClip CreateAlertChime()
        {
            const int sampleRate = 44100;
            const float duration = 0.14f;
            int samples = Mathf.RoundToInt(sampleRate * duration);
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float env = Mathf.Sin((t / duration) * Mathf.PI);
                float freq = i < samples / 2 ? 520f : 390f;
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.45f;
            }
            var clip = AudioClip.Create("RcpCompressionAlert", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
