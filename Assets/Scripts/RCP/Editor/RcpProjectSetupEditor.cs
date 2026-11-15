using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;
using XRLabs.Authoring.Editor;
using XRLabs.Coach;
using XRLabs.Coach.Editor;
using XRLabs.Core;
using XRLabs.Interactions;
using XRLabs.Platform;
using XRLabs.Visor.Editor;

namespace NasaVR.RCP.Editor
{
    /// <summary>
    /// Configura el proyecto NasaVR para el Asistente de RCP en Passthrough (Meta Quest, sin login ni menú),
    /// limpia los restos innecesarios del template, hornea las voces ES/EN y arma la escena AsistenteRCP_AR.
    /// </summary>
    public static class RcpProjectSetupEditor
    {
        private const string ExperienceId = "rcp-asistente-emergencia-demo";
        private const string ScenePath = "Assets/Scenes/AsistenteRCP_AR.unity";
        private const string GuionJsonPath = "Docs/guion_rcp.json";
        private const string RigPrefabPath = "Packages/com.xrlabs.core/Runtime/MenuXRI/XRLabsRig.prefab";
        private const string CoachPrefabPath = "Packages/com.xrlabs.core/Runtime/Coach/Hologram/HologramCoach.prefab";
        private const string ApkOutputPath = "Builds/Android/AsistenteRCP_AR.apk";

        [MenuItem("Tools/NasaVR RCP/1. Limpiar, Configurar Escena y Hornear Voces")]
        public static void SetupProjectMenu()
        {
            SetupProjectInternal();
        }

        [MenuItem("Tools/NasaVR RCP/2. Compilar APK para Meta Quest")]
        public static void BuildQuestApkMenu()
        {
            BuildQuestApkInternal();
        }

        private const string DefaultElevenLabsVoiceId = "EXAVITQu4vr4xnSDxMaL"; // Sarah - Mature, Reassuring, Confident

        public static void SetupProjectFromCli()
        {
            bool ok = SetupProjectInternal();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Hornea las voces usando ElevenLabs (variable de entorno ELEVENLABS_API_KEY) bombeando el loop
        /// del Editor para las continuaciones async. Ejecutar en Unity batchmode SIN -quit.
        /// </summary>
        public static void BakeVoicesFromCli()
        {
            try
            {
                var settings = VoiceBakeRunner.GetOrCreateSettings();
                string libPath = $"Assets/Experiencias/{ExperienceId}/Frases/{ExperienceId}.asset";
                ReimportGuionIfPresent();
                var library = AssetDatabase.LoadAssetAtPath<LineLibrary>(libPath);
                if (library == null)
                    throw new InvalidOperationException("No se encontró LineLibrary en " + libPath);

                string voiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID");
                if (string.IsNullOrWhiteSpace(voiceId)) voiceId = DefaultElevenLabsVoiceId;

                if (VoiceApiKeys.Has(VoiceProviderKind.ElevenLabs))
                {
                    ConfigureElevenLabsSettings(settings, voiceId);
                }
                else
                {
                    Debug.LogWarning("[NasaVR RCP] ELEVENLABS_API_KEY no definida; usando MacSay.");
                    ConfigureMacSaySettings(settings);
                    if (MacSayProvider.IsSupported)
                    {
                        var syncSummary = new VoiceBaker(settings, new MacSayProvider { Synchronous = true })
                            .BakeAsync(library).GetAwaiter().GetResult();
                        Debug.Log("[NasaVR RCP] Horneado MacSay: " + syncSummary);
                    }
                    AssetDatabase.SaveAssets();
                    EditorApplication.Exit(0);
                    return;
                }

                var baker = VoiceBaker.CreateDefault(settings);
                var task = baker.BakeAsync(library, new BakeScope { Force = false });
                double startedAt = EditorApplication.timeSinceStartup;

                void PollBake()
                {
                    if (!task.IsCompleted)
                    {
                        if (EditorApplication.timeSinceStartup - startedAt > 240.0)
                        {
                            EditorApplication.update -= PollBake;
                            Debug.LogError("[NasaVR RCP] Timeout horneando voces ElevenLabs.");
                            EditorApplication.Exit(1);
                        }
                        return;
                    }

                    EditorApplication.update -= PollBake;
                    if (task.IsFaulted)
                    {
                        Debug.LogError("[NasaVR RCP] Error horneando ElevenLabs: " + task.Exception);
                        EditorApplication.Exit(1);
                        return;
                    }

                    var summary = task.Result;
                    Debug.Log("[NasaVR RCP] Horneado ElevenLabs: " + summary);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    bool ok = summary.Failed == 0 && string.IsNullOrEmpty(summary.AbortReason);
                    EditorApplication.Exit(ok ? 0 : 1);
                }

                EditorApplication.update += PollBake;
            }
            catch (Exception ex)
            {
                Debug.LogError("[NasaVR RCP] Error iniciando horneado ElevenLabs: " + ex);
                EditorApplication.Exit(1);
            }
        }

        public static void BuildQuestApkFromCli()
        {
            bool ok = BuildQuestApkInternal();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool SetupProjectInternal()
        {
            try
            {
                Debug.Log("[NasaVR RCP] Iniciando limpieza y configuración...");
                CleanTemplateLeftovers();
                EnsureExperienceImportedAndVoicesBaked();
                BuildExperienceScene();
                ConfigureUrpForQuest();
                ConfigurePlayerAndMetaSettings();
                CleanStaleOpenXrSettings();
                VisorOpenXr.Configure(VisorTarget.Quest3);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[NasaVR RCP] Configuración completada con éxito.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[NasaVR RCP] Error durante SetupProjectInternal: " + ex);
                return false;
            }
        }

        private static void CleanStaleOpenXrSettings()
        {
            const string oxrAsset = "Assets/XR/Settings/OpenXRPackageSettings.asset";
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(oxrAsset))
            {
                if (obj is UnityEngine.XR.OpenXR.OpenXRSettings oxr)
                {
                    var so = new SerializedObject(oxr);
                    var prop = so.FindProperty("features");
                    if (prop == null || !prop.isArray) continue;

                    bool changed = false;
                    for (int i = prop.arraySize - 1; i >= 0; i--)
                    {
                        var feat = prop.GetArrayElementAtIndex(i).objectReferenceValue;
                        if (feat == null)
                        {
                            prop.DeleteArrayElementAtIndex(i);
                            changed = true;
                            continue;
                        }

                        // Desactiva features de AndroidXR heredados para evitar conflictos en Quest.
                        if (feat.name.Contains("AndroidXR", StringComparison.OrdinalIgnoreCase) &&
                            feat is UnityEngine.XR.OpenXR.Features.OpenXRFeature f && f.enabled)
                        {
                            f.enabled = false;
                            EditorUtility.SetDirty(f);
                            changed = true;
                        }
                    }

                    // Habilita Passthrough y manos también en Standalone (Windows Quest Link).
                    if (oxr.name == "Standalone")
                    {
                        string[] linkFeatures =
                        {
                            "PassthroughFeature",
                            "HandTracking",
                            "MetaHandTrackingAim",
                            "HandInteractionProfile",
                            "MetaQuestTouchPlusControllerProfile",
                            "MetaQuestTouchProControllerProfile",
                            "OculusTouchControllerProfile",
                            "MetaXRFeature",
                            "DisplayUtilitiesFeature",
                            "OpenXRCompositionLayersFeature",
                            "FoveatedRenderingFeature"
                        };
                        foreach (var f in oxr.GetFeatures())
                        {
                            if (f == null) continue;
                            string typeName = f.GetType().Name;
                            if (linkFeatures.Contains(typeName) && !f.enabled)
                            {
                                f.enabled = true;
                                EditorUtility.SetDirty(f);
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                    {
                        so.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(oxr);
                    }
                }
            }
            AssetDatabase.SaveAssets();
        }

        private static bool BuildQuestApkInternal()
        {
            try
            {
                if (!SetupProjectInternal()) return false;

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                    {
                        Debug.LogError("[NasaVR RCP] No se pudo cambiar el Build Target a Android.");
                        return false;
                    }
                }

                // Aplica el perfil de OpenXR Quest3 tras cambiar de plataforma.
                VisorOpenXr.Configure(VisorTarget.Quest3);

                Directory.CreateDirectory(Path.GetDirectoryName(ApkOutputPath) ?? "Builds/Android");
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = ApkOutputPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.Development
                };

                Debug.Log($"[NasaVR RCP] Compilando APK en {ApkOutputPath}...");
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                Debug.Log($"[NasaVR RCP] Build result: {summary.result} | Errors: {summary.totalErrors} | Warnings: {summary.totalWarnings} | Size: {summary.totalSize / (1024f * 1024f):0.0} MB");
                return summary.result == BuildResult.Succeeded;
            }
            catch (Exception ex)
            {
                Debug.LogError("[NasaVR RCP] Error compilando APK: " + ex);
                return false;
            }
        }

        // ── 1. Limpieza del template ─────────────────────────────────────────

        private static void CleanTemplateLeftovers()
        {
            string[] junkPaths =
            {
                "Assets/Scenes/SampleScene.unity",
                "Assets/Scenes/SampleScene",
                "Assets/VRTemplateAssets",
                "Assets/_Recovery",
                "Assets/Scripts/FondoCamara.cs"
            };

            foreach (var path in junkPaths)
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null || AssetDatabase.IsValidFolder(path))
                {
                    if (AssetDatabase.DeleteAsset(path))
                    {
                        Debug.Log("[NasaVR RCP] Eliminado recurso innecesario del template: " + path);
                    }
                }
            }
        }

