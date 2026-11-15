using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using XRLabs.Core;

namespace NasaVR.RCP
{
    /// <summary>
    /// Panel espacial de guía AR para el Asistente de RCP.
    /// Muestra paso actual, subtítulos en vivo, botones táctiles para el paso de decisión (p3)
    /// sin necesidad de teclado, indicador de ritmo/tiempo para compresiones (p6-p8), cambio
    /// de idioma (ES/EN), recolocación del paciente y reinicio.
    /// </summary>
    public class RcpExperienceHUD : MonoBehaviour
    {
        [Header("Referencias de UI")]
        [SerializeField] private TMP_Text headerText;
        [SerializeField] private TMP_Text stepTitleText;
        [SerializeField] private TMP_Text subtitleText;
        [SerializeField] private TMP_Text statusBadgeText;
        [SerializeField] private Image accentBar;
        [SerializeField] private Image beatRingImage;
        [SerializeField] private GameObject choiceGroup;
        [SerializeField] private Button choiceBreathingNormalButton;
        [SerializeField] private Button choiceNotBreathingButton;
        [SerializeField] private TMP_Text choiceNormalLabel;
        [SerializeField] private TMP_Text choiceNotBreathingLabel;
        [SerializeField] private GameObject compressionGroup;
        [SerializeField] private TMP_Text timerText;
        [SerializeField] private TMP_Text metronomeLabel;
        [SerializeField] private Button languageButton;
        [SerializeField] private TMP_Text languageButtonLabel;
        [SerializeField] private Button repositionButton;
        [SerializeField] private TMP_Text repositionButtonLabel;
        [SerializeField] private Button restartButton;
        [SerializeField] private TMP_Text restartButtonLabel;
        [SerializeField] private Button skipVoiceButton;
        [SerializeField] private TMP_Text skipVoiceLabel;

        [Header("Ubicación en AR")]
        [SerializeField] private float followDistance = 1.05f;
        [SerializeField] private float followHeightOffset = -0.04f;
        [SerializeField] private bool continuousHeadFollow = false;
        [SerializeField] private float followLerpSpeed = 3.5f;

        [Header("Dependencias")]
        [SerializeField] private RcpPlacementController placementController;
        [SerializeField] private RcpMetronome metronome;
        [SerializeField] private RcpPatientLocator patientLocator;
        [SerializeField] private RcpCompressionMonitor compressionMonitor;
        [SerializeField] private RcpCardiacMonitorUI cardiacMonitor;

        private StepSystem _steps;
        private StepDefinition _currentStep;
        private float _stepStartTime;
        private bool _isErrorSubtitle;
        private bool _isPlacingPatient;
        private float _errorClearAt;

        private static readonly Dictionary<string, float> TimedStepSeconds = new()
        {
            { "p6", 30f },
            { "p7", 25f },
            { "p8", 40f }
        };

        private static readonly Color NormalAccent = new(0.14f, 0.78f, 0.82f, 1f);
        private static readonly Color AlertAccent = new(1f, 0.52f, 0.16f, 1f);
        private static readonly Color BeatIdleColor = new(0.14f, 0.78f, 0.82f, 0.35f);
        private static readonly Color BeatPeakColor = new(0.25f, 1f, 0.65f, 1f);

        private void Awake()
        {
            WireButtons();
        }

        private void Start()
        {
            if (placementController == null) placementController = FindFirstObjectByType<RcpPlacementController>();
            if (metronome == null) metronome = FindFirstObjectByType<RcpMetronome>();
            if (patientLocator == null) patientLocator = FindFirstObjectByType<RcpPatientLocator>();
            if (compressionMonitor == null) compressionMonitor = FindFirstObjectByType<RcpCompressionMonitor>();
            if (cardiacMonitor == null) cardiacMonitor = GetComponentInChildren<RcpCardiacMonitorUI>(true);

            if (SimulationManager.Instance != null && SimulationManager.Instance.Steps != null)
            {
                Bind(SimulationManager.Instance.Steps);
            }

            if (VOManager.Instance != null)
            {
                VOManager.Instance.LineStarted += HandleLineStarted;
                VOManager.Instance.LineEnded += HandleLineEnded;
            }

            LanguageService.Changed += HandleLanguageChanged;
            RefreshLocalizedLabels();
            SnapInFrontOfCamera();
        }

        private void OnDestroy()
        {
            if (_steps != null)
            {
                _steps.StepStarted -= HandleStepStarted;
                _steps.StepFailed -= HandleStepFailed;
                _steps.StepsFinished -= HandleStepsFinished;
            }

            if (VOManager.Instance != null)
            {
                VOManager.Instance.LineStarted -= HandleLineStarted;
                VOManager.Instance.LineEnded -= HandleLineEnded;
            }

            LanguageService.Changed -= HandleLanguageChanged;
        }

