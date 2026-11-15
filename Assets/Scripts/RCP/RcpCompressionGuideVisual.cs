using UnityEngine;

namespace NasaVR.RCP
{
    /// <summary>
    /// Muestra y sincroniza el modelo 3D animado de manos realizando RCP (CPR-Guide.fbx)
    /// sobre el área del pecho únicamente durante las fases de compresiones (p6-p8).
    /// </summary>
    public class RcpCompressionGuideVisual : MonoBehaviour
    {
        [SerializeField] private GameObject guideModelRoot;
        [SerializeField] private RcpMetronome metronome;
        [SerializeField] private RcpPatientLocator patientLocator;
        [SerializeField] private float pumpBounceAmplitude = 0.015f;

        private Animator _animator;
        private Animation _legacyAnimation;
        private Vector3 _baseLocalPosition;
        private bool _initializedPose;

        private void Awake()
        {
            CacheReferences();
        }

        private void OnEnable()
        {
            CacheReferences();
            UpdateVisibility();
        }

        private void Update()
        {
            bool shouldShow = ShouldBeVisible();
            if (guideModelRoot != null && guideModelRoot.activeSelf != shouldShow)
            {
                guideModelRoot.SetActive(shouldShow);
            }

            if (!shouldShow || guideModelRoot == null) return;

            float bpm = metronome != null ? metronome.BeatsPerMinute : 110f;
            float speedFactor = Mathf.Clamp(bpm / 110f, 0.7f, 1.4f);

            if (_animator != null)
            {
                _animator.speed = speedFactor;
            }
            if (_legacyAnimation != null)
            {
                if (!_legacyAnimation.isPlaying) _legacyAnimation.Play();
            }

            if (_initializedPose && metronome != null)
            {
                // Acompaña el ritmo del metrónomo con una leve pulsación vertical sobre el esternón.
                float offsetY = -pumpBounceAmplitude * metronome.BeatPulse;
                guideModelRoot.transform.localPosition = _baseLocalPosition + new Vector3(0f, offsetY, 0f);
            }
        }

        public bool IsShowing => guideModelRoot != null && guideModelRoot.activeSelf;

        private bool ShouldBeVisible()
        {
            if (patientLocator != null && patientLocator.IsPlacingPatient) return false;
            return metronome != null && metronome.IsActive;
        }

        private void UpdateVisibility()
        {
            if (guideModelRoot != null)
            {
                guideModelRoot.SetActive(ShouldBeVisible());
            }
        }

        private void CacheReferences()
        {
            if (metronome == null) metronome = FindFirstObjectByType<RcpMetronome>();
            if (patientLocator == null) patientLocator = FindFirstObjectByType<RcpPatientLocator>();
            if (guideModelRoot == null) return;

            if (!_initializedPose)
            {
                _baseLocalPosition = guideModelRoot.transform.localPosition;
                _initializedPose = true;
            }

            _animator = guideModelRoot.GetComponentInChildren<Animator>(true);
            _legacyAnimation = guideModelRoot.GetComponentInChildren<Animation>(true);
        }
    }
}