        // ── 2. Guion + Horneado de voces offline con MacSayProvider ──────────

        private static void ReimportGuionIfPresent()
        {
            if (!File.Exists(GuionJsonPath)) return;

            string json = File.ReadAllText(GuionJsonPath);
            var doc = GuionDocument.Parse(json);
            var report = GuionImporter.Import(doc);
            if (report.HasErrors)
                throw new InvalidOperationException("GuionImporter reportó errores al importar " + GuionJsonPath + ": " + report);
            AssetDatabase.Refresh();
        }

        private static void EnsureExperienceImportedAndVoicesBaked()
        {
            string seqPath = $"Assets/Experiencias/{ExperienceId}/Secuencias/{ExperienceId}.asset";
            string libPath = $"Assets/Experiencias/{ExperienceId}/Frases/{ExperienceId}.asset";

            ReimportGuionIfPresent();

            var sequence = AssetDatabase.LoadAssetAtPath<SimulationSequence>(seqPath);
            var library = AssetDatabase.LoadAssetAtPath<LineLibrary>(libPath);

            if (sequence == null || library == null)
            {
                throw new InvalidOperationException("Faltan los assets importados del guion RCP en Assets/Experiencias/.");
            }

            var settings = VoiceBakeRunner.GetOrCreateSettings();

            // Si ya está configurado con ElevenLabs (o existe la API key en entorno), no pisar con MacSay.
            bool hasElevenLabsClips = library.lines.Any(l => l.variants.Any(v => v.clip != null &&
                AssetDatabase.GetAssetPath(v.clip).EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)));
            if (settings.provider == VoiceProviderKind.ElevenLabs &&
                (hasElevenLabsClips || VoiceApiKeys.Has(VoiceProviderKind.ElevenLabs)))
            {
                Debug.Log("[NasaVR RCP] Manteniendo VoiceSettings de ElevenLabs.");
                return;
            }

            if (VoiceApiKeys.Has(VoiceProviderKind.ElevenLabs))
            {
                string voiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID");
                if (string.IsNullOrWhiteSpace(voiceId)) voiceId = DefaultElevenLabsVoiceId;
                ConfigureElevenLabsSettings(settings, voiceId);
                return;
            }

            ConfigureMacSaySettings(settings);
            if (MacSayProvider.IsSupported)
            {
                var baker = new VoiceBaker(settings, new MacSayProvider { Synchronous = true });
                var summary = baker.BakeAsync(library).GetAwaiter().GetResult();
                Debug.Log("[NasaVR RCP] Horneado de voces offline: " + summary);
            }
        }