        private void Update()
        {
            if (continuousHeadFollow)
            {
                UpdateHeadFollow();
            }
            UpdateCompressionVisuals();
            UpdateKeyboardShortcuts();

            if (_isErrorSubtitle && Time.time >= _errorClearAt)
            {
                _isErrorSubtitle = false;
                if (accentBar != null) accentBar.color = NormalAccent;
            }
        }

        public void Bind(StepSystem steps)
        {
            if (_steps != null)
            {
                _steps.StepStarted -= HandleStepStarted;
                _steps.StepFailed -= HandleStepFailed;
                _steps.StepsFinished -= HandleStepsFinished;
            }

            _steps = steps;
            if (_steps == null) return;

            cardiacMonitor?.Bind(_steps);
            _steps.StepStarted += HandleStepStarted;
            _steps.StepFailed += HandleStepFailed;
            _steps.StepsFinished += HandleStepsFinished;

            if (_steps.CurrentStep != null)
            {
                HandleStepStarted(_steps.CurrentStep, _steps.CurrentStepIndex, _steps.TotalSteps);
            }
        }

        private void WireButtons()
        {
            if (choiceBreathingNormalButton != null)
                choiceBreathingNormalButton.onClick.AddListener(OnBreathingNormalClicked);

            if (choiceNotBreathingButton != null)
                choiceNotBreathingButton.onClick.AddListener(OnNotBreathingClicked);

            if (languageButton != null)
                languageButton.onClick.AddListener(ToggleLanguage);

            if (repositionButton != null)
                repositionButton.onClick.AddListener(OnRepositionOrConfirmClicked);

            if (restartButton != null)
                restartButton.onClick.AddListener(RestartExperience);

            if (skipVoiceButton != null)
                skipVoiceButton.onClick.AddListener(() => VOManager.Instance?.Skip());
        }

        private void HandleStepStarted(StepDefinition step, int index, int total)
        {
            _currentStep = step;
            _stepStartTime = Time.time;
            _isErrorSubtitle = false;

            if (accentBar != null) accentBar.color = NormalAccent;

            bool es = LanguageService.Current == CoachLanguage.Spanish;
            string section = _steps != null ? _steps.GetSectionNameFor(index) : string.Empty;
            if (headerText != null)
            {
                string stepWord = es ? "Paso" : "Step";
                headerText.text = string.IsNullOrEmpty(section)
                    ? $"RCP • AR  |  {stepWord} {index + 1}/{total}"
                    : $"{section.ToUpperInvariant()}  |  {stepWord} {index + 1}/{total}";
            }

            if (stepTitleText != null)
            {
                stepTitleText.text = step != null ? step.titleText : string.Empty;
            }

            bool isChoice = step != null && step.stepId == "p3";
            if (choiceGroup != null) choiceGroup.SetActive(isChoice);

            bool isTimed = step != null && TimedStepSeconds.ContainsKey(step.stepId);
            if (compressionGroup != null) compressionGroup.SetActive(isTimed);
            bool lockReposition = isTimed || (step != null && step.stepId == "p5");
            if (repositionButton != null) repositionButton.interactable = !lockReposition;

            if (statusBadgeText != null)
            {
                if (isChoice)
                    statusBadgeText.text = es ? "Elige una opción" : "Select an option";
                else if (isTimed)
                    statusBadgeText.text = es ? "Mantén el ritmo" : "Keep the rhythm";
                else if (step != null && step.stepType == StepType.NonInteractive)
                    statusBadgeText.text = es ? "Escucha las indicaciones" : "Listen to guidance";
                else
                    statusBadgeText.text = es ? "Trigger / Pellizca o toca la marca" : "Trigger / Pinch or touch marker";
            }

            // Si no hay audio o antes de que arranque LineStarted, mostramos texto base de la instrucción.
            if (step != null && !step.instructionLine.IsEmpty && subtitleText != null)
            {
                subtitleText.text = ResolvePreviewText(step.instructionLine.id);
            }
        }

        private void HandleStepFailed(StepDefinition step, int index, string errorKey)
        {
            _isErrorSubtitle = true;
            _errorClearAt = Time.time + 6f;
            if (accentBar != null) accentBar.color = AlertAccent;
        }

        private void HandleStepsFinished()
        {
            if (choiceGroup != null) choiceGroup.SetActive(false);
            if (compressionGroup != null) compressionGroup.SetActive(false);
            if (repositionButton != null) repositionButton.interactable = true;

            bool es = LanguageService.Current == CoachLanguage.Spanish;
            if (statusBadgeText != null)
            {
                statusBadgeText.text = es ? "Protocolo completado" : "Protocol complete";
            }
        }

