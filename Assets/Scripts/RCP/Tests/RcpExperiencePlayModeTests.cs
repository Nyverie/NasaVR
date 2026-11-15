using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using XRLabs.Core;
using XRLabs.Interactions;

namespace NasaVR.RCP.Tests
{
    public class RcpExperiencePlayModeTests
    {
        private const string ScenePath = "Assets/Scenes/AsistenteRCP_AR.unity";

        private static IEnumerator LoadExperienceScene()
        {
            var op = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
            Assert.IsNotNull(op, "No se pudo cargar AsistenteRCP_AR.unity (asegúrate de tenerla en BuildSettings).");
            while (!op.isDone) yield return null;
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator SceneSetup_FlatFallback_DoesNotShowBlackBackground()
        {
            yield return LoadExperienceScene();

            var coordinator = Object.FindFirstObjectByType<RcpRigCoordinator>();
            Assert.IsNotNull(coordinator, "Falta RcpRigCoordinator en [Platform].");

            coordinator.ForceModeForTests(false);
            yield return null;

            Assert.IsFalse(coordinator.XrRigRoot.activeSelf, "XRLabsRig debe estar apagado en modo plano.");
            Assert.IsTrue(coordinator.FlatRigRoot.activeSelf, "FlatCamera debe estar activo en modo plano.");

            var flatCam = coordinator.FlatRigRoot.GetComponent<Camera>();
            Assert.IsNotNull(flatCam);
            Assert.Greater(flatCam.backgroundColor.a, 0.9f, "FlatCamera no debe quedar con alpha 0 (negro en Editor).");
        }

        [UnityTest]
        public IEnumerator XrMode_ActivatesRig_AttachesHandVisualizer_GluesHudToPatient_AndLowersCoach()
        {
            yield return LoadExperienceScene();

            var coordinator = Object.FindFirstObjectByType<RcpRigCoordinator>();
            var placement = Object.FindFirstObjectByType<RcpPlacementController>();
            var locator = Object.FindFirstObjectByType<RcpPatientLocator>();
            var hud = Object.FindFirstObjectByType<RcpExperienceHUD>();
            var coach = Object.FindFirstObjectByType<XRLabs.Coach.HologramCoach>();
            Assert.IsNotNull(coordinator);
            Assert.IsNotNull(placement);
            Assert.IsNotNull(locator);
            Assert.IsNotNull(hud);
            Assert.IsNotNull(coach);

            coordinator.ForceModeForTests(true);
            locator.ConfirmPlacement();
            yield return null;

            Assert.IsTrue(coordinator.XrRigRoot.activeSelf, "XRLabsRig debe encenderse en modo XR.");
            Assert.IsFalse(coordinator.FlatRigRoot.activeSelf, "FlatCamera debe apagarse en modo XR.");
            Assert.IsNotNull(coordinator.XrRigRoot.GetComponent<RcpHandVisualizer>(), "XRLabsRig debe tener RcpHandVisualizer.");
            Assert.AreEqual(placement.transform, hud.transform.parent, "Toda la UI ([RCP_HUD]) debe estar pegada a [Colocacion].");
            Assert.LessOrEqual(coach.transform.position.y, 0.5f, "El Coach debe apoyar su base en el suelo/nivel del paciente.");

            Camera xrCam = Camera.main;
            Assert.IsNotNull(xrCam, "Camera.main debe resolver al Main Camera de XRLabsRig.");
            Assert.AreEqual(0f, xrCam.backgroundColor.a, 0.01f, "La cámara XR debe tener alpha 0 para Passthrough.");

            Vector3 camForwardFlat = new Vector3(xrCam.transform.forward.x, 0f, xrCam.transform.forward.z).normalized;
            Vector3 toColocacion = placement.transform.position - xrCam.transform.position;
            float dist = new Vector2(toColocacion.x, toColocacion.z).magnitude;
            float dot = Vector3.Dot(camForwardFlat, new Vector3(toColocacion.x, 0f, toColocacion.z).normalized);

            Assert.Greater(dot, 0.7f, "[Colocacion] debe quedar frente al usuario.");
            Assert.That(dist, Is.InRange(0.40f, 1.50f), $"[Colocacion] distancia inesperada: {dist:0.00}m");
        }

        [UnityTest]
        public IEnumerator CprFlow_AdvancesSteps_HandlesP3Choice_ValidatesCompressionMonitor_AndHasAllAudioClips()
        {
            yield return LoadExperienceScene();

            var sim = SimulationManager.Instance;
            Assert.IsNotNull(sim);
            var steps = sim.Steps;
            Assert.IsNotNull(steps);
            Assert.AreEqual(10, steps.TotalSteps, "El guion debe tener 10 pasos (p0..p9).");

            var locator = Object.FindFirstObjectByType<RcpPatientLocator>();
            Assert.IsNotNull(locator);
            locator.SkipPlacementForTests();

            // Verifica que VOManager tenga cargada la librería con los 40 clips horneados (incluyendo p0.amarre ES/EN).
            var vo = VOManager.Instance;
            Assert.IsNotNull(vo);
            var libs = Resources.FindObjectsOfTypeAll<LineLibrary>();
            int clipsAssigned = 0;
            foreach (var lib in libs)
            {
                if (lib == null || !lib.name.Contains("rcp-asistente-emergencia-demo")) continue;
                foreach (var line in lib.lines)
                {
                    foreach (var v in line.variants)
                    {
                        if (v.clip != null) clipsAssigned++;
                    }
                }
            }
            Assert.AreEqual(40, clipsAssigned, "Deben existir los 40 AudioClips ES/EN horneados con ElevenLabs.");

            var guideVisual = Object.FindFirstObjectByType<RcpCompressionGuideVisual>();
            var ecgMonitor = Object.FindFirstObjectByType<RcpCardiacMonitorUI>();
            Assert.IsNotNull(guideVisual, "Debe existir RcpCompressionGuideVisual.");
            Assert.IsNotNull(ecgMonitor, "Debe existir RcpCardiacMonitorUI en el HUD.");

            // Inicia secuencia en p0 (sujeción al piso en microgravedad) -> p1 -> p2 tras saltar voz.
            steps.StartSteps(XRLabs.Core.SimulationMode.Guided);
            yield return null;
            Assert.AreEqual("p0", steps.CurrentStep?.stepId);
            Assert.IsFalse(guideVisual.IsShowing, "Las manos guía 3D no deben mostrarse antes de compresiones.");
            Assert.AreEqual(CardiacMonitorMode.CardiacArrest, ecgMonitor.CurrentMode);

            var chestCol = GameObject.Find("Objeto_pecho")?.GetComponent<Collider>();
            Assert.IsNotNull(chestCol);
            Assert.IsFalse(chestCol.enabled, "Objeto_pecho no debe capturar el láser ni parpadear al inicio.");

            yield return WaitForStep(steps, "p1", 2f);
            Assert.AreEqual("p1", steps.CurrentStep?.stepId);

            vo.Skip();
            yield return WaitForStep(steps, "p2", 2f);
            Assert.AreEqual("p2", steps.CurrentStep?.stepId);

            // Solo apuntar (hover) NO debe saltar el paso; requiere Select (Trigger / Pellizco).
            InvokeHotspotTouch("Objeto_hombros", fromHover: true);
            yield return null;
            yield return null;
            Assert.AreEqual("p2", steps.CurrentStep?.stepId, "Solo apuntar con hover no debe avanzar de paso.");

            InvokeHotspotTouch("Objeto_hombros", fromHover: false);
            yield return WaitForStep(steps, "p3", 2f);
            Assert.AreEqual("p3", steps.CurrentStep?.stepId);
            Assert.IsFalse(chestCol.enabled, "Objeto_pecho debe permanecer desactivado en p3 para priorizar botones UI.");

            // Opción errónea en p3 mantiene el paso en p3; espera fin de corrección antes del reintento válido.
            yield return WaitForActivePhase(steps, 2f);
            var choice = steps.CurrentStep?.completionCondition as KeyChoiceStepCondition;
            Assert.IsNotNull(choice, "p3 debe usar KeyChoiceStepCondition como completionCondition.");
            choice.SimulateError();
            yield return null;
            Assert.AreEqual("p3", steps.CurrentStep?.stepId);
            yield return WaitForActivePhase(steps, 2f);

            // Opción correcta en p3 avanza a p4.
            choice.SimulateCorrect();
            yield return WaitForStep(steps, "p4", 2f);
            Assert.AreEqual("p4", steps.CurrentStep?.stepId);
            yield return WaitForActivePhase(steps, 2f);

            // Hotspot confirmar (911) -> p5 -> hotspot pecho -> p6 con metrónomo, manos guía 3D, ECG y monitor activos.
            InvokeHotspotTouch("Objeto_confirmar", fromHover: false);
            yield return WaitForStep(steps, "p5", 2f);
            Assert.AreEqual("p5", steps.CurrentStep?.stepId);
            yield return WaitForActivePhase(steps, 2f);

            InvokeHotspotTouch("Objeto_pecho", fromHover: false);
            yield return WaitForStep(steps, "p6", 2f);
            Assert.AreEqual("p6", steps.CurrentStep?.stepId);
            yield return null;

            var metronome = Object.FindFirstObjectByType<RcpMetronome>();
            var monitor = Object.FindFirstObjectByType<RcpCompressionMonitor>();
            Assert.IsNotNull(metronome);
            Assert.IsNotNull(monitor);
            Assert.IsTrue(metronome.IsActive, "El metrónomo de 110 LPM debe activarse al llegar a p6.");
            Assert.IsTrue(guideVisual.IsShowing, "Las manos guía 3D (CPR-Guide.fbx) deben aparecer al iniciar compresiones.");
            Assert.AreEqual(CardiacMonitorMode.Compressions, ecgMonitor.CurrentMode);
            var repoBtn = GameObject.Find("BtnReposition")?.GetComponent<UnityEngine.UI.Button>();
            Assert.IsNotNull(repoBtn);
            Assert.IsFalse(repoBtn.interactable, "BtnReposition debe bloquearse durante compresiones para no mover el cuerpo.");

            // Sin manos cerca debe alertar HandsAwayFromChest; con compresiones a ~110 LPM debe validar InPositionGoodRate.
            monitor.ForceDistanceForTests(0.85f);
            yield return new WaitForSeconds(2.0f);
            Assert.AreEqual(CompressionQualityState.HandsAwayFromChest, monitor.CurrentState);

            monitor.ForceDistanceForTests(0.05f);
            for (int i = 0; i < 4; i++)
            {
                monitor.SimulateCompressionPulseForTests(0.05f);
                yield return new WaitForSeconds(0.54f);
            }
            Assert.AreEqual(CompressionQualityState.InPositionGoodRate, monitor.CurrentState);

            // Al llegar al paso final (p9), el paciente recupera pulso sinusal verde y se ocultan las manos guía.
            steps.JumpToStep(steps.TotalSteps - 1);
            yield return null;
            Assert.AreEqual("p9", steps.CurrentStep?.stepId);
            Assert.AreEqual(CardiacMonitorMode.RecoveredPulse, ecgMonitor.CurrentMode);
            Assert.IsFalse(guideVisual.IsShowing);
        }

        private static IEnumerator WaitForStep(StepSystem steps, string expectedId, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (steps.CurrentStep != null && steps.CurrentStep.stepId == expectedId)
                {
                    yield break;
                }
                VOManager.Instance?.Skip();
                yield return null;
            }
        }

        private static IEnumerator WaitForActivePhase(StepSystem steps, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (steps.Phase == StepSystem.StepPhase.Active)
                {
                    yield break;
                }
                VOManager.Instance?.Skip();
                yield return null;
            }
        }

        private static void InvokeHotspotTouch(string hotspotObjectName, bool fromHover)
        {
            var go = GameObject.Find(hotspotObjectName);
            Assert.IsNotNull(go, $"No se encontró el hotspot {hotspotObjectName}.");
            var hotspot = go.GetComponent<TouchableHotspot>();
            Assert.IsNotNull(hotspot, $"{hotspotObjectName} no tiene TouchableHotspot.");
            var method = typeof(TouchableHotspot).GetMethod("HandleTouch", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "No se encontró TouchableHotspot.HandleTouch.");
            method.Invoke(hotspot, new object[] { fromHover });
        }
    }
}