        private static void ConfigureElevenLabsSettings(VoiceSettings settings, string voiceId)
        {
            settings.provider = VoiceProviderKind.ElevenLabs;
            settings.elevenLabsModelId = ElevenLabsProvider.DefaultModel;
            settings.allowIncompleteVoiceInDevBuilds = true;
            settings.maxConcurrency = 3;

            SetVoiceIds(settings, voiceId, null);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        private static void ConfigureMacSaySettings(VoiceSettings settings)
        {
            settings.provider = VoiceProviderKind.MacSay;
            settings.allowIncompleteVoiceInDevBuilds = true;

            SetVoiceIds(settings, null, MacSayProvider.DefaultSpanishVoice, MacSayProvider.DefaultEnglishVoice);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        private static void SetVoiceIds(VoiceSettings settings, string elevenId, string esMacVoice, string enMacVoice = null)
        {
            foreach (var lang in new[] { CoachLanguage.Spanish, CoachLanguage.English })
            {
                var entry = settings.voices.FirstOrDefault(v => v.language == lang);
                if (entry == null)
                {
                    entry = new LanguageVoice { language = lang };
                    settings.voices.Add(entry);
                }
                if (!string.IsNullOrEmpty(elevenId)) entry.elevenLabsVoiceId = elevenId;
                string macVoice = lang == CoachLanguage.Spanish ? esMacVoice : enMacVoice;
                if (!string.IsNullOrEmpty(macVoice) && string.IsNullOrEmpty(entry.systemVoice))
                    entry.systemVoice = macVoice;
            }
        }

        // ── 3. Construcción de la escena AsistenteRCP_AR.unity ───────────────

        private static void BuildExperienceScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var seq = AssetDatabase.LoadAssetAtPath<SimulationSequence>($"Assets/Experiencias/{ExperienceId}/Secuencias/{ExperienceId}.asset");
            var lib = AssetDatabase.LoadAssetAtPath<LineLibrary>($"Assets/Experiencias/{ExperienceId}/Frases/{ExperienceId}.asset");

            // Luz direccional suave para dar volumen a los marcadores 3D sin sombras costosas.
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // [Passthrough] + Foveated Rendering + Meta OVRManager/OVRPassthroughLayer preconfigurados
            var ptGo = new GameObject("[Passthrough]");
            var passthroughSetup = ptGo.AddComponent<RcpPassthroughSetup>();
            ptGo.AddComponent<FoveatedRenderingBootstrap>();
#if META_XR_SDK
            var ovrMgr = ptGo.AddComponent<OVRManager>();
            ovrMgr.isInsightPassthroughEnabled = true;
            ovrMgr.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            ovrMgr.launchSimultaneousHandsControllersOnStartup = true;
            var ovrPt = ptGo.AddComponent<OVRPassthroughLayer>();
#pragma warning disable CS0618
            ovrPt.overlayType = OVROverlay.OverlayType.Underlay;
#pragma warning restore CS0618
            ovrPt.textureOpacity = 1f;
            ovrPt.hidden = false;
#endif

            // [Platform]: XRLabsRig (XR/Hands/Controllers) + FlatCamera (Editor) + RcpRigCoordinator
            var platformGo = new GameObject("[Platform]");
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
            if (rigPrefab == null) throw new FileNotFoundException("No se encontró XRLabsRig.prefab en " + RigPrefabPath);

            var rigInstance = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
            rigInstance.name = "XRLabsRig";
            rigInstance.transform.SetParent(platformGo.transform, false);
            foreach (var cam in rigInstance.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                var urp = cam.GetComponent<UniversalAdditionalCameraData>();
                if (urp != null) urp.renderPostProcessing = false;
            }
            rigInstance.AddComponent<RcpHandVisualizer>();
            rigInstance.SetActive(false);

            var flatCamGo = new GameObject("FlatCamera");
            flatCamGo.transform.SetParent(platformGo.transform, false);
            flatCamGo.transform.position = new Vector3(0f, 1.50f, 0f);
            flatCamGo.tag = "MainCamera";
            var flatCam = flatCamGo.AddComponent<Camera>();
            flatCam.clearFlags = CameraClearFlags.SolidColor;
            flatCam.backgroundColor = new Color(0.08f, 0.10f, 0.14f, 1f);
            flatCam.nearClipPlane = 0.05f;
            flatCamGo.AddComponent<AudioListener>();
            flatCamGo.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            flatCamGo.AddComponent<FlatInteractionDriver>();
            flatCamGo.AddComponent<OrbitCameraController>();
            flatCamGo.SetActive(false);

            var rigCoordinator = platformGo.AddComponent<RcpRigCoordinator>();

            // [Simulation]: StepSystem, SimulationManager, VOManager, HintSystem, StepGuidance, Metrónomo
            var simGo = new GameObject("[Simulation]");
            var simManager = simGo.AddComponent<SimulationManager>();
            var stepSystem = simGo.AddComponent<StepSystem>();
            var scoring = simGo.AddComponent<ScoringSystem>();
            var telemetry = simGo.AddComponent<TelemetryClient>();
            var voManager = simGo.AddComponent<VOManager>();
            simGo.AddComponent<HintSystem>();
            simGo.AddComponent<FeedbackSystem>();
            var guidance = simGo.AddComponent<StepGuidance>();
            var metronome = simGo.AddComponent<RcpMetronome>();
            var compressionMonitor = simGo.AddComponent<RcpCompressionMonitor>();

            var stepSo = new SerializedObject(stepSystem);
            stepSo.FindProperty("sequence").objectReferenceValue = seq;
            stepSo.ApplyModifiedPropertiesWithoutUndo();

            var voSo = new SerializedObject(voManager);
            var libsProp = voSo.FindProperty("lineLibraries");
            libsProp.arraySize = 1;
            libsProp.GetArrayElementAtIndex(0).objectReferenceValue = lib;
            voSo.ApplyModifiedPropertiesWithoutUndo();

            var simSo = new SerializedObject(simManager);
            simSo.FindProperty("simulationMode").enumValueIndex = (int)XRLabs.Core.SimulationMode.Guided;
            simSo.FindProperty("stepSystem").objectReferenceValue = stepSystem;
            simSo.FindProperty("scoringSystem").objectReferenceValue = scoring;
            simSo.FindProperty("telemetryClient").objectReferenceValue = telemetry;
            simSo.ApplyModifiedPropertiesWithoutUndo();

            var guidSo = new SerializedObject(guidance);
            guidSo.FindProperty("stepSystem").objectReferenceValue = stepSystem;
            guidSo.ApplyModifiedPropertiesWithoutUndo();

            // [Colocacion]: Silueta en piso + Reticle Paso 0 + Hotspots + Ancla Coach + HUD pegado al paciente
            var colocacionGo = new GameObject("[Colocacion]");
            colocacionGo.transform.position = new Vector3(0f, 0.42f, 0.68f);
            var placementCtrl = colocacionGo.AddComponent<RcpPlacementController>();
            var patientLocator = colocacionGo.AddComponent<RcpPatientLocator>();

            var inicioGo = new GameObject("Inicio");
            inicioGo.transform.SetParent(colocacionGo.transform, false);
            var inicioTarget = inicioGo.AddComponent<SceneTarget>();
            SetSerializedField(inicioTarget, "targetId", "inicio");

            var anchorGo = new GameObject("Ancla_paciente");
            anchorGo.transform.SetParent(colocacionGo.transform, false);
            anchorGo.transform.localPosition = new Vector3(-0.56f, -0.08f, 0.14f);
            var anchor = anchorGo.AddComponent<CoachAnchor>();
            SetSerializedField(anchor, "anchorId", "paciente");
            SetSerializedField(anchor, "faceUser", true);

            // [Coach]: HologramCoach a nivel de piso junto al paciente (discHeight 0.85m) y retrato de enfermera
            var coachRoot = new GameObject("[Coach]");
            var coachPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CoachPrefabPath);
            if (coachPrefab != null)
            {
                var coachInst = (GameObject)PrefabUtility.InstantiatePrefab(coachPrefab, scene);
                coachInst.name = "Coach";
                coachInst.transform.SetParent(coachRoot.transform, false);
                coachInst.transform.position = new Vector3(-0.56f, 0.34f, 0.82f);

                var holo = coachInst.GetComponent<HologramCoach>();
                if (holo != null)
                {
                    const string portraitPath = "Assets/Textures/CoachEnfermera.jpg";
                    AssetDatabase.ImportAsset(portraitPath, ImportAssetOptions.ForceUpdate);
                    var portraitTex = AssetDatabase.LoadAssetAtPath<Texture2D>(portraitPath);
                    var holoSo = new SerializedObject(holo);
                    if (portraitTex != null)
                    {
                        holoSo.FindProperty("portraitTexture").objectReferenceValue = portraitTex;
                    }
                    holoSo.FindProperty("discHeight").floatValue = 0.85f;
                    holoSo.FindProperty("discRadius").floatValue = 0.17f;
                    holoSo.FindProperty("speakerName").stringValue = "Enfermera Clara";
                    holoSo.FindProperty("roleLabel").stringValue = "Asistente RCP";
                    holoSo.ApplyModifiedPropertiesWithoutUndo();
                }

                var presence = coachInst.GetComponent<CoachPresence>();
                var director = coachRoot.AddComponent<CoachDirector>();
                var dirSo = new SerializedObject(director);
                dirSo.FindProperty("stepSystem").objectReferenceValue = stepSystem;
                dirSo.FindProperty("presence").objectReferenceValue = presence;
                dirSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // Silueta corporal holográfica translúcida y reticle Paso 0
            var floorReticle = CreatePatientSilhouetteAndReticle(colocacionGo.transform);

            CreateShouldersHotspot(
                colocacionGo.transform,
                new Vector3(0f, 0.055f, 0.16f));

            var chestHotspot = CreateChestHotspot(
                colocacionGo.transform,
                new Vector3(0f, 0.02f, 0f));

            CreateCall911Hotspot(
                colocacionGo.transform,
                new Vector3(-0.34f, 0.16f, 0.10f));

            var handsGuideGo = CreateCompressionHandsGuide(colocacionGo.transform, new Vector3(0f, 0.03f, 0f));
            var handsGuideCtrl = colocacionGo.AddComponent<RcpCompressionGuideVisual>();
            var guideSo = new SerializedObject(handsGuideCtrl);
            guideSo.FindProperty("guideModelRoot").objectReferenceValue = handsGuideGo;
            guideSo.FindProperty("metronome").objectReferenceValue = metronome;
            guideSo.FindProperty("patientLocator").objectReferenceValue = patientLocator;
            guideSo.ApplyModifiedPropertiesWithoutUndo();

            // [RCP_HUD]: Panel compacto pegado directamente sobre [Colocacion] con Monitor ECG en vivo
            var hud = CreateHud(scene, colocacionGo.transform, placementCtrl, metronome, compressionMonitor, stepSystem);
            var hudSo = new SerializedObject(hud);
            hudSo.FindProperty("patientLocator").objectReferenceValue = patientLocator;
            hudSo.FindProperty("compressionMonitor").objectReferenceValue = compressionMonitor;
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            var compSo = new SerializedObject(compressionMonitor);
            compSo.FindProperty("chestTarget").objectReferenceValue = chestHotspot.transform;
            compSo.FindProperty("rigRoot").objectReferenceValue = rigInstance.transform;
            compSo.FindProperty("metronome").objectReferenceValue = metronome;
            compSo.ApplyModifiedPropertiesWithoutUndo();

            var placeSo = new SerializedObject(placementCtrl);
            placeSo.FindProperty("coachTransform").objectReferenceValue = coachRoot.transform;
            placeSo.FindProperty("experienceHud").objectReferenceValue = hud;
            placeSo.FindProperty("compressionMonitor").objectReferenceValue = compressionMonitor;
            placeSo.ApplyModifiedPropertiesWithoutUndo();

            var locSo = new SerializedObject(patientLocator);
            locSo.FindProperty("placementController").objectReferenceValue = placementCtrl;
            locSo.FindProperty("experienceHud").objectReferenceValue = hud;
            locSo.FindProperty("stepSystem").objectReferenceValue = stepSystem;
            locSo.FindProperty("floorReticle").objectReferenceValue = floorReticle;
            locSo.ApplyModifiedPropertiesWithoutUndo();

            var coordSo = new SerializedObject(rigCoordinator);
            coordSo.FindProperty("xrRigRoot").objectReferenceValue = rigInstance;
            coordSo.FindProperty("flatRigRoot").objectReferenceValue = flatCamGo;
            coordSo.FindProperty("passthroughSetup").objectReferenceValue = passthroughSetup;
            coordSo.FindProperty("placementController").objectReferenceValue = placementCtrl;
            coordSo.FindProperty("experienceHud").objectReferenceValue = hud;
            coordSo.FindProperty("coachRoot").objectReferenceValue = coachRoot.transform;
            coordSo.ApplyModifiedPropertiesWithoutUndo();

            // EventSystem con XRUIInputModule (soporta Hands/Controllers XR y Mouse en Editor)
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<XRUIInputModule>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? "Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            // Sin Login, sin Menú: AsistenteRCP_AR es la única escena en BuildSettings (índice 0).
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[NasaVR RCP] Escena guardada y puesta en BuildSettings[0]: " + ScenePath);
        }

        private static Transform CreatePatientSilhouetteAndReticle(Transform parent)
        {
            var bodyRoot = new GameObject("SiluetaPaciente");
            bodyRoot.transform.SetParent(parent, false);

            var holoMat = CreateUrpLitMaterial(new Color(0.08f, 0.56f, 0.78f, 0.32f), transparent: true, emissionBoost: 0.45f);
            var clavMat = CreateUrpLitMaterial(new Color(0.18f, 0.88f, 0.86f, 0.52f), transparent: true, emissionBoost: 0.65f);

            void AddPart(string name, PrimitiveType shape, Vector3 localPos, Vector3 rot, Vector3 scale, Material mat, Transform p = null)
            {
                var part = GameObject.CreatePrimitive(shape);
                part.name = name;
                part.transform.SetParent(p ?? bodyRoot.transform, false);
                part.transform.localPosition = localPos;
                part.transform.localEulerAngles = rot;
                part.transform.localScale = scale;
                var col = part.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);
                part.GetComponent<Renderer>().sharedMaterial = mat;
            }

            // Silueta anatómica supina translúcida (tórax, hombros, clavículas, cuello y cabeza)
            AddPart("ToraxSuperior", PrimitiveType.Capsule, new Vector3(0f, -0.055f, 0.03f), new Vector3(90f, 0f, 0f), new Vector3(0.30f, 0.22f, 0.09f), holoMat);
            AddPart("AbdomenBase", PrimitiveType.Capsule, new Vector3(0f, -0.062f, -0.14f), new Vector3(90f, 0f, 0f), new Vector3(0.25f, 0.14f, 0.08f), holoMat);
            AddPart("HombroIzq", PrimitiveType.Sphere, new Vector3(-0.17f, -0.05f, 0.14f), Vector3.zero, new Vector3(0.11f, 0.065f, 0.12f), holoMat);
            AddPart("HombroDer", PrimitiveType.Sphere, new Vector3(0.17f, -0.05f, 0.14f), Vector3.zero, new Vector3(0.11f, 0.065f, 0.12f), holoMat);
            AddPart("ClaviculaGuia", PrimitiveType.Capsule, new Vector3(0f, -0.032f, 0.14f), new Vector3(0f, 0f, 90f), new Vector3(0.025f, 0.16f, 0.025f), clavMat);
            AddPart("EsternonGuia", PrimitiveType.Capsule, new Vector3(0f, -0.022f, 0.02f), new Vector3(90f, 0f, 0f), new Vector3(0.035f, 0.10f, 0.02f), clavMat);
            AddPart("CuelloGuia", PrimitiveType.Cylinder, new Vector3(0f, -0.052f, 0.22f), new Vector3(90f, 0f, 0f), new Vector3(0.085f, 0.05f, 0.065f), holoMat);
            AddPart("CabezaGuia", PrimitiveType.Sphere, new Vector3(0f, -0.035f, 0.34f), Vector3.zero, new Vector3(0.165f, 0.135f, 0.205f), holoMat);

            // Reticle luminoso en piso visible durante Paso 0 ("Ubicar paciente")
            var reticle = new GameObject("ReticlePiso");
            reticle.transform.SetParent(parent, false);
            reticle.transform.localPosition = new Vector3(0f, -0.068f, 0.02f);
            AddPart("AnilloExterior", PrimitiveType.Cylinder, Vector3.zero, Vector3.zero, new Vector3(0.82f, 0.006f, 0.82f), clavMat, reticle.transform);
            AddPart("CentroPecho", PrimitiveType.Cylinder, new Vector3(0f, 0.005f, 0f), Vector3.zero, new Vector3(0.22f, 0.008f, 0.22f),
                CreateUrpLitMaterial(new Color(0.20f, 1f, 0.70f, 0.88f), transparent: true, emissionBoost: 0.9f), reticle.transform);

            return reticle.transform;
        }