        private void HandleLineStarted(LinePlayback line)
        {
            if (line == null || subtitleText == null) return;
            subtitleText.text = line.Text;
            if (line.IsError && accentBar != null)
            {
                _isErrorSubtitle = true;
                _errorClearAt = Time.time + Mathf.Max(2f, line.Duration + 0.5f);
                accentBar.color = AlertAccent;
            }
        }

        private void HandleLineEnded(LinePlayback line)
        {
            if (line != null && line.IsError && accentBar != null)
            {
                accentBar.color = NormalAccent;
                _isErrorSubtitle = false;
            }
        }

        private void OnBreathingNormalClicked()
        {
            if (_currentStep?.completionCondition is KeyChoiceStepCondition choice)
            {
                choice.SimulateError();
            }
        }

        private void OnNotBreathingClicked()
        {
            if (_currentStep?.completionCondition is KeyChoiceStepCondition choice)
            {
                choice.SimulateCorrect();
            }
        }

        private void ToggleLanguage()
        {
            var next = LanguageService.Current == CoachLanguage.Spanish
                ? CoachLanguage.English
                : CoachLanguage.Spanish;
            LanguageService.Set(next);
        }

        public void SetPlacementModeState(bool placing)
        {
            _isPlacingPatient = placing;
            if (!placing)
            {
                RefreshLocalizedLabels();
                return;
            }

            if (choiceGroup != null) choiceGroup.SetActive(false);
            if (compressionGroup != null) compressionGroup.SetActive(false);
            if (accentBar != null) accentBar.color = NormalAccent;

            bool es = LanguageService.Current == CoachLanguage.Spanish;
            if (headerText != null)
                headerText.text = es ? "PASO 0  •  UBICAR PACIENTE" : "STEP 0  •  LOCATE PATIENT";
            if (stepTitleText != null)
                stepTitleText.text = es ? "Alinea al paciente en el piso" : "Align patient on the floor";
            if (subtitleText != null)
                subtitleText.text = es
                    ? "Apunta al pecho en el piso y haz Trigger o Pellizco (o toca CONFIRMAR AQUÍ)."
                    : "Aim at the chest on the floor and pull Trigger or Pinch (or tap CONFIRM HERE).";
            if (statusBadgeText != null)
                statusBadgeText.text = es ? "Esperando ubicación" : "Waiting placement";

            RefreshLocalizedLabels();
        }

        private void OnRepositionOrConfirmClicked()
        {
            if (patientLocator == null) patientLocator = FindFirstObjectByType<RcpPatientLocator>();
            if (patientLocator != null)
            {
                if (_isPlacingPatient || patientLocator.IsPlacingPatient)
                    patientLocator.ConfirmPlacement();
                else
                    patientLocator.BeginPlacement();
                return;
            }
            placementController?.PlaceInFrontOfUser();
        }

        private void RestartExperience()
        {
            if (_steps == null) return;
            VOManager.Instance?.StopAll();
            foreach (var hotspot in FindObjectsByType<XRLabs.Interactions.TouchableHotspot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                hotspot?.ResetHotspot();
            }
            _steps.StartSteps(XRLabs.Core.SimulationMode.Guided);
        }

        private void HandleLanguageChanged(CoachLanguage lang)
        {
            RefreshLocalizedLabels();
            if (_currentStep != null)
            {
                HandleStepStarted(_currentStep, _steps.CurrentStepIndex, _steps.TotalSteps);
            }
        }

        private void RefreshLocalizedLabels()
        {
            bool es = LanguageService.Current == CoachLanguage.Spanish;

            if (choiceNormalLabel != null)
                choiceNormalLabel.text = es ? "1. Respira con normalidad" : "1. Breathing normally";

            if (choiceNotBreathingLabel != null)
                choiceNotBreathingLabel.text = es ? "2. No respira / solo jadea" : "2. Not breathing / gasping";

            if (languageButtonLabel != null)
                languageButtonLabel.text = es ? "Idioma: ES" : "Lang: EN";

            if (repositionButtonLabel != null)
            {
                if (_isPlacingPatient)
                    repositionButtonLabel.text = es ? "Confirmar aquí" : "Confirm here";
                else
                    repositionButtonLabel.text = es ? "Reubicar" : "Relocate";
            }

            if (restartButtonLabel != null)
                restartButtonLabel.text = es ? "Reiniciar" : "Restart";

            if (skipVoiceLabel != null)
                skipVoiceLabel.text = es ? "Saltar voz" : "Skip voice";

            if (metronomeLabel != null)
                metronomeLabel.text = es ? "Ritmo: 110 LPM" : "Rate: 110 BPM";
        }

