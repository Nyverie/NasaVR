# Arquitectura: Asistente de RCP en AR (Passthrough) — Meta Quest + XRLabs Core

## 1. Objetivo y alcance

Prototipo autónomo de Realidad Mixta (Passthrough) para **Meta Quest 3 / 3S**, **sin login y sin menú inicial**, que guía al usuario en tiempo real paso a paso en la atención de RCP (`Docs/guion_rcp.json`, esquema `xrlabs.guion/0.1`).

La app arranca directamente en la escena `AsistenteRCP_AR.unity` (índice `0` en Build Settings).

---

## 2. Stack técnico

| Capa | Paquete / Tecnología | Rol |
|---|---|---|
| Motor | Unity `6000.3.14f1` (URP 17.3.0, Android ARM64, Vulkan, IL2CPP) | Render móvil optimizado para Quest |
| Runtime XR | `com.unity.xr.openxr` + `com.unity.xr.meta-openxr` | Backend OpenXR para Meta Quest |
| Meta Passthrough & Quest | `com.meta.xr.sdk.core` (`OVRManager`, `OVRPassthroughLayer`) | Passthrough Underlay, Hand Tracking, Foveated Rendering |
| Interacciones VR/AR | `com.unity.xr.interaction.toolkit` + `com.unity.xr.hands` | Interacción por manos (Pinch/Poke) y controladores |
| Sistema de pasos y Coach | `com.xrlabs.core` (`0.7.0`) + `com.cysharp.unitask` (`2.3.3`) | `SimulationManager`, `StepSystem`, `VOManager`, `HintSystem`, `LineLibrary`, `TouchableHotspot`, `SubtitlePanel` |

### Paquetes removidos respecto al template inicial
- `com.unity.xr.androidxr-openxr` (Android XR / Galaxy XR, innecesario para Quest)
- `com.unity.learn.iet-framework` (tutoriales del template)
- `com.unity.multiplayer.center` (multijugador del template)

---

## 3. Flujo de la experiencia (`guion_rcp.json`)

1. **`p1` (narración):** Bienvenida y verificación de seguridad del entorno.
2. **`p2` (tocar `hombros`):** Sacudir hombros y preguntar "¿me oyes?" → tocar hotspot `Objeto_hombros`.
3. **`p3` (elegir):** Comprobar respiración (≤10s). Panel AR espacial con dos botones:
   - **"Respira con normalidad" (opción 1 / error):** Dispara `KeyChoiceStepCondition.SimulateError()`, reproduce `p3.err.respira` y repite instrucción.
   - **"No respira / solo jadea" (opción 2 / correcta):** Dispara `KeyChoiceStepCondition.SimulateCorrect()` y avanza.
4. **`p4` (tocar `confirmar`):** Llamar a emergencias y tocar botón espacial **Hecho** (`Objeto_confirmar`).
5. **`p5` (tocar `pecho`):** Fijar punto de compresión en el centro del pecho (`Objeto_pecho`).
6. **`p6` (tiempo 30s):** Compresiones al ritmo del **metrónomo (110 BPM)**.
7. **`p7` (tiempo 25s):** Instrucciones de uso de DEA mientras se mantienen compresiones.
8. **`p8` (tiempo 40s):** Continuidad y relevo cada 2 minutos.
9. **`p9` (narración):** Cierre y recordatorio de informar hora de inicio a emergencias.

---

## 4. Estructura de la escena `AsistenteRCP_AR.unity`

- **`[Platform]` / `XRLabsRig` + Meta Passthrough:**
  - Rig XR (`XROrigin`) con `Main Camera` (`ClearFlags = SolidColor`, `Background = (0,0,0,0)`).
  - `OVRManager` (`isInsightPassthroughEnabled = true`) + `OVRPassthroughLayer` (`overlayType = Underlay`).
  - `FoveatedRenderingBootstrap` (High / Dynamic).
  - En modo Editor (sin headset), `PlatformBootstrapper` habilita la cámara plana con `FlatInteractionDriver` + `OrbitCameraController` para probar con mouse y teclas `1`/`2`.
- **`[Simulation]`:**
  - `SimulationManager` (modo `Guided`), `StepSystem`, `VOManager`, `HintSystem`, `FeedbackSystem`, `ScoringSystem`, `TelemetryClient`.
  - Secuencia `Assets/Experiencias/rcp-asistente-emergencia-demo/Secuencias/rcp-asistente-emergencia-demo.asset`.
  - Biblioteca `Assets/Experiencias/rcp-asistente-emergencia-demo/Frases/rcp-asistente-emergencia-demo.asset`.
- **`[Colocacion]`:**
  - Grupo movible/anclable sobre el maniquí o paciente real con `Objeto_hombros`, `Objeto_pecho`, `Objeto_confirmar`, `Ancla_paciente` y botón/manija para reposicionar todo el conjunto en el mundo real.
- **`[RCP_Extras]` (componentes específicos del prototipo):**
  - `RcpExperienceHUD`: Panel espacial flotante (subtítulos, paso actual, botones de elección para `p3`, indicador visual de ritmo y barra de tiempo para `p6`–`p8`, botón **Hecho** para `p4`, cambio de idioma ES/EN, reiniciar y botón de recolocar).
  - `RcpMetronome`: Generador/reproductor de pulso de audio a 110 BPM activo durante `p6`, `p7`, `p8`.
  - `RcpPlacementController`: Permite colocar/ajustar la posición del grupo `[Colocacion]` frente al usuario o arrastrarlo con la mano/controlador.

---

## 5. Audio y voz (TTS)

- `VOManager` resuelve las líneas de `LineLibrary` en `es` o `en`.
- Las voces de `p1`..`p9` y `p3.err.respira` se generan en `.mp3` con **ElevenLabs** (`eleven_multilingual_v2`) dentro de `Assets/Experiencias/rcp-asistente-emergencia-demo/Voces/`, viajando ya importadas en el proyecto (sin guardar API keys en archivos del repositorio).
- Si falta algún clip, `VOManager` estima la duración automáticamente y muestra el texto en el HUD/subtítulos.

---

## 6. Build y despliegue en Meta Quest (Windows / CLI / `adb`)

1. Escena principal: `Assets/Scenes/AsistenteRCP_AR.unity` en índice `0` de Build Settings (sin login ni menú).
2. URP configurado para Quest (HDR Off, MSAA 4x, Sombras Off, Intermediate Texture Auto).
3. OpenXR Android configurado para Meta Quest (`Meta Quest Support`, `Meta XR Feature`, `Hand Tracking Subsystem`, `Meta Hand Tracking Aim`, `Foveated Rendering`, `Meta Quest: Camera (Passthrough)`).
4. Para probar/compilar en Windows: abrir el proyecto en Unity `6000.3.14f1`, cambiar/confirmar plataforma **Android**, usar `Tools → NasaVR RCP → 2. Compilar APK para Meta Quest` (o `Build Settings → Build And Run`) hacia el visor conectado en modo desarrollador.