        private static GameObject CreateShouldersHotspot(Transform parent, Vector3 localPos)
        {
            var go = new GameObject("Objeto_hombros");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var padMat = CreateUrpLitMaterial(new Color(0.18f, 0.78f, 0.98f, 0.88f), transparent: true, emissionBoost: 0.55f);
            var barMat = CreateUrpLitMaterial(new Color(0.45f, 0.92f, 1f, 0.65f), transparent: true, emissionBoost: 0.4f);

            AddPrimitiveVisual(go.transform, "PadHombroIzq", PrimitiveType.Cylinder, new Vector3(-0.17f, 0f, 0f), Vector3.zero, new Vector3(0.10f, 0.012f, 0.10f), padMat);
            AddPrimitiveVisual(go.transform, "PadHombroDer", PrimitiveType.Cylinder, new Vector3(0.17f, 0f, 0f), Vector3.zero, new Vector3(0.10f, 0.012f, 0.10f), padMat);
            AddPrimitiveVisual(go.transform, "PuenteHombros", PrimitiveType.Capsule, new Vector3(0f, 0.005f, 0f), new Vector3(0f, 0f, 90f), new Vector3(0.018f, 0.16f, 0.018f), barMat);

            ConfigureHotspotBehavior(go, "hombros", 0.21f);
            CreateHotspotLabel(go.transform, "TOCAR HOMBROS", new Vector3(0f, 0.095f, 0f));
            return go;
        }

