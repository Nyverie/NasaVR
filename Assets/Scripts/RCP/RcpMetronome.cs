using System;
using UnityEngine;
using XRLabs.Core;

namespace NasaVR.RCP
{
    /// <summary>
    /// Metrónomo procedural de RCP (110 LPM por defecto, rango AHA 100-120 LPM).
    /// Se activa automáticamente durante los pasos de compresión (p6, p7, p8)
    /// y expone BeatPulse para animar indicadores visuales sincronizados.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class RcpMetronome : MonoBehaviour
    {
        [Header("Ritmo")]
        [SerializeField, Range(90f, 130f)] private float beatsPerMinute = 110f;
        [SerializeField] private string[] activeStepIds = { "p6", "p7", "p8" };
        [SerializeField, Range(0f, 1f)] private float volume = 0.75f;

        private AudioSource _audioSource;
        private AudioClip _tickClip;
        private AudioClip _accentClip;
        private StepSystem _steps;
        private bool _isActive;
        private float _nextBeatTime;
        private float _beatPulse;
        private int _beatCount;

        public bool IsActive => _isActive;
        public float BeatsPerMinute => beatsPerMinute;
        public float BeatPulse => _beatPulse;
        public int BeatCount => _beatCount;

        public event Action<int> OnBeat;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.volume = volume;

            _tickClip = BuildClickClip(880f, 0.045f);
            _accentClip = BuildClickClip(1174.66f, 0.055f);
        }

        private void Start()
        {
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
                _steps.StepsFinished -= HandleStepsFinished;
            }
        }

        public void Bind(StepSystem steps)
        {
            if (_steps != null)
            {
                _steps.StepStarted -= HandleStepStarted;
                _steps.StepsFinished -= HandleStepsFinished;
            }

            _steps = steps;
            if (_steps == null) return;

            _steps.StepStarted += HandleStepStarted;
            _steps.StepsFinished += HandleStepsFinished;

            if (_steps.CurrentStep != null)
            {
                EvaluateStep(_steps.CurrentStep.stepId);
            }
        }

        private void Update()
        {
            _beatPulse = Mathf.MoveTowards(_beatPulse, 0f, Time.deltaTime * 4.5f);

            if (!_isActive) return;

            if (Time.time >= _nextBeatTime)
            {
                TriggerBeat();
                float interval = 60f / Mathf.Max(60f, beatsPerMinute);
                _nextBeatTime += interval;
                if (_nextBeatTime < Time.time)
                {
                    _nextBeatTime = Time.time + interval;
                }
            }
        }

        private void HandleStepStarted(StepDefinition step, int index, int total)
        {
            EvaluateStep(step != null ? step.stepId : string.Empty);
        }

        private void HandleStepsFinished()
        {
            SetActive(false);
        }

        private void EvaluateStep(string stepId)
        {
            bool shouldRun = false;
            if (!string.IsNullOrEmpty(stepId) && activeStepIds != null)
            {
                foreach (var id in activeStepIds)
                {
                    if (string.Equals(id, stepId, StringComparison.OrdinalIgnoreCase))
                    {
                        shouldRun = true;
                        break;
                    }
                }
            }

            SetActive(shouldRun);
        }

        public void SetActive(bool active)
        {
            if (_isActive == active) return;
            _isActive = active;

            if (active)
            {
                _beatCount = 0;
                _nextBeatTime = Time.time + 0.35f;
            }
        }

        private void TriggerBeat()
        {
            _beatCount++;
            _beatPulse = 1f;

            // Acento cada 30 compresiones (ciclo clásico de RCP) o en el primer pulso.
            bool isCycleStart = ((_beatCount - 1) % 30) == 0;
            AudioClip clip = isCycleStart ? _accentClip : _tickClip;
            if (_audioSource != null && clip != null)
            {
                _audioSource.PlayOneShot(clip, volume);
            }

            OnBeat?.Invoke(_beatCount);
        }

        private static AudioClip BuildClickClip(float frequency, float durationSeconds)
        {
            const int sampleRate = 22050;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * durationSeconds));
            var data = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float envelope = Mathf.Exp(-t * 55f);
                data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.65f;
            }

            var clip = AudioClip.Create($"RcpBeat_{frequency:0}", sampleCount, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
