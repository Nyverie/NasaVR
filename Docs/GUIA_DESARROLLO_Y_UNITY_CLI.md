# Guía de Desarrollo, Flujo con IA y Unity CLI — NasaVR (`AsistenteRCP_AR`)

Este documento explica la arquitectura práctica del proyecto, cómo está implementado y probado el flujo actual de RCP en AR/Passthrough para Meta Quest, y el **workflow exacto para que un desarrollador o un agente de IA trabaje usando Unity CLI + Scene-as-Code + PlayMode Tests**.

---

## 1. Filosofía de trabajo del proyecto ("Scene-as-Code")

El proyecto está estructurado para que **casi toda la escena, UI, materiales, OpenXR y configuración de Quest sean reproducibles por código** desde `Assets/Scripts/RCP/Editor/RcpProjectSetupEditor.cs`.

- **Por qué trabajamos así:** permite que tanto personas como agentes de IA editen C# y `Docs/guion_rcp.json`, regeneren `Assets/Scenes/AsistenteRCP_AR.unity` con un solo comando CLI y validen el resultado con tests automatizados sin depender de clics manuales frágiles en el Inspector.
- **Paquete base (`com.xrlabs.core`):** el paquete `com.xrlabs.core-0.7.0.tgz` (`Packages/manifest.json`) provee `SimulationManager`, `StepSystem`, `VOManager`, `LineLibrary`, `VoiceBaker`, `TouchableHotspot`, `HologramCoach`, `CoachDirector` y `XRLabsRig.prefab`. **No modifiques directamente `Library/PackageCache/com.xrlabs.core@...`**, porque Unity lo trata como paquete inmutable; extiende o adapta todo desde `Assets/Scripts/RCP/`.

---

## 2. Mapa rápido del repositorio

| Ruta | Responsabilidad |
|---|---|
| `Docs/guion_rcp.json` | Fuente de verdad del protocolo RCP (`p0`–`p9`), textos ES/EN, pasos interactivos/tiempo/elección y anclas del Coach. |
| `Docs/ARQUITECTURA_RCP_PASSTHROUGH.md` | Resumen arquitectónico inicial y paquetes XR configurados. |
| `Assets/Scripts/RCP/Editor/RcpProjectSetupEditor.cs` | Constructor completo de escena (`BuildExperienceScene`), reimportador de guion (`ReimportGuionIfPresent`), horneado de voces (`BakeVoicesFromCli`), configuración URP/OpenXR/Meta y build Android (`BuildQuestApkFromCli`). |
| `Assets/Scripts/RCP/RcpRigCoordinator.cs` | Alterna `XRLabsRig` (XR/Manos/Controladores) vs `FlatCamera` (Desktop) y realiza la colocación inicial frente al usuario una sola vez. |
| `Assets/Scripts/RCP/RcpPassthroughSetup.cs` | Configura cámara transparente (`alpha = 0`), apaga post-proceso y configura `OVRManager` + `OVRPassthroughLayer` (Underlay, FloorLevel, manos+mandos simultáneos). |
| `Assets/Scripts/RCP/RcpPatientLocator.cs` | **Paso 0** de ubicación en piso: solo mueve `floorReticle` mientras apuntas, manteniendo `[Colocacion]` y `[RCP_HUD]` quietos hasta confirmar (`Trigger`, pinch o `Confirmar aquí`). |
| `Assets/Scripts/RCP/RcpPlacementController.cs` | Resalta hotspots del paso activo, apaga `Collider` / `XRSimpleInteractable` / `XRInteractableSnapVolume` de los hotspots no activos para no bloquear UI y ubica al Coach junto al paciente. |
| `Assets/Scripts/RCP/RcpExperienceHUD.cs` | Controla `[RCP_HUD]` pegado sobre `[Colocacion]` (título, subtítulos, elección `p3`, cronómetro/metrónomo `p6`–`p8`, idioma ES/EN, bloqueo de `BtnReposition` en `p5`–`p8`). |
| `Assets/Scripts/RCP/RcpCardiacMonitorUI.cs` | Dibuja ECG en vivo en el HUD (`CardiacArrest` rojo → `Compressions` amarillo/verde → `RecoveredPulse` verde sinusal en `p9`). |
| `Assets/Scripts/RCP/RcpCompressionGuideVisual.cs` | Muestra y sincroniza el modelo 3D animado `Assets/Models/CPR-Guide.fbx` sobre el esternón durante `p6`–`p8`. |
| `Assets/Scripts/RCP/RcpCompressionMonitor.cs` | Detecta cercanía de manos (`XRHandSubsystem`) o mandos al pecho y ritmo vertical de compresiones (objetivo ~100–120 LPM, con alertas sonoras y de HUD). |
| `Assets/Scripts/RCP/RcpMetronome.cs` | Genera el pulso sonoro y `BeatPulse` a 110 BPM en `p6`–`p8`. |
| `Assets/Scripts/RCP/RcpHandVisualizer.cs` | Renderiza las 26 articulaciones en Hand Tracking, soporta toque físico directo / pinch sin mandos y silencia el láser + rayo UI al acercar manos al pecho. |
| `Assets/Scripts/RCP/RcpHandInputHelper.cs` | Utilidad compartida para leer flanco de subida de gatillo o pellizco (`XRHandSubsystem`). |
| `Assets/Scripts/RCP/Tests/RcpExperiencePlayModeTests.cs` | Suite de PlayMode tests que valida flujo `p0`–`p9`, 40 clips ES/EN, cámara única en `p6`, colliders en `p0`/`p3`, bloqueo de `BtnReposition` y modos Flat/XR. |
| `Assets/Models/CPR-Guide.fbx` | Modelo 3D animado de manos realizando RCP (importado sin cámaras ni luces embebidas). |
| `Assets/Textures/CoachEnfermera.jpg` | Retrato usado por `HologramCoach` ("Enfermera Clara"). |
| `Assets/Experiencias/rcp-asistente-emergencia-demo/` | Assets generados por `GuionImporter` (`Pasos/`, `Secuencias/`, `Frases/`) y audios `.mp3` en `Voces/es/` y `Voces/en/`. |