        private static GameObject CreateChestHotspot(Transform parent, Vector3 localPos)
        {
            var go = new GameObject("Objeto_pecho");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var ringMat = CreateUrpLitMaterial(new Color(0.15f, 0.88f, 0.72f, 0.58f), transparent: true, emissionBoost: 0.65f);
            var crossMat = CreateUrpLitMaterial(new Color(0.28f, 1f, 0.78f, 0.92f), transparent: true, emissionBoost: 0.95f);

            AddPrimitiveVisual(go.transform, "AnilloEsternon", PrimitiveType.Cylinder, Vector3.zero, Vector3.zero, new Vector3(0.19f, 0.008f, 0.19f), ringMat);
            AddPrimitiveVisual(go.transform, "CruzMedicaV", PrimitiveType.Cube, new Vector3(0f, 0.006f, 0f), Vector3.zero, new Vector3(0.028f, 0.008f, 0.10f), crossMat);
            AddPrimitiveVisual(go.transform, "CruzMedicaH", PrimitiveType.Cube, new Vector3(0f, 0.006f, 0f), Vector3.zero, new Vector3(0.10f, 0.008f, 0.028f), crossMat);

            ConfigureHotspotBehavior(go, "pecho", 0.14f);
            CreateHotspotLabel(go.transform, "CENTRO DEL PECHO", new Vector3(0f, 0.09f, -0.09f));
            return go;
        }