        private void UpdateCompressionVisuals()
        {
            if (compressionGroup == null || !compressionGroup.activeSelf || _currentStep == null) return;

            if (TimedStepSeconds.TryGetValue(_currentStep.stepId, out float totalSeconds) && timerText != null)
            {
                float remaining = Mathf.Max(0f, totalSeconds - (Time.time - _stepStartTime));
                timerText.text = $"{Mathf.CeilToInt(remaining)}s";
            }

            if (beatRingImage != null && metronome != null)
            {
                float pulse = metronome.BeatPulse;
                beatRingImage.color = Color.Lerp(BeatIdleColor, BeatPeakColor, pulse);
                beatRingImage.transform.localScale = Vector3.one * (1f + pulse * 0.25f);
            }

            if (compressionMonitor != null && metronomeLabel != null)
            {
                bool es = LanguageService.Current == CoachLanguage.Spanish;
                int bpm = Mathf.RoundToInt(compressionMonitor.MeasuredBpm);
                switch (compressionMonitor.CurrentState)
                {
                    case CompressionQualityState.HandsAwayFromChest:
                        metronomeLabel.text = es
                            ? "¡Manos sobre el pecho! (110 LPM)"
                            : "Hands on chest! (110 BPM)";
                        if (accentBar != null) accentBar.color = AlertAccent;
                        break;
                    case CompressionQualityState.HandsIdleOnChest:
                        metronomeLabel.text = es
                            ? "¡Presiona al ritmo! (110 LPM)"
                            : "Push to the beat! (110 BPM)";
                        if (accentBar != null) accentBar.color = AlertAccent;
                        break;
                    case CompressionQualityState.TooSlow:
                        metronomeLabel.text = es
                            ? $"Más rápido ▲ ({bpm} LPM)"
                            : $"Push faster ▲ ({bpm} BPM)";
                        if (accentBar != null) accentBar.color = AlertAccent;
                        break;
                    case CompressionQualityState.TooFast:
                        metronomeLabel.text = es
                            ? $"Más lento ▼ ({bpm} LPM)"
                            : $"Push slower ▼ ({bpm} BPM)";
                        if (accentBar != null) accentBar.color = AlertAccent;
                        break;
                    case CompressionQualityState.InPositionGoodRate:
                        metronomeLabel.text = bpm > 0
                            ? (es ? $"¡Buen ritmo! {bpm} LPM" : $"Good rate! {bpm} BPM")
                            : (es ? "Ritmo: 110 LPM" : "Rate: 110 BPM");
                        if (accentBar != null && !_isErrorSubtitle) accentBar.color = BeatPeakColor;
                        break;
                }
            }
        }

        private void UpdateKeyboardShortcuts()
        {
            // Atajo rápido en modo plano para recolocar (R).
            if (!XRSettings.isDeviceActive && UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.rKey.wasPressedThisFrame)
            {
                placementController?.PlaceInFrontOfUser();
            }
        }

        private void UpdateHeadFollow()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 flatForward = cam.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            flatForward.Normalize();

            Vector3 desiredPos = cam.transform.position + flatForward * followDistance;
            desiredPos.y = cam.transform.position.y + followHeightOffset;

            Quaternion desiredRot = Quaternion.LookRotation(desiredPos - cam.transform.position, Vector3.up);
            transform.position = Vector3.Lerp(transform.position, desiredPos, Time.deltaTime * followLerpSpeed);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, Time.deltaTime * followLerpSpeed);
        }

        public void SnapInFrontOfCamera()
        {
            // Si el HUD está anclado directamente sobre [Colocacion], se mantiene pegado al paciente.
            if (transform.parent != null && placementController != null && transform.parent == placementController.transform)
            {
                transform.localPosition = new Vector3(0f, 0.44f, 0.14f);
                transform.localRotation = Quaternion.Euler(20f, 0f, 0f);
                return;
            }

            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 flatForward = cam.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            flatForward.Normalize();

            Vector3 pos = cam.transform.position + flatForward * followDistance;
            pos.y = Mathf.Max(0.65f, cam.transform.position.y + followHeightOffset);
            transform.position = pos;
            transform.rotation = Quaternion.LookRotation(pos - cam.transform.position, Vector3.up);
        }

        private static string ResolvePreviewText(string lineId)
        {
            if (VOManager.Instance == null || string.IsNullOrEmpty(lineId)) return string.Empty;
            var lib = Resources.FindObjectsOfTypeAll<LineLibrary>();
            foreach (var l in lib)
            {
                if (l != null && l.TryResolve(lineId, LanguageService.Current, out var resolved))
                    return resolved.Text;
            }
            return string.Empty;
        }
    }
}
