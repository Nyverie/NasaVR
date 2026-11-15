using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XRLabs.Core;

namespace NasaVR.RCP
{
    public enum CardiacMonitorMode
    {
        CardiacArrest,
        Compressions,
        RecoveredPulse
    }

    /// <summary>
    /// Monitor ECG dinámico integrado al HUD AR.
    /// Muestra asistolia/fibrilación roja durante el paro (p1-p5), ondas de perfusión durante las compresiones (p6-p8)
    /// y recuperación a ritmo sinusal verde con latido cardíaco audible en p9 o al completar el ciclo.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class RcpCardiacMonitorUI : MonoBehaviour
    {
        [Header("Referencias UI")]
        [SerializeField] private RawImage ecgRawImage;
        [SerializeField] private TMP_Text ecgStatusLabel;
        [SerializeField] private TMP_Text ecgBpmLabel;
        [SerializeField] private Image ecgPanelBackground;

        [Header("Dependencias")]
        [SerializeField] private RcpMetronome metronome;
        [SerializeField] private RcpCompressionMonitor compressionMonitor;

        private const int TexWidth = 220;
        private const int TexHeight = 52;

        public CardiacMonitorMode CurrentMode { get; private set; } = CardiacMonitorMode.CardiacArrest;

        private Texture2D _texture;
        private Color32[] _pixels;
        private int _cursorX;
        private int _prevY = TexHeight / 2;
        private float _accumTime;
        private float _sinusTimer;
        private float _compressionImpulse;
        private float _lastBeatPulse;
        private StepSystem _steps;
        private AudioSource _audioSource;
        private AudioClip _sinusBeepClip;

        private static readonly Color32 BgColor = new(10, 18, 28, 235);
        private static readonly Color32 GridColor = new(24, 42, 56, 235);
        private static readonly Color32 ArrestColor = new(255, 68, 74, 255);
        private static readonly Color32 CompressionColor = new(46, 224, 235, 255);
        private static readonly Color32 RecoveredColor = new(52, 245, 138, 255);

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;
            _sinusBeepClip = CreateSinusChirpClip();

            InitTexture();
        }

        private void Start()
        {
            if (metronome == null) metronome = FindFirstObjectByType<RcpMetronome>();
            if (compressionMonitor == null) compressionMonitor = FindFirstObjectByType<RcpCompressionMonitor>();
            if (SimulationManager.Instance != null && SimulationManager.Instance.Steps != null)
            {
                Bind(SimulationManager.Instance.Steps);
            }
            LanguageService.Changed += HandleLanguageChanged;
            RefreshLabels();
        }