        private static GameObject CreateCall911Hotspot(Transform parent, Vector3 localPos)
        {
            var go = new GameObject("Objeto_confirmar");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var darkMat = CreateUrpLitMaterial(new Color(0.10f, 0.14f, 0.20f, 1f));
            var sosMat = CreateUrpLitMaterial(new Color(0.88f, 0.18f, 0.18f, 1f), emissionBoost: 0.55f);
            var whiteMat = CreateUrpLitMaterial(Color.white, emissionBoost: 0.2f);
            var beaconMat = CreateUrpLitMaterial(new Color(0.20f, 0.72f, 1f, 0.92f), transparent: true, emissionBoost: 0.9f);

            // Consola compacta SOS 911 en 3D
            AddPrimitiveVisual(go.transform, "BaseConsola", PrimitiveType.Cube, new Vector3(0f, -0.015f, 0f), new Vector3(-18f, 0f, 0f), new Vector3(0.16f, 0.105f, 0.026f), darkMat);
            AddPrimitiveVisual(go.transform, "BotonSOS", PrimitiveType.Cube, new Vector3(0f, -0.015f, -0.011f), new Vector3(-18f, 0f, 0f), new Vector3(0.135f, 0.082f, 0.015f), sosMat);
            AddPrimitiveVisual(go.transform, "TelefonoBarra", PrimitiveType.Cube, new Vector3(0f, -0.008f, -0.021f), new Vector3(-18f, 0f, 30f), new Vector3(0.068f, 0.016f, 0.012f), whiteMat);
            AddPrimitiveVisual(go.transform, "TelefonoAuricularA", PrimitiveType.Sphere, new Vector3(-0.025f, -0.022f, -0.021f), Vector3.zero, new Vector3(0.021f, 0.021f, 0.015f), whiteMat);
            AddPrimitiveVisual(go.transform, "TelefonoAuricularB", PrimitiveType.Sphere, new Vector3(0.025f, 0.006f, -0.021f), Vector3.zero, new Vector3(0.021f, 0.021f, 0.015f), whiteMat);
            AddPrimitiveVisual(go.transform, "BalizaRoja", PrimitiveType.Cylinder, new Vector3(-0.022f, 0.045f, 0f), Vector3.zero, new Vector3(0.032f, 0.014f, 0.032f), sosMat);
            AddPrimitiveVisual(go.transform, "BalizaAzul", PrimitiveType.Cylinder, new Vector3(0.022f, 0.045f, 0f), Vector3.zero, new Vector3(0.032f, 0.014f, 0.032f), beaconMat);

            ConfigureHotspotBehavior(go, "confirmar", 0.13f);
            CreateHotspotLabel(go.transform, "911 • LLAMAR", new Vector3(0f, 0.095f, 0f));
            return go;
        }

        private static GameObject CreateCompressionHandsGuide(Transform parent, Vector3 localPos)
        {
            const string modelPath = "Assets/Models/CPR-Guide.fbx";
            const string controllerPath = "Assets/Models/CPR-Guide.controller";

            var holder = new GameObject("GuiaManosRCP");
            holder.transform.SetParent(parent, false);
            holder.transform.localPosition = localPos;

            if (File.Exists(modelPath))
            {
                AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);
                if (AssetImporter.GetAtPath(modelPath) is ModelImporter importer)
                {
                    importer.animationType = ModelImporterAnimationType.Generic;
                    var clipAnims = importer.clipAnimations != null && importer.clipAnimations.Length > 0
                        ? importer.clipAnimations
                        : importer.defaultClipAnimations;
                    for (int i = 0; i < clipAnims.Length; i++)
                    {
                        clipAnims[i].loopTime = true;
                    }
                    if (clipAnims.Length > 0)
                    {
                        importer.clipAnimations = clipAnims;
                    }
                    importer.SaveAndReimport();
                }

                var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (modelAsset != null)
                {
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, holder.transform);
                    inst.name = "CPR-Guide_Model";

                    // Quita cualquier collider del modelo para no interferir con raycasts de Objeto_pecho.
                    foreach (var c in inst.GetComponentsInChildren<Collider>(true))
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                    }

                    // Normaliza escala y apoyo sobre el esternón sin importar escala original del FBX.
                    var renderers = inst.GetComponentsInChildren<Renderer>(true);
                    var holoHandsMat = CreateUrpLitMaterial(new Color(0.20f, 0.94f, 0.88f, 0.72f), transparent: true, emissionBoost: 0.45f);
                    if (renderers.Length > 0)
                    {
                        Bounds combined = renderers[0].bounds;
                        for (int i = 0; i < renderers.Length; i++)
                        {
                            combined.Encapsulate(renderers[i].bounds);
                            renderers[i].sharedMaterial = holoHandsMat;
                        }

                        float maxHoriz = Mathf.Max(combined.size.x, combined.size.z, 0.001f);
                        float scale = 0.24f / maxHoriz;
                        inst.transform.localScale = Vector3.one * scale;
                        float minYOffset = -combined.min.y * scale;
                        inst.transform.localPosition = new Vector3(0f, minYOffset, 0f);
                    }