---

## 3. Flujo actual implementado (`Paso 0` + `p0`–`p9`)

1. **Paso 0 (`RcpPatientLocator`):** Al iniciar aparece la silueta y el panel `[RCP_HUD]` con el mensaje para ubicar al paciente en el piso (`floorReticle` sigue la mirada). Se confirma con Trigger, pellizco (pinch) o `Confirmar aquí`.
2. **`p0` (narración microgravedad):** Recuerda sujetar/amarrar al paciente y a ti mismo al piso antes de empezar.
3. **`p1` (narración):** Bienvenida y seguridad del entorno.
4. **`p2` (tocar `Objeto_hombros`):** Sacudir hombros y verificar respuesta.
5. **`p3` (elegir en HUD):** "¿Respira con normalidad?" — Opción 1 (error) o Opción 2 (correcta, avanza a `p4`). `Objeto_pecho` permanece desactivado en `p0` y `p3`.
6. **`p4` (tocar `Objeto_confirmar`):** Baliza `911 • LLAMAR`.
7. **`p5` (tocar `Objeto_pecho`):** Fijar centro del pecho (aquí `BtnReposition` queda bloqueado para evitar reubicaciones accidentales).
8. **`p6`–`p8` (tiempo 30s / 25s / 40s):** Compresiones a 110 BPM con metrónomo, manos guía 3D (`CPR-Guide.fbx`), validación de distancia/ritmo y monitor ECG en amarillo/verde; láser y rayo UI del `NearFarInteractor` se silencian al acercar las manos al pecho.
9. **`p9` (narración final):** Cierre, oculta manos guía 3D y ECG pasa a ritmo sinusal verde recuperado.

---

## 4. Cómo trabajar con Unity CLI (para desarrolladores e IA)

Versión exacta requerida del Editor: **`6000.3.14f1`** (`ProjectSettings/ProjectVersion.txt`).

- **Ruta del binario en macOS:**
  `/Applications/Unity/Hub/Editor/6000.3.14f1/Unity.app/Contents/MacOS/Unity`
- **Ruta del binario en Windows:**
  `"C:\Program Files\Unity\Hub\Editor\6000.3.14f1\Editor\Unity.exe"`

### Regla crítica antes de lanzar Unity CLI
Unity **no permite dos procesos sobre el mismo proyecto**. Si el Editor gráfico está abierto, ciérralo primero y asegúrate de que no quede un `Temp/UnityLockfile` huérfano antes de correr `-batchmode`:

```bash
# macOS: cerrar editor si estaba abierto
pkill -f "Unity.*NasaVR" || true
rm -f Temp/UnityLockfile
```

---

### Comando 1: Reimportar guion, reconstruir escena y configurar Quest (`SetupProjectFromCli`)

Úsalo siempre que modifiques `RcpProjectSetupEditor.cs`, `Docs/guion_rcp.json`, materiales, FBX o configuraciones OpenXR/URP:

```bash
"/Applications/Unity/Hub/Editor/6000.3.14f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -quit \
  -projectPath "/ruta/al/repo/NasaVR" \
  -executeMethod NasaVR.RCP.Editor.RcpProjectSetupEditor.SetupProjectFromCli \
  -logFile /tmp/nasavr_setup.log
```

*(En Windows cambia solo la ruta de `Unity.exe`, la ruta de `-projectPath` y usa `-logFile nasavr_setup.log`.)*

Qué hace exactamente `SetupProjectFromCli`:
1. Reimporta `Docs/guion_rcp.json` con `GuionImporter.Import(doc)` conservando los `.mp3` ya horneados en `Voces/`.
2. Mantiene `VoiceSettings` de ElevenLabs si ya existen clips `.mp3` (o usa `MacSayProvider` en macOS si no hay ElevenLabs).
3. Reconstruye desde cero `Assets/Scenes/AsistenteRCP_AR.unity` y la deja como índice `0` en Build Settings.
4. Reimporta `Assets/Models/CPR-Guide.fbx` con `importCameras = false` e `importLights = false`.
5. Ajusta URP móvil, PlayerSettings Android (IL2CPP, ARM64, Vulkan, API >= 32) y `VisorOpenXr.Configure(VisorTarget.Quest3)`.

---

### Comando 2: Hornear nuevas líneas de voz con ElevenLabs (`BakeVoicesFromCli`)

Si agregaste o cambiaste texto en `Docs/guion_rcp.json`:

1. Pasa la clave **únicamente como variable de entorno** (`ELEVENLABS_API_KEY`). **Nunca la guardes en código, JSON, `.md` ni `.asset`.**
2. **NO uses `-quit`** con `BakeVoicesFromCli`, porque el horneado HTTP es asíncrono y bombea `EditorApplication.update` hasta llamar `EditorApplication.Exit(...)` al terminar.

```bash
ELEVENLABS_API_KEY="tu_api_key" \
"/Applications/Unity/Hub/Editor/6000.3.14f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode \
  -projectPath "/ruta/al/repo/NasaVR" \
  -executeMethod NasaVR.RCP.Editor.RcpProjectSetupEditor.BakeVoicesFromCli \
  -logFile /tmp/nasavr_bake.log
```

Comportamiento:
- `BakeAsync` usa `Force = false`: **salta los clips cuyo hash no cambió** y solo genera en ElevenLabs las líneas nuevas o editadas (`es` y `en`).
- Verifica en el log una línea similar a:
  `[NasaVR RCP] Horneado ElevenLabs: Voz: X generadas, Y saltadas, 0 fallidas...`
- Después vuelve a ejecutar `SetupProjectFromCli` para asegurar referencias limpias en la escena si cambiaste estructura del guion.

---

### Comando 3: Ejecutar PlayMode Tests desde CLI

> **Trampa importante:** **NUNCA pases `-quit`** junto a `-runTests`. Unity cerraría el proceso antes de que terminen los tests PlayMode.

```bash
rm -f /tmp/nasavr_test_results.xml
"/Applications/Unity/Hub/Editor/6000.3.14f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode \
  -projectPath "/ruta/al/repo/NasaVR" \
  -runTests \
  -testPlatform PlayMode \
  -assemblyNames "NasaVR.RCP.Tests" \
  -testResults /tmp/nasavr_test_results.xml \
  -logFile /tmp/nasavr_tests.log
```

Al finalizar:
- Código de salida `0` = todos pasaron; `2` = algún test falló.
- Revisa el XML resultante (`/tmp/nasavr_test_results.xml`) buscando `result="Passed"` y `failed="0"`.

---

### Comando 4: Compilar APK para Meta Quest (Windows recomendado)

Desde CLI:
```bat
"C:\Program Files\Unity\Hub\Editor\6000.3.14f1\Editor\Unity.exe" ^
  -batchmode -quit ^
  -projectPath "C:\ruta\al\repo\NasaVR" ^
  -executeMethod NasaVR.RCP.Editor.RcpProjectSetupEditor.BuildQuestApkFromCli ^
  -logFile nasavr_build.log
```

O desde Unity Editor: menú **`Tools -> NasaVR RCP -> 2. Compilar APK para Meta Quest`**.
El APK se genera en `Builds/Android/AsistenteRCP_AR.apk` (carpeta ignorada o fuera de código fuente según `.gitignore`).