        private void OnDestroy()
        {
            if (_steps != null)
            {
                _steps.StepStarted -= HandleStepStarted;
                _steps.StepsFinished -= HandleStepsFinished;
            }
            LanguageService.Changed -= HandleLanguageChanged;
            if (_texture != null) Destroy(_texture);
            if (_sinusBeepClip != null) Destroy(_sinusBeepClip);
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
                UpdateModeForStep(_steps.CurrentStep.stepId);
            }
        }

        public void SetModeForTests(CardiacMonitorMode mode)
        {
            SetMode(mode);
        }

        private void HandleStepStarted(StepDefinition step, int index, int total)
        {
            UpdateModeForStep(step != null ? step.stepId : string.Empty);
        }

        private void HandleStepsFinished()
        {
            SetMode(CardiacMonitorMode.RecoveredPulse);
        }

        private void HandleLanguageChanged(CoachLanguage _)
        {
            RefreshLabels();
        }

        private void UpdateModeForStep(string stepId)
        {
            if (stepId == "p9")
            {
                SetMode(CardiacMonitorMode.RecoveredPulse);
            }
            else if (stepId == "p6" || stepId == "p7" || stepId == "p8")
            {
                SetMode(CardiacMonitorMode.Compressions);
            }
            else
            {
                SetMode(CardiacMonitorMode.CardiacArrest);
            }
        }

        private void SetMode(CardiacMonitorMode mode)
        {
            if (CurrentMode == mode) return;
            CurrentMode = mode;
            _sinusTimer = 0f;
            RefreshLabels();
        }

        private void Update()
        {
            if (_texture == null) return;

            if (metronome != null)
            {
                float pulse = metronome.BeatPulse;
                if (pulse > 0.72f && _lastBeatPulse <= 0.72f && CurrentMode == CardiacMonitorMode.Compressions)
                {
                    _compressionImpulse = 1f;
                }
                _lastBeatPulse = pulse;
            }

            if (CurrentMode == CardiacMonitorMode.RecoveredPulse)
            {
                _sinusTimer += Time.deltaTime;
                const float beatPeriod = 60f / 76f;
                if (_sinusTimer >= beatPeriod)
                {
                    _sinusTimer -= beatPeriod;
                    if (_audioSource != null && _sinusBeepClip != null)
                    {
                        _audioSource.PlayOneShot(_sinusBeepClip, 0.22f);
                    }
                }
            }

            _compressionImpulse = Mathf.Max(0f, _compressionImpulse - Time.deltaTime * 4.2f);
            _accumTime += Time.deltaTime;

            // Avanza el cursor a ~75 columnas por segundo.
            const float stepDt = 1f / 75f;
            bool dirty = false;
            while (_accumTime >= stepDt)
            {
                _accumTime -= stepDt;
                DrawNextColumn();
                dirty = true;
            }

            if (dirty)
            {
                _texture.SetPixels32(_pixels);
                _texture.Apply(false);
            }

            if (CurrentMode == CardiacMonitorMode.Compressions || CurrentMode == CardiacMonitorMode.CardiacArrest)
            {
                RefreshLabels();
            }
        }

        private void DrawNextColumn()
        {
            _cursorX = (_cursorX + 1) % TexWidth;

            // Limpia una banda por delante del cursor estilo osciloscopio hospitalario.
            for (int offset = 0; offset < 8; offset++)
            {
                int col = (_cursorX + offset) % TexWidth;
                for (int y = 0; y < TexHeight; y++)
                {
                    bool grid = (col % 22 == 0) || (y % 13 == 0);
                    _pixels[y * TexWidth + col] = grid ? GridColor : BgColor;
                }
            }

            float normSample = SampleWaveform(Time.time);
            int nextY = Mathf.Clamp(Mathf.RoundToInt((TexHeight * 0.42f) + normSample * (TexHeight * 0.44f)), 3, TexHeight - 4);
            Color32 lineColor = CurrentMode switch
            {
                CardiacMonitorMode.RecoveredPulse => RecoveredColor,
                CardiacMonitorMode.Compressions => CompressionColor,
                _ => ArrestColor
            };

            int yMin = Mathf.Min(_prevY, nextY);
            int yMax = Mathf.Max(_prevY, nextY);
            for (int y = yMin; y <= yMax; y++)
            {
                _pixels[y * TexWidth + _cursorX] = lineColor;
                if (y + 1 < TexHeight) _pixels[(y + 1) * TexWidth + _cursorX] = lineColor;
            }

            _prevY = nextY;
        }

        private float SampleWaveform(float t)
        {
            switch (CurrentMode)
            {
                case CardiacMonitorMode.RecoveredPulse:
                {
                    // Complejo P-QRS-T a ~76 LPM.
                    float phase = (_sinusTimer / (60f / 76f));
                    if (phase > 0.05f && phase < 0.16f)
                        return 0.18f * Mathf.Sin((phase - 0.05f) / 0.11f * Mathf.PI);
                    if (phase >= 0.20f && phase < 0.24f)
                        return -0.20f;
                    if (phase >= 0.24f && phase < 0.30f)
                        return 1.0f * (1f - Mathf.Abs(phase - 0.27f) / 0.03f);
                    if (phase >= 0.30f && phase < 0.35f)
                        return -0.34f * (1f - Mathf.Abs(phase - 0.325f) / 0.025f);
                    if (phase >= 0.46f && phase < 0.64f)
                        return 0.26f * Mathf.Sin((phase - 0.46f) / 0.18f * Mathf.PI);
                    return 0f;
                }
                case CardiacMonitorMode.Compressions:
                {
                    return 0.72f * _compressionImpulse + 0.05f * Mathf.Sin(t * 9f);
                }
                default:
                {
                    // Fibrilación ventricular baja / asistolia irregular.
                    return 0.10f * Mathf.Sin(t * 21f) * Mathf.Cos(t * 13f);
                }
            }
        }

        private void RefreshLabels()
        {
            bool es = LanguageService.Current == CoachLanguage.Spanish;
            bool blink = Mathf.Sin(Time.time * 5.5f) > 0f;

            switch (CurrentMode)
            {
                case CardiacMonitorMode.RecoveredPulse:
                    if (ecgStatusLabel != null)
                    {
                        ecgStatusLabel.text = es ? "♥ PULSO RECUPERADO (ROSC)" : "♥ PULSE RESTORED (ROSC)";
                        ecgStatusLabel.color = new Color(0.32f, 1f, 0.62f, 1f);
                    }
                    if (ecgBpmLabel != null)
                    {
                        ecgBpmLabel.text = es ? "76 LPM" : "76 BPM";
                        ecgBpmLabel.color = new Color(0.32f, 1f, 0.62f, 1f);
                    }
                    if (ecgPanelBackground != null)
                    {
                        ecgPanelBackground.color = new Color(0.05f, 0.20f, 0.14f, 0.92f);
                    }
                    break;

                case CardiacMonitorMode.Compressions:
                {
                    int bpm = compressionMonitor != null && compressionMonitor.MeasuredBpm > 1f
                        ? Mathf.RoundToInt(compressionMonitor.MeasuredBpm)
                        : 110;
                    if (ecgStatusLabel != null)
                    {
                        ecgStatusLabel.text = es ? "COMPRESIÓN ACTIVA" : "ACTIVE COMPRESSION";
                        ecgStatusLabel.color = new Color(0.45f, 0.94f, 1f, 1f);
                    }
                    if (ecgBpmLabel != null)
                    {
                        ecgBpmLabel.text = es ? $"{bpm} LPM" : $"{bpm} BPM";
                        ecgBpmLabel.color = Color.white;
                    }
                    if (ecgPanelBackground != null)
                    {
                        ecgPanelBackground.color = new Color(0.06f, 0.13f, 0.20f, 0.92f);
                    }
                    break;
                }

                default:
                    if (ecgStatusLabel != null)
                    {
                        ecgStatusLabel.text = es ? "(!) PARO CARDÍACO • SIN PULSO" : "(!) CARDIAC ARREST • NO PULSE";
                        ecgStatusLabel.color = blink ? new Color(1f, 0.36f, 0.38f, 1f) : new Color(0.85f, 0.25f, 0.28f, 0.7f);
                    }
                    if (ecgBpmLabel != null)
                    {
                        ecgBpmLabel.text = es ? "0 LPM" : "0 BPM";
                        ecgBpmLabel.color = new Color(1f, 0.38f, 0.38f, 1f);
                    }
                    if (ecgPanelBackground != null)
                    {
                        ecgPanelBackground.color = new Color(0.18f, 0.06f, 0.08f, 0.90f);
                    }
                    break;
            }
        }

        private void InitTexture()
        {
            _texture = new Texture2D(TexWidth, TexHeight, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _pixels = new Color32[TexWidth * TexHeight];
            for (int y = 0; y < TexHeight; y++)
            {
                for (int x = 0; x < TexWidth; x++)
                {
                    bool grid = (x % 22 == 0) || (y % 13 == 0);
                    _pixels[y * TexWidth + x] = grid ? GridColor : BgColor;
                }
            }
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);

            if (ecgRawImage != null)
            {
                ecgRawImage.texture = _texture;
            }
        }

        private static AudioClip CreateSinusChirpClip()
        {
            const int sampleRate = 22050;
            const float duration = 0.08f;
            int samples = Mathf.RoundToInt(sampleRate * duration);
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float env = Mathf.Sin(Mathf.PI * (i / (float)samples));
                data[i] = Mathf.Sin(2f * Mathf.PI * 660f * t) * env * 0.45f;
            }
            var clip = AudioClip.Create("RcpSinusChirp", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