                    var clips = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>()
                        .Where(c => c != null && !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                        .ToArray();

                    if (clips.Length > 0)
                    {
                        if (File.Exists(controllerPath)) AssetDatabase.DeleteAsset(controllerPath);
                        var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                        controller.AddMotion(clips[0]);

                        var animator = inst.GetComponent<Animator>();
                        if (animator == null) animator = inst.AddComponent<Animator>();
                        animator.runtimeAnimatorController = controller;
                    }
                }
            }

            holder.SetActive(false);
            return holder;
        }

        private static void AddPrimitiveVisual(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 rot, Vector3 scale, Material mat)
        {
            var p = GameObject.CreatePrimitive(type);
            p.name = name;
            p.transform.SetParent(parent, false);
            p.transform.localPosition = pos;
            p.transform.localEulerAngles = rot;
            p.transform.localScale = scale;
            var c = p.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
            p.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void ConfigureHotspotBehavior(GameObject go, string interactionId, float colliderRadius)
        {
            var col = go.AddComponent<SphereCollider>();
            col.radius = colliderRadius;

            go.AddComponent<XRSimpleInteractable>();
            var hotspot = go.AddComponent<TouchableHotspot>();
            SetSerializedField(hotspot, "interactionId", interactionId);
            // Requiere Trigger / Pellizco / toque físico directo (no solo apuntar con el láser).
            SetSerializedField(hotspot, "triggerOnTouch", false);
            SetSerializedField(hotspot, "oneShot", true);

            var target = go.AddComponent<SceneTarget>();
            SetSerializedField(target, "targetId", interactionId);
        }

        private static void CreateHotspotLabel(Transform parent, string text, Vector3 localPos)
        {
            var labelGo = new GameObject("Etiqueta");
            labelGo.transform.SetParent(parent, false);
            labelGo.transform.localPosition = localPos;
            var fc = labelGo.AddComponent<FaceCamera>();
            SetSerializedField(fc, "backToCamera", true);

            var bg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bg.name = "PillBg";
            bg.transform.SetParent(labelGo.transform, false);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.004f);
            bg.transform.localScale = new Vector3(0.24f, 0.052f, 0.004f);
            var bgCol = bg.GetComponent<Collider>();
            if (bgCol != null) UnityEngine.Object.DestroyImmediate(bgCol);
            bg.GetComponent<Renderer>().sharedMaterial = CreateUrpLitMaterial(new Color(0.05f, 0.09f, 0.14f, 0.85f), transparent: true);

            var textGo = new GameObject("Texto");
            textGo.transform.SetParent(labelGo.transform, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = 0.21f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.rectTransform.sizeDelta = new Vector2(0.40f, 0.09f);
        }

        private static Material CreateUrpLitMaterial(Color color, bool transparent = false, float emissionBoost = 0.30f)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (transparent)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
                if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)RenderQueue.Transparent;
            }
            if (emissionBoost > 0f && mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", color * emissionBoost);
            }
            return mat;
        }

        private static RcpExperienceHUD CreateHud(Scene scene, Transform patientAnchorParent,
            RcpPlacementController placementCtrl, RcpMetronome metronome,
            RcpCompressionMonitor compressionMonitor, StepSystem stepSystem)
        {
            var root = new GameObject("[RCP_HUD]");
            SceneManager.MoveGameObjectToScene(root, scene);
            if (patientAnchorParent != null)
            {
                root.transform.SetParent(patientAnchorParent, false);
            }

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(1080f, 660f);
            rt.localScale = Vector3.one * 0.00062f;
            rt.localPosition = new Vector3(0f, 0.44f, 0.14f);
            rt.localRotation = Quaternion.Euler(20f, 0f, 0f);

            root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
            root.AddComponent<GraphicRaycaster>();
            root.AddComponent<TrackedDeviceGraphicRaycaster>();

            // Fondo principal
            var bg = CreateUiBox("Background", root.transform, Vector2.zero, Vector2.one, new Color(0.05f, 0.09f, 0.14f, 0.92f));
            bg.GetComponent<Image>().raycastTarget = false;

            var accent = CreateUiBox("AccentBar", root.transform, new Vector2(0f, 0f), new Vector2(0.014f, 1f), new Color(0.14f, 0.78f, 0.82f, 1f)).GetComponent<Image>();
            accent.raycastTarget = false;

            var headerText = CreateUiText("Header", root.transform, new Vector2(0.04f, 0.88f), new Vector2(0.62f, 0.97f), 20f, FontStyles.Bold, new Color(0.55f, 0.90f, 0.96f, 1f), TextAlignmentOptions.Left);
            var badgeText = CreateUiText("StatusBadge", root.transform, new Vector2(0.62f, 0.88f), new Vector2(0.96f, 0.97f), 19f, FontStyles.Bold, new Color(0.95f, 0.88f, 0.45f, 1f), TextAlignmentOptions.Right);

            // Franja de Monitor Cardíaco ECG en vivo (paro cardíaco -> compresiones -> pulso recuperado)
            var ecgBox = CreateUiBox("EcgMonitorPanel", root.transform, new Vector2(0.04f, 0.72f), new Vector2(0.96f, 0.86f), new Color(0.18f, 0.06f, 0.08f, 0.90f));
            var ecgBgImg = ecgBox.GetComponent<Image>();
            ecgBgImg.raycastTarget = false;

            var ecgRawGo = new GameObject("EcgWaveform", typeof(RectTransform), typeof(RawImage));
            ecgRawGo.transform.SetParent(ecgBox.transform, false);
            SetAnchors(ecgRawGo.GetComponent<RectTransform>(), new Vector2(0.015f, 0.10f), new Vector2(0.50f, 0.90f));
            var ecgRaw = ecgRawGo.GetComponent<RawImage>();
            ecgRaw.raycastTarget = false;

            var ecgStatusLbl = CreateUiText("EcgStatusLabel", ecgBox.transform, new Vector2(0.52f, 0.46f), new Vector2(0.98f, 0.92f), 20f, FontStyles.Bold, new Color(1f, 0.36f, 0.38f, 1f), TextAlignmentOptions.Left);
            var ecgBpmLbl = CreateUiText("EcgBpmLabel", ecgBox.transform, new Vector2(0.52f, 0.08f), new Vector2(0.98f, 0.48f), 25f, FontStyles.Bold, new Color(1f, 0.38f, 0.38f, 1f), TextAlignmentOptions.Left);

            var ecgMonitor = root.AddComponent<RcpCardiacMonitorUI>();
            var ecgSo = new SerializedObject(ecgMonitor);
            ecgSo.FindProperty("ecgRawImage").objectReferenceValue = ecgRaw;
            ecgSo.FindProperty("ecgStatusLabel").objectReferenceValue = ecgStatusLbl;
            ecgSo.FindProperty("ecgBpmLabel").objectReferenceValue = ecgBpmLbl;
            ecgSo.FindProperty("ecgPanelBackground").objectReferenceValue = ecgBgImg;
            ecgSo.FindProperty("metronome").objectReferenceValue = metronome;
            ecgSo.FindProperty("compressionMonitor").objectReferenceValue = compressionMonitor;
            ecgSo.ApplyModifiedPropertiesWithoutUndo();

            var titleText = CreateUiText("StepTitle", root.transform, new Vector2(0.04f, 0.60f), new Vector2(0.96f, 0.70f), 29f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            var subtitleText = CreateUiText("Subtitle", root.transform, new Vector2(0.04f, 0.34f), new Vector2(0.96f, 0.59f), 24f, FontStyles.Normal, new Color(0.92f, 0.96f, 1f, 1f), TextAlignmentOptions.TopLeft);

            // Grupo de elección para paso p3 (sin teclado)
            var choiceGroup = new GameObject("ChoiceGroup", typeof(RectTransform));
            choiceGroup.transform.SetParent(root.transform, false);
            SetAnchors(choiceGroup.GetComponent<RectTransform>(), new Vector2(0.04f, 0.16f), new Vector2(0.96f, 0.32f));
            var (btnNormal, lblNormal) = CreateUiButton("BtnRespiraNormal", choiceGroup.transform, new Vector2(0f, 0f), new Vector2(0.485f, 1f), new Color(0.80f, 0.44f, 0.14f, 1f), "1. Respira con normalidad", 22f);
            var (btnNoRespira, lblNoRespira) = CreateUiButton("BtnNoRespira", choiceGroup.transform, new Vector2(0.515f, 0f), new Vector2(1f, 1f), new Color(0.10f, 0.62f, 0.56f, 1f), "2. No respira / solo jadea", 22f);
            choiceGroup.SetActive(false);

            // Grupo de compresiones (p6 - p8)
            var compGroup = new GameObject("CompressionGroup", typeof(RectTransform));
            compGroup.transform.SetParent(root.transform, false);
            SetAnchors(compGroup.GetComponent<RectTransform>(), new Vector2(0.04f, 0.16f), new Vector2(0.96f, 0.32f));
            var beatRing = CreateUiBox("BeatIndicator", compGroup.transform, new Vector2(0.02f, 0.15f), new Vector2(0.10f, 0.85f), new Color(0.14f, 0.78f, 0.82f, 0.5f)).GetComponent<Image>();
            beatRing.raycastTarget = false;
            var metronomeLabel = CreateUiText("MetronomeLabel", compGroup.transform, new Vector2(0.13f, 0.10f), new Vector2(0.74f, 0.90f), 24f, FontStyles.Bold, new Color(0.55f, 0.98f, 0.82f, 1f), TextAlignmentOptions.Left);
            metronomeLabel.text = "Ritmo: 110 LPM";
            var timerText = CreateUiText("TimerText", compGroup.transform, new Vector2(0.75f, 0.10f), new Vector2(0.98f, 0.90f), 29f, FontStyles.Bold, Color.white, TextAlignmentOptions.Right);
            compGroup.SetActive(false);

            // Barra inferior de controles
            var (btnLang, lblLang) = CreateUiButton("BtnLang", root.transform, new Vector2(0.04f, 0.03f), new Vector2(0.25f, 0.13f), new Color(0.18f, 0.25f, 0.34f, 1f), "Idioma: ES", 18f);
            var (btnRepo, lblRepo) = CreateUiButton("BtnReposition", root.transform, new Vector2(0.27f, 0.03f), new Vector2(0.50f, 0.13f), new Color(0.12f, 0.48f, 0.52f, 1f), "Confirmar aquí", 18f);
            var (btnSkip, lblSkip) = CreateUiButton("BtnSkip", root.transform, new Vector2(0.52f, 0.03f), new Vector2(0.74f, 0.13f), new Color(0.18f, 0.25f, 0.34f, 1f), "Saltar voz", 18f);
            var (btnRestart, lblRestart) = CreateUiButton("BtnRestart", root.transform, new Vector2(0.76f, 0.03f), new Vector2(0.96f, 0.13f), new Color(0.22f, 0.32f, 0.44f, 1f), "Reiniciar", 18f);

            var hud = root.AddComponent<RcpExperienceHUD>();
            var so = new SerializedObject(hud);
            so.FindProperty("headerText").objectReferenceValue = headerText;
            so.FindProperty("stepTitleText").objectReferenceValue = titleText;
            so.FindProperty("subtitleText").objectReferenceValue = subtitleText;
            so.FindProperty("statusBadgeText").objectReferenceValue = badgeText;
            so.FindProperty("accentBar").objectReferenceValue = accent;
            so.FindProperty("beatRingImage").objectReferenceValue = beatRing;
            so.FindProperty("choiceGroup").objectReferenceValue = choiceGroup;
            so.FindProperty("choiceBreathingNormalButton").objectReferenceValue = btnNormal;
            so.FindProperty("choiceNotBreathingButton").objectReferenceValue = btnNoRespira;
            so.FindProperty("choiceNormalLabel").objectReferenceValue = lblNormal;
            so.FindProperty("choiceNotBreathingLabel").objectReferenceValue = lblNoRespira;
            so.FindProperty("compressionGroup").objectReferenceValue = compGroup;
            so.FindProperty("timerText").objectReferenceValue = timerText;
            so.FindProperty("metronomeLabel").objectReferenceValue = metronomeLabel;
            so.FindProperty("languageButton").objectReferenceValue = btnLang;
            so.FindProperty("languageButtonLabel").objectReferenceValue = lblLang;
            so.FindProperty("repositionButton").objectReferenceValue = btnRepo;
            so.FindProperty("repositionButtonLabel").objectReferenceValue = lblRepo;
            so.FindProperty("restartButton").objectReferenceValue = btnRestart;
            so.FindProperty("restartButtonLabel").objectReferenceValue = lblRestart;
            so.FindProperty("skipVoiceButton").objectReferenceValue = btnSkip;
            so.FindProperty("skipVoiceLabel").objectReferenceValue = lblSkip;
            so.FindProperty("placementController").objectReferenceValue = placementCtrl;
            so.FindProperty("metronome").objectReferenceValue = metronome;
            so.FindProperty("cardiacMonitor").objectReferenceValue = ecgMonitor;
            so.ApplyModifiedPropertiesWithoutUndo();
            return hud;
        }

        // ── 4. URP + Quest + Meta ProjectConfig ──────────────────────────────

        private static void ConfigureUrpForQuest()
        {
            string[] urpGuids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            foreach (var guid in urpGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (urp == null) continue;

                urp.supportsHDR = false;
                urp.msaaSampleCount = 4;
                urp.renderScale = 1f;

                var so = new SerializedObject(urp);
                SetBoolIfExists(so, "m_MainLightShadowsSupported", false);
                SetBoolIfExists(so, "m_AdditionalLightShadowsSupported", false);
                SetBoolIfExists(so, "m_AnyShadowsSupported", false);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(urp);
            }

            string[] rendererGuids = AssetDatabase.FindAssets("t:UniversalRendererData");
            foreach (var guid in rendererGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null) continue;

                var so = new SerializedObject(data);
                var inter = so.FindProperty("m_IntermediateTextureMode");
                if (inter != null) inter.enumValueIndex = (int)IntermediateTextureMode.Auto;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(data);
            }
        }

        private static void ConfigurePlayerAndMetaSettings()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "XRLabs";
            PlayerSettings.productName = "Asistente RCP AR";
            PlayerSettings.SetApplicationIdentifier(android, "com.xrlabs.rcpar");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;