Para instalar directo en un Quest conectado por USB (modo desarrollador):
```bash
adb install -r Builds/Android/AsistenteRCP_AR.apk
```

---

## 5. Ciclo de trabajo recomendado (humano o IA)

1. **Editar** scripts en `Assets/Scripts/RCP/` o guion en `Docs/guion_rcp.json`.
2. **Cerrar** Unity GUI si está abierta.
3. Si hubo cambios en `guion_rcp.json`, correr `BakeVoicesFromCli` con `ELEVENLABS_API_KEY`.
4. Correr `SetupProjectFromCli` para regenerar la escena sin intervención manual y verificar cero errores de compilación.
5. Correr `-runTests -testPlatform PlayMode -assemblyNames "NasaVR.RCP.Tests"` (agregando un test nuevo en `RcpExperiencePlayModeTests.cs` por cada bug corregido o feature añadida).
6. **Limpiar artefactos transitorios antes de hacer `git commit`** (ver §6).
7. Hacer `git commit` + `git push`, y en la máquina con visor Quest conectado hacer `git pull` + compilar APK.

---

## 6. Limpieza obligatoria antes de `git commit`

1. **Verificar cero API keys en el repo:**
   ```bash
   git grep -n "sk_" Assets Docs Packages ProjectSettings || echo "CLEAN"
   ```
2. **Revertir archivos transitorios que Unity Test Runner / TMP ensucian al correr tests:**
   Al ejecutar `-runTests`, Unity suele modificar/borrar temporalmente:
   - `Assets/Resources/PerformanceTestRunInfo.json` (+ `.meta`)
   - `Assets/Resources/PerformanceTestRunSettings.json` (+ `.meta`)
   - `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset`
   - `Assets/StreamingAssets` (+ `.meta`)

   Límpialos antes de hacer `git add`:
   ```bash
   git checkout -- \
     Assets/Resources/PerformanceTestRunInfo.json \
     Assets/Resources/PerformanceTestRunInfo.json.meta \
     Assets/Resources/PerformanceTestRunSettings.json \
     Assets/Resources/PerformanceTestRunSettings.json.meta \
     "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset"
   rm -rf Assets/StreamingAssets Assets/StreamingAssets.meta
   ```

---

## 7. Trampas ya diagnosticadas y resueltas (evitar regresiones)

1. **Cámaras y luces ocultas dentro de `.fbx` de Blender (`CPR-Guide.fbx`):**
   Un `.fbx` exportado desde Blender suele traer `Camera` y `Light` por defecto. Cuando ese prefab se activaba al entrar en `p6`, esa cámara adicional tomaba render sobre la vista XR y parecía "pegar" la escena a la cámara.
   - **Protección actual:** `RcpProjectSetupEditor.CreateCompressionHandsGuide()` fuerza `importer.importCameras = false` e `importer.importLights = false`, `RcpCompressionGuideVisual` deshabilita cualquier `Camera`/`Light` en runtime y `RcpExperiencePlayModeTests` valida que en `p6` solo exista **1 cámara activa** y **0 cámaras/luces dentro de `GuiaManosRCP`**.
2. **`TouchableHotspot` crea `XRInteractableSnapVolume` en runtime:**
   `TouchableHotspot.Start()` genera una esfera trigger llamada `SnapVolume` (hasta `0.35 m`) cuando `XRSettings.isDeviceActive` es true. Si queda habilitada en pasos de UI (`p3`), el láser queda atrapado por el torso en vez de apuntar a los botones.
   - **Protección actual:** `RcpPlacementController` desactiva `Collider`, `XRSimpleInteractable` y `XRInteractableSnapVolume` de todos los hotspots no requeridos en el paso actual.
3. **Toques accidentales en `BtnReposition` al acercar manos:**
   El botón `Reubicar` está en la franja inferior del HUD sobre el pecho.
   - **Protección actual:** `[Colocacion]` y `[RCP_HUD]` ya no persiguen la cámara en Paso 0 (solo se mueve `floorReticle`), `BtnReposition` se bloquea (`interactable = false`) en `p5`–`p8`, se excluye de poke físico directo en `RcpHandVisualizer`, y `NearFarInteractor` apaga láser + `enableUIInteraction` cerca del pecho durante compresiones.
4. **No usar `Resources.FindObjectsOfTypeAll` para suponer elementos activos ni tocar `com.xrlabs.core`:**
   `StepSystem` y `HologramCoach` son configurados por escena desde `RcpProjectSetupEditor.cs`. Mantén las personalizaciones del flujo en `Assets/Scripts/RCP/`.
