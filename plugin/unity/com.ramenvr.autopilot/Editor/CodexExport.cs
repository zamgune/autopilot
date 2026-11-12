// The project as readable text for the brain's codex (Library/AutopilotCodex/): what Unity keeps in binary-ish YAML with
// GUIDs and file IDs, written out with names. Run in the editor (before play mode, when it is needed), or headless:
//
//   Unity.exe -batchmode -nographics -quit -projectPath <project>
//       -executeMethod Autopilot.Playtest.Editor.CodexExport.Run -out <project>/Library/AutopilotCodex
//
// Files:
//   summary.txt          the project at a glance (written last: the export is complete when it is there)
//   build_scenes.txt     the scenes in the build, in order
//   scripts.txt          each script of the project: its class, base class and file
//   scenes/<path>.txt    each scene's hierarchy: objects (with the bridge's paths), components and their serialized
//                        values, references by name and path, UnityEvent listeners (what a button calls)
//   prefabs/<path>.txt   each prefab, the same way
//   data/<path>.txt      ScriptableObject assets and their values
//   input/<path>.txt     Input System action maps: actions, types, bindings, composites, control schemes; and the
//                        legacy Input Manager's axes
// In the editor's window the export goes in steps across the editor's updates (Start): the Live page counts the files
// (Indexing files (n/total)) and Stop ends it (no summary.txt then: it is done again next time).
// Values are Unity's own: x right, y up, z forward, metres.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Autopilot.Playtest.Editor
{
    public static class CodexExport
    {
        const int MaxFileChars = 2 * 1024 * 1024;
        const int MaxArray = 32;
        const int MaxDepth = 4;

        /// <summary>-executeMethod Autopilot.Playtest.Editor.CodexExport.Run -out &lt;dir&gt;</summary>
        public static void Run()
        {
            var dir = PlaytestClient.Arg("out");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "AutopilotCodex");
            try
            {
                Export(dir);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[Autopilot] The project index export failed: " + e);
                EditorApplication.Exit(1);
            }
        }

        public static string Needed(string project)
        {
            return Codex.ExportNeeded(project);
        }

        /// <summary>The export, whole, before it returns (the command line, a player's request, tests).</summary>
        public static void Export(string dir)
        {
            using (var steps = Steps(dir, new Job()))
            {
                while (steps.MoveNext())
                {
                }
            }
        }

        /// <summary>An export going on across the editor's updates (Start): how far it is, and how it ended.</summary>
        public sealed class Job
        {
            /// <summary>Files written (a scene, a prefab, a data asset; the input; the build list and scripts) and how
            /// many in all (0 until they are counted).</summary>
            public int Done, Total;
            /// <summary>Stop ended it (no summary.txt was written).</summary>
            public bool Stopped;
            public bool Finished;
            public Exception Error;
            internal IEnumerator<bool> Steps;
            internal Action<Job> Then;
        }

        static Job running;

        /// <summary>The export going on in steps, if any.</summary>
        public static Job Running => running != null && !running.Finished ? running : null;

        /// <summary>How far the export going on is (the Live page's "Indexing files (done/total)"); false when none is
        /// going.</summary>
        public static bool Progress(out int done, out int total)
        {
            var job = Running;
            done = job?.Done ?? 0;
            total = job?.Total ?? 0;
            return job != null;
        }

        /// <summary>The export going on stopped (Stop pressed while it runs): it ends before its next file, writing no
        /// summary.txt (so it is done again next time), and its continuation is called with Stopped.</summary>
        public static void Stop()
        {
            var job = Running;
            if (job != null) job.Stopped = true;
        }

        /// <summary>Seconds of an editor update the export's steps may use before it lets the editor go on (the window
        /// repaints its count between them).</summary>
        const double SliceSeconds = 0.05;

        /// <summary>The export into <paramref name="dir"/> in steps across the editor's updates (the editor stays
        /// responsive and the window shows how far it is); <paramref name="then"/> once it is over (finished, failed:
        /// Error, or stopped: Stopped), on the main thread. One export at a time: while one runs, this one's
        /// continuation waits for that one's end.</summary>
        public static Job Start(string dir, Action<Job> then)
        {
            var going = Running;
            if (going != null)
            {
                var prior = going.Then;
                going.Then = j =>
                {
                    prior?.Invoke(j);
                    then?.Invoke(j);
                };
                return going;
            }
            var job = new Job { Then = then };
            job.Steps = Steps(dir, job);
            running = job;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return job;
        }

        static void Tick()
        {
            var job = running;
            if (job == null || job.Finished)
            {
                EditorApplication.update -= Tick;
                return;
            }
            var until = EditorApplication.timeSinceStartup + SliceSeconds;
            try
            {
                while (!job.Stopped && EditorApplication.timeSinceStartup < until)
                {
                    if (!job.Steps.MoveNext())
                    {
                        Finish(job);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                job.Error = e;
                Finish(job);
                return;
            }
            if (job.Stopped) Finish(job);
        }

        static void Finish(Job job)
        {
            EditorApplication.update -= Tick;
            job.Finished = true;
            try
            {
                job.Steps.Dispose();  // (a stopped one closes the scene it had open: its finally)
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] The project index export didn't close cleanly: " + e.Message);
            }
            if (running == job) running = null;
            if (job.Error != null) Debug.LogWarning("[Autopilot] The project index export failed: " + job.Error.Message);
            try
            {
                job.Then?.Invoke(job);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Autopilot] After the project index export: " + e);
            }
        }

        /// <summary>The export, a file at a time: each step writes one (a scene, a prefab, a data asset; the input; the
        /// build list and scripts) and counts it in job.Done; summary.txt is written last.</summary>
        static IEnumerator<bool> Steps(string dir, Job job)
        {
            var started = DateTime.Now;
            Directory.CreateDirectory(dir);
            var summaryPath = Path.Combine(dir, "summary.txt");
            if (File.Exists(summaryPath)) File.Delete(summaryPath);
            var counts = new Dictionary<string, int>();
            var playing = EditorApplication.isPlayingOrWillChangePlaymode;

            // What there is to write, counted first (the count the window shows)
            var scenePaths = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var dataPaths = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .Where(p => !p.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            job.Total = scenePaths.Count + prefabPaths.Count + dataPaths.Count + 2;  // (and the input; the build list and scripts)
            yield return true;

            // Scenes: opened one by one in edit mode; in play mode only those loaded (live), the others kept as they were
            var scenesDir = Path.Combine(dir, "scenes");
            if (!playing && Directory.Exists(scenesDir)) Directory.Delete(scenesDir, true);
            var n = 0;
            foreach (var path in scenePaths)
            {
                ++job.Done;
                var scene = SceneManager.GetSceneByPath(path);
                var opened = false;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    if (playing) continue;
                    try
                    {
                        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                        opened = true;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Autopilot] Couldn't open {path} for the export: {e.Message}");
                        continue;
                    }
                }
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"# Scene {path}" + (BuildIndex(path) >= 0 ? $" (build index {BuildIndex(path)})" : " (not in the build)") + (playing ? " - live, as play mode has it now" : ""));
                    sb.AppendLine("# Objects by the bridge's paths; values in Unity's frame (x right, y up, z forward, metres).");
                    sb.AppendLine();
                    var roots = new List<GameObject>();
                    scene.GetRootGameObjects(roots);
                    foreach (var root in roots)
                        DumpObject(sb, root, 0, go => Paths.Of(go));
                    Write(scenesDir, Strip(path) + ".txt", sb);
                    ++n;
                }
                finally
                {
                    if (opened) EditorSceneManager.CloseScene(scene, true);
                }
                yield return true;
            }
            counts["scenes"] = n;

            // Prefabs
            var prefabsDir = Path.Combine(dir, "prefabs");
            if (Directory.Exists(prefabsDir)) Directory.Delete(prefabsDir, true);
            n = 0;
            foreach (var path in prefabPaths)
            {
                ++job.Done;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var sb = new StringBuilder();
                sb.AppendLine($"# Prefab {path}");
                sb.AppendLine("# Objects by their path in the prefab; values in Unity's frame (x right, y up, z forward, metres).");
                sb.AppendLine();
                DumpObject(sb, prefab, 0, PrefabPath);
                Write(prefabsDir, Strip(path) + ".txt", sb);
                ++n;
                yield return true;
            }
            counts["prefabs"] = n;

            // ScriptableObject data
            var dataDir = Path.Combine(dir, "data");
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            n = 0;
            foreach (var path in dataPaths)
            {
                ++job.Done;
                var assets = AssetDatabase.LoadAllAssetsAtPath(path).OfType<ScriptableObject>().Where(a => a != null && Reflect.IsGameType(a.GetType())).ToList();
                if (assets.Count == 0) continue;
                var sb = new StringBuilder();
                sb.AppendLine($"# Data {path}");
                foreach (var a in assets)
                {
                    sb.AppendLine();
                    sb.AppendLine($"{a.name} ({a.GetType().Name}{ScriptOf(a)})");
                    DumpFields(sb, a, 1);
                }
                Write(dataDir, Strip(path) + ".txt", sb);
                ++n;
                yield return true;
            }
            counts["data"] = n;

            // Input
            var inputDir = Path.Combine(dir, "input");
            if (Directory.Exists(inputDir)) Directory.Delete(inputDir, true);
            counts["input"] = ExportInput(inputDir);
            ++job.Done;
            yield return true;

            // Build scenes, scripts
            var build = new StringBuilder();
            build.AppendLine("# Scenes in the build (index: path, enabled)");
            var i = 0;
            foreach (var s in EditorBuildSettings.scenes)
                build.AppendLine($"{(s.enabled ? (i++).ToString() : "-")}: {s.path}{(s.enabled ? "" : " (disabled)")}");
            File.WriteAllText(Path.Combine(dir, "build_scenes.txt"), build.ToString(), new UTF8Encoding(false));
            var scripts = new StringBuilder();
            scripts.AppendLine("# The project's scripts: class : base class  (file)");
            var nScripts = 0;
            foreach (var path in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                var type = script != null ? script.GetClass() : null;
                scripts.AppendLine(type != null ? $"{type.FullName} : {type.BaseType?.Name}  ({path})" : $"(no class)  ({path})");
                ++nScripts;
            }
            File.WriteAllText(Path.Combine(dir, "scripts.txt"), scripts.ToString(), new UTF8Encoding(false));
            ++job.Done;

            // The summary, last
            var summary = new StringBuilder();
            summary.AppendLine($"# {PlayerSettings.productName}: the Unity project at a glance (Autopilot codex export)");
            summary.AppendLine();
            summary.AppendLine($"product: {PlayerSettings.productName} by {PlayerSettings.companyName}");
            summary.AppendLine($"productGUID: {PlayerSettings.productGUID}");
            summary.AppendLine($"unity: {Application.unityVersion}");
            summary.AppendLine($"render pipeline: {(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline.name : "built-in")}");
            var input = Hands.Capabilities();
            summary.AppendLine($"input handling: {input["handling"]} (Input System package {(Hands.InputSystemCompiled ? "on" : "off")}, legacy Input Manager {(Hands.LegacyInputEnabled ? "on" : "off")})");
            foreach (var note in (List<object>)input["notes"]) summary.AppendLine("  note: " + note);
            summary.AppendLine($"scripts: {nScripts} (scripts.txt)");
            summary.AppendLine($"scenes: {counts["scenes"]} exported (scenes/), build list in build_scenes.txt");
            summary.AppendLine($"prefabs: {counts["prefabs"]} (prefabs/)");
            summary.AppendLine($"data assets: {counts["data"]} (data/)");
            summary.AppendLine($"input assets: {counts["input"]} (input/)");
            summary.AppendLine();
            summary.AppendLine("Paths: scene objects as the bridge names them: <scene>:/<root>/<child> (Gem@1 for the second of that name),");
            summary.AppendLine("components as <object path>|<Type>. Values in Unity's frame: x right, y up, z forward, metres;");
            summary.AppendLine("eulerAngles in degrees (y turns right, x tilts down).");
            summary.AppendLine($"exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss} in {(DateTime.Now - started).TotalSeconds:0.0} s{(playing ? " (in play mode: scenes not loaded kept from the last export)" : "")}");
            File.WriteAllText(summaryPath, summary.ToString(), new UTF8Encoding(false));
        }

        static int BuildIndex(string path)
        {
            var i = 0;
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path == path) return i;
                ++i;
            }
            return -1;
        }

        static string Strip(string assetPath)
        {
            return assetPath.StartsWith("Assets/", StringComparison.Ordinal) ? assetPath.Substring(7) : assetPath;
        }

        static void Write(string dir, string rel, StringBuilder sb)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var text = sb.Length > MaxFileChars ? sb.ToString(0, MaxFileChars) + "\n... (cut: the file was too long)\n" : sb.ToString();
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        static string PrefabPath(GameObject go)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Add(Paths.Escape(t.name));
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string V(Vector3 v)
        {
            return $"({R(v.x)}, {R(v.y)}, {R(v.z)})";
        }

        static string R(float f)
        {
            return Math.Round(f, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static string ScriptOf(Object o)
        {
            if (o is MonoBehaviour mb)
            {
                var script = MonoScript.FromMonoBehaviour(mb);
                var path = script != null ? AssetDatabase.GetAssetPath(script) : "";
                return string.IsNullOrEmpty(path) ? "" : ", " + path;
            }
            if (o is ScriptableObject so)
            {
                var script = MonoScript.FromScriptableObject(so);
                var path = script != null ? AssetDatabase.GetAssetPath(script) : "";
                return string.IsNullOrEmpty(path) ? "" : ", " + path;
            }
            return "";
        }

        static void DumpObject(StringBuilder sb, GameObject go, int depth, Func<GameObject, string> pathOf)
        {
            if (sb.Length > MaxFileChars) return;
            var pad = new string(' ', depth * 2);
            var t = go.transform;
            var flags = new List<string>();
            if (!go.activeSelf) flags.Add("inactive");
            if (go.tag != "Untagged") flags.Add("tag " + go.tag);
            if (go.layer != 0) flags.Add("layer " + LayerMask.LayerToName(go.layer));
            if (go.isStatic) flags.Add("static");
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                var source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(source)) flags.Add("prefab " + source);
            }
            sb.Append(pad).Append(pathOf(go));
            if (flags.Count > 0) sb.Append("  [").Append(string.Join(", ", flags)).Append(']');
            sb.AppendLine();
            sb.Append(pad).Append("  position ").Append(V(t.position)).Append("  euler ").Append(V(t.eulerAngles));
            if (t.localScale != Vector3.one) sb.Append("  scale ").Append(V(t.localScale));
            if (t is RectTransform rt) sb.Append("  rect ").Append(R(rt.rect.width)).Append('x').Append(R(rt.rect.height));
            sb.AppendLine();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    sb.Append(pad).AppendLine("  (missing script)");
                    continue;
                }
                if (c is Transform) continue;
                sb.Append(pad).Append("  ").Append(c.GetType().Name);
                if (c is Behaviour b && !b.enabled) sb.Append(" (disabled)");
                sb.Append(ScriptOf(c).Length > 0 ? " (" + ScriptOf(c).Substring(2) + ")" : "");
                sb.AppendLine(":");
                DumpFields(sb, c, depth + 2);
            }
            for (var i = 0; i < t.childCount; ++i)
                DumpObject(sb, t.GetChild(i).gameObject, depth + 1, pathOf);
        }

        static readonly HashSet<string> Noise = new HashSet<string>
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject", "m_Script",
            "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Name", "m_Enabled", "m_LightProbeUsage", "m_ReflectionProbeUsage",
            "m_RayTracingMode", "m_RayTraceProcedural", "m_RenderingLayerMask", "m_RendererPriority", "m_StaticBatchInfo",
            "m_StaticBatchRoot", "m_ProbeAnchor", "m_LightProbeVolumeOverride", "m_ScaleInLightmap", "m_ReceiveGI",
            "m_PreserveUVs", "m_IgnoreNormalsForChartDetection", "m_ImportantGI", "m_StitchLightmapSeams", "m_SelectedEditorRenderState",
            "m_MinimumChartSize", "m_AutoUVMaxDistance", "m_AutoUVMaxAngle", "m_LightmapParameters", "m_SortingLayerID",
            "m_SortingLayer", "m_SortingOrder", "m_AdditionalVertexStreams", "m_EnlightenVertexStream", "m_LightmapIndex",
            "m_LightmapIndexDynamic", "m_LightmapTilingOffset", "m_LightmapTilingOffsetDynamic", "m_DynamicOccludee",
            "m_StaticShadowCaster", "m_MotionVectors", "m_SmallMeshCulling", "m_ForceMeshLod", "m_MeshLodSelectionBias",
            "m_GlobalIlluminationMeshLod", "m_MaskInteraction", "m_ProvidesContacts", "m_LayerOverridePriority",
            "m_IncludeLayers", "m_ExcludeLayers", "m_CookingOptions", "m_CullTransparentMesh", "m_RaycastPadding",
        };

        [ThreadStatic] static bool builtin;

        static void DumpFields(StringBuilder sb, Object o, int depth)
        {
            builtin = !Reflect.IsGameType(o.GetType());
            SerializedObject so;
            try
            {
                so = new SerializedObject(o);
            }
            catch
            {
                return;
            }
            var pad = new string(' ', depth * 2);
            var it = so.GetIterator();
            var lines = 0;
            if (!it.NextVisible(true)) return;
            do
            {
                if (Noise.Contains(it.name)) continue;
                if (++lines > 80)
                {
                    sb.Append(pad).AppendLine("...");
                    break;
                }
                Value(sb, it, pad, 0);
            } while (it.NextVisible(false));
        }

        static string Label(SerializedProperty p)
        {
            var n = p.name;
            return n.StartsWith("m_", StringComparison.Ordinal) && n.Length > 2 ? char.ToLowerInvariant(n[2]) + n.Substring(3) : n;
        }

        static void Value(StringBuilder sb, SerializedProperty p, string pad, int depth)
        {
            var label = Label(p);
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(p.longValue.ToString());
                    return;
                case SerializedPropertyType.Boolean:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(p.boolValue ? "true" : "false");
                    return;
                case SerializedPropertyType.Float:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(Math.Round(p.doubleValue, 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case SerializedPropertyType.String:
                    sb.Append(pad).Append(label).Append(" = \"").Append(Cut(p.stringValue, 300)).AppendLine("\"");
                    return;
                case SerializedPropertyType.Enum:
                    var names = p.enumDisplayNames;
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(p.enumValueIndex >= 0 && p.enumValueIndex < names.Length ? names[p.enumValueIndex] : p.intValue.ToString());
                    return;
                case SerializedPropertyType.ObjectReference:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(Ref(p.objectReferenceValue));
                    return;
                case SerializedPropertyType.Vector2:
                    sb.Append(pad).Append(label).Append(" = (").Append(R(p.vector2Value.x)).Append(", ").Append(R(p.vector2Value.y)).AppendLine(")");
                    return;
                case SerializedPropertyType.Vector3:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(V(p.vector3Value));
                    return;
                case SerializedPropertyType.Vector4:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(p.vector4Value.ToString());
                    return;
                case SerializedPropertyType.Quaternion:
                    sb.Append(pad).Append(label).Append(" = euler ").AppendLine(V(p.quaternionValue.eulerAngles));
                    return;
                case SerializedPropertyType.Color:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(p.colorValue.ToString());
                    return;
                case SerializedPropertyType.Rect:
                    sb.Append(pad).Append(label).Append(" = ").AppendLine(p.rectValue.ToString());
                    return;
                case SerializedPropertyType.Bounds:
                    sb.Append(pad).Append(label).Append(" = center ").Append(V(p.boundsValue.center)).Append(" extents ").AppendLine(V(p.boundsValue.extents));
                    return;
                case SerializedPropertyType.LayerMask:
                    sb.Append(pad).Append(label).Append(" = layers ").AppendLine(p.intValue == -1 ? "Everything" : p.intValue.ToString());
                    return;
                case SerializedPropertyType.AnimationCurve:
                    sb.Append(pad).Append(label).Append(" = curve of ").Append(p.animationCurveValue?.length ?? 0).AppendLine(" keys");
                    return;
                case SerializedPropertyType.ManagedReference:
                    sb.Append(pad).Append(label).Append(" = (").Append(p.managedReferenceFullTypename).AppendLine(")");
                    break;
            }
            if (p.type == "PersistentCallGroup" || label == "persistentCalls")
            {
                PersistentCalls(sb, p, pad, label);
                return;
            }
            if (p.isArray && p.propertyType == SerializedPropertyType.Generic)
            {
                if (builtin && depth > 0) return;
                sb.Append(pad).Append(label).Append(" = [").Append(p.arraySize).AppendLine(" items]");
                if (depth >= MaxDepth) return;
                for (var i = 0; i < p.arraySize && i < MaxArray; ++i)
                {
                    var e = p.GetArrayElementAtIndex(i);
                    if (e.propertyType == SerializedPropertyType.Generic)
                    {
                        sb.Append(pad).Append("  [").Append(i).AppendLine("]");
                        Children(sb, e, pad + "    ", depth + 1);
                    }
                    else
                    {
                        var line = new StringBuilder();
                        Value(line, e, "", depth + 1);
                        sb.Append(pad).Append("  [").Append(i).Append("] ").Append(line.ToString().Substring(line.ToString().IndexOf('=') + 2));
                    }
                }
                if (p.arraySize > MaxArray) sb.Append(pad).AppendLine("  ...");
                return;
            }
            if (p.propertyType == SerializedPropertyType.Generic && p.hasVisibleChildren)
            {
                // (Unity's own components: their nested settings structs are noise; events and arrays are kept)
                if (builtin && !p.type.Contains("Event")) return;
                if (p.type.Contains("UnityEvent") || p.type.EndsWith("Event", StringComparison.Ordinal))
                {
                    var calls = p.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    if (calls != null)
                    {
                        PersistentCalls(sb, p.FindPropertyRelative("m_PersistentCalls"), pad, label);
                        return;
                    }
                }
                sb.Append(pad).Append(label).AppendLine(":");
                if (depth < MaxDepth) Children(sb, p, pad + "  ", depth + 1);
            }
        }

        static void Children(StringBuilder sb, SerializedProperty p, string pad, int depth)
        {
            var copy = p.Copy();
            var end = copy.GetEndProperty();
            if (!copy.NextVisible(true)) return;
            var n = 0;
            while (!SerializedProperty.EqualContents(copy, end))
            {
                if (++n > 60) break;
                Value(sb, copy, pad, depth);
                if (!copy.NextVisible(false)) break;
            }
        }

        static void PersistentCalls(StringBuilder sb, SerializedProperty group, string pad, string label)
        {
            var calls = group?.FindPropertyRelative("m_Calls");
            sb.Append(pad).Append(label).Append(" calls: ");
            if (calls == null || calls.arraySize == 0)
            {
                sb.AppendLine("(none set in the editor; code may add listeners)");
                return;
            }
            var parts = new List<string>();
            for (var i = 0; i < calls.arraySize; ++i)
            {
                var c = calls.GetArrayElementAtIndex(i);
                var target = c.FindPropertyRelative("m_Target")?.objectReferenceValue;
                var method = c.FindPropertyRelative("m_MethodName")?.stringValue;
                var arg = c.FindPropertyRelative("m_Arguments.m_StringArgument")?.stringValue;
                parts.Add($"{Ref(target)}.{method}({(string.IsNullOrEmpty(arg) ? "" : "\"" + arg + "\"")})");
            }
            sb.AppendLine(string.Join("; ", parts));
        }

        static string Ref(Object o)
        {
            if (o == null) return "None";
            if (o is GameObject go && go.scene.IsValid()) return Paths.Of(go);
            if (o is Transform tr && tr.gameObject.scene.IsValid()) return Paths.Of(tr.gameObject);
            if (o is Component c && c.gameObject.scene.IsValid()) return Paths.Of(c);
            var path = AssetDatabase.GetAssetPath(o);
            if (o is Component pc && !string.IsNullOrEmpty(path)) return $"{pc.GetType().Name} on {PrefabPath(pc.gameObject)} ({path})";
            if (o is GameObject pg && !string.IsNullOrEmpty(path)) return $"{PrefabPath(pg)} ({path})";
            return string.IsNullOrEmpty(path) ? $"{o.GetType().Name} {o.name}" : $"{o.GetType().Name} {o.name} ({path})";
        }

        static string Cut(string s, int n)
        {
            s = (s ?? "").Replace("\n", "\\n");
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }

        // -- input ---------------------------------------------------------------------------------------------

        static int ExportInput(string dir)
        {
            var n = 0;
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var paths = AssetDatabase.FindAssets("t:InputActionAsset").Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
                if (asset == null) continue;
                var sb = new StringBuilder();
                var projectWide = false;
#if AUTOPILOT_PROJECT_WIDE_ACTIONS
                try
                {
                    projectWide = UnityEngine.InputSystem.InputSystem.actions != null && AssetDatabase.GetAssetPath(UnityEngine.InputSystem.InputSystem.actions) == path;
                }
                catch
                {
                    // (none)
                }
#endif
                sb.AppendLine($"# Input actions {path}{(projectWide ? " (the project-wide actions)" : "")}");
                foreach (var scheme in asset.controlSchemes)
                    sb.AppendLine($"control scheme {scheme.name}: {string.Join(", ", scheme.deviceRequirements.Select(d => d.controlPath + (d.isOptional ? " (optional)" : "")))}");
                foreach (var map in asset.actionMaps)
                {
                    sb.AppendLine();
                    sb.AppendLine($"map {map.name}");
                    foreach (var action in map.actions)
                    {
                        sb.AppendLine($"  action {map.name}/{action.name}: {action.type}{(string.IsNullOrEmpty(action.expectedControlType) ? "" : " " + action.expectedControlType)}"
                                      + (string.IsNullOrEmpty(action.interactions) ? "" : $" interactions {action.interactions}")
                                      + (string.IsNullOrEmpty(action.processors) ? "" : $" processors {action.processors}"));
                        foreach (var b in action.bindings)
                        {
                            if (b.isComposite) sb.AppendLine($"    composite {b.path} {b.name}");
                            else
                                sb.AppendLine($"    {(b.isPartOfComposite ? "  " + b.name + ": " : "")}{b.path}"
                                              + (string.IsNullOrEmpty(b.groups) ? "" : $" [{b.groups}]")
                                              + (string.IsNullOrEmpty(b.interactions) ? "" : $" interactions {b.interactions}")
                                              + (string.IsNullOrEmpty(b.processors) ? "" : $" processors {b.processors}"));
                        }
                    }
                }
                Write(dir, Strip(path) + ".txt", sb);
                ++n;
            }
#endif
            // The legacy Input Manager's axes (ProjectSettings/InputManager.asset)
            var manager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset").FirstOrDefault();
            if (manager != null)
            {
                var so = new SerializedObject(manager);
                var axes = so.FindProperty("m_Axes");
                if (axes != null && axes.arraySize > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"# Legacy Input Manager axes (UnityEngine.Input; {(Hands.LegacyInputEnabled ? "enabled" : "not enabled")} in this project)");
                    for (var i = 0; i < axes.arraySize; ++i)
                    {
                        var a = axes.GetArrayElementAtIndex(i);
                        string S(string f) => a.FindPropertyRelative(f)?.stringValue ?? "";
                        sb.AppendLine($"{S("m_Name")}: +{S("positiveButton")} -{S("negativeButton")} alt +{S("altPositiveButton")} -{S("altNegativeButton")} type {a.FindPropertyRelative("type")?.intValue} axis {a.FindPropertyRelative("axis")?.intValue}");
                    }
                    Write(dir, "LegacyInputManager.txt", sb);
                    ++n;
                }
            }
            return n;
        }
    }
}