#if META_XR_SDK
            var cfg = OVRProjectConfig.CachedProjectConfig;
            if (cfg != null)
            {
                cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
                cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
                OVRProjectConfig.CommitProjectConfig(cfg);
            }
#endif
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void SetSerializedField(UnityEngine.Object target, string propName, object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(propName);
            if (prop == null) return;
            switch (value)
            {
                case string s: prop.stringValue = s; break;
                case bool b: prop.boolValue = b; break;
                case float f: prop.floatValue = f; break;
                case int i: prop.intValue = i; break;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetBoolIfExists(SerializedObject so, string propName, bool value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.boolValue = value;
        }

        private static GameObject CreateUiBox(string name, Transform parent, Vector2 min, Vector2 max, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            SetAnchors(go.GetComponent<RectTransform>(), min, max);
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static TextMeshProUGUI CreateUiText(string name, Transform parent, Vector2 min, Vector2 max,
            float size, FontStyles style, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            SetAnchors(go.GetComponent<RectTransform>(), min, max);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.alignment = align;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static (Button, TextMeshProUGUI) CreateUiButton(string name, Transform parent, Vector2 min, Vector2 max,
            Color bgColor, string label, float fontSize)
        {
            var go = CreateUiBox(name, parent, min, max, bgColor);
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = Color.Lerp(bgColor, Color.white, 0.2f);
            colors.pressedColor = Color.Lerp(bgColor, Color.black, 0.2f);
            btn.colors = colors;

            var tmp = CreateUiText("Label", go.transform, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f),
                fontSize, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            tmp.text = label;
            return (btn, tmp);
        }

        private static void SetAnchors(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
