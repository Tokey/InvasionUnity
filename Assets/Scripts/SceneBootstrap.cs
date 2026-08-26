using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// Builds the WHOLE scene from primitives at runtime and wires every reference.
    /// Usage: empty scene -> create one empty GameObject -> add this component -> Play.
    /// (Optionally assign a SortExperimentConfig asset; otherwise defaults are used.)
    /// Replace the primitive crates/pallets with real prefabs later by assigning
    /// cratePrefabOverride / palletPrefabOverride.
    /// </summary>
    public class SceneBootstrap : MonoBehaviour
    {
        public SortExperimentConfig configAsset;

        [Header("Optional prefab overrides (else primitives are used)")]
        public GameObject cratePrefabOverride;
        public GameObject palletPrefabOverride;

        const float TableY = 0.5f;

        public bool useExistingSceneSetup = false;

        void Awake()
        {
            var config = configAsset != null
                ? configAsset
                : ScriptableObject.CreateInstance<SortExperimentConfig>();

            if (useExistingSceneSetup && TryWireExistingScene(config))
                return; // done wiring, don't procedurally generate anything else

            // --- Camera ---------------------------------------------------
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            cam.transform.position = new Vector3(0f, 16f, -10f);
            cam.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f);

            // --- Light ----------------------------------------------------
            if (FindAnyObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                lightGo.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
                light.intensity = 1.1f;
            }

            // --- Table ----------------------------------------------------
            var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "Table";
            table.transform.position = new Vector3(0f, 0f, 0f);
            table.transform.localScale = new Vector3(26f, 1f, 18f);
            Tint(table, new Color(0.35f, 0.33f, 0.30f));

            // --- Zones (pallets), diagonal from the crates ------------------
            // Zones must exist before the crates: DraggableCrate caches the zone
            // list in its Awake, which runs the moment AddComponent is called.
            DropZone laggyZone = MakeZone("Zone_Laggy", new Vector3(-6.5f, 0f, 5.5f),
                config.oddZoneLabel, new Color(0.55f, 0.35f, 0.20f), true);
            DropZone okZone = MakeZone("Zone_Responsive", new Vector3(6.5f, 0f, 5.5f),
                config.normalZoneLabel, new Color(0.55f, 0.35f, 0.20f), false);

            // --- Crates (identical appearance!) -----------------------------
            DraggableCrate crateA = MakeCrate("Crate_A", new Vector3(-3f, 0f, -5.5f));
            DraggableCrate crateB = MakeCrate("Crate_B", new Vector3(3f, 0f, -5.5f));

            BuildManagers(config, crateA, crateB, laggyZone, okZone);
        }

        /// <summary>
        /// Finds hand-placed crates/zones and wires the experiment to them.
        /// Returns false (and warns) if anything is missing, so Awake can fall
        /// back to the procedural build.
        /// </summary>
        bool TryWireExistingScene(SortExperimentConfig config)
        {
            var crateAGo = GameObject.Find("Crate_A");
            var crateBGo = GameObject.Find("Crate_B");
            DraggableCrate crateA = crateAGo != null ? crateAGo.GetComponent<DraggableCrate>() : null;
            DraggableCrate crateB = crateBGo != null ? crateBGo.GetComponent<DraggableCrate>() : null;

            DropZone laggyZone = null;
            DropZone okZone = null;
            // Unordered — Unity no longer offers a sorted overload. Only matters if a scene has
            // more than one zone per kind, which the guard below already treats as malformed.
            foreach (var z in FindObjectsByType<DropZone>())
            {
                if (z.isLaggyZone) { if (laggyZone == null) laggyZone = z; }
                else if (okZone == null) okZone = z;
            }

            if (crateA == null || crateB == null || laggyZone == null || okZone == null)
            {
                Debug.LogWarning("[SceneBootstrap] useExistingSceneSetup is true but couldn't find all required scene objects (Crate_A, Crate_B, a DropZone with isLaggyZone and one without). Falling back to procedural build.");
                return false;
            }

            // Hand-placed crates never had Setup() called, so their home position
            // would default to the origin and both crates would teleport there on
            // the first ResetForTrial. Anchor them where the author placed them.
            crateA.Setup(crateA.transform.position);
            crateB.Setup(crateB.transform.position);

            BuildManagers(config, crateA, crateB, laggyZone, okZone);
            return true;
        }

        void BuildManagers(SortExperimentConfig config, DraggableCrate crateA, DraggableCrate crateB,
                           DropZone laggyZone, DropZone okZone)
        {
            var mgr = new GameObject("ExperimentManager");
            mgr.AddComponent<MouseHistory>();
            var logger = mgr.AddComponent<SortDataLogger>();
            var hud = mgr.AddComponent<SortHud>();
            var trial = mgr.AddComponent<TrialManager>();

            trial.config = config;
            trial.crateA = crateA;
            trial.crateB = crateB;
            trial.laggyZone = laggyZone;
            trial.responsiveZone = okZone;
            trial.logger = logger;
            trial.hud = hud;
        }

        DropZone MakeZone(string name, Vector3 pos, string label, Color color, bool isLaggy)
        {
            GameObject go;
            if (palletPrefabOverride != null)
            {
                go = Instantiate(palletPrefabOverride);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.localScale = new Vector3(4.2f, 0.25f, 4.2f);
                Tint(go, color);
                // Zones don't need to block clicks. Disable rather than Destroy:
                // Destroy is deferred to end of frame, so the collider would still
                // be raycast-able for the rest of this frame.
                var col = go.GetComponent<Collider>();
                if (col != null) col.enabled = false;
            }
            go.name = name;
            pos.y = TableY + 0.13f;
            go.transform.position = pos;

            var zone = go.AddComponent<DropZone>();
            zone.isLaggyZone = isLaggy;

            // Floating text label.
            var textGo = new GameObject(name + "_Label");
            textGo.transform.SetParent(go.transform, false);
            textGo.transform.localPosition = new Vector3(0f, 2.5f, 0f);
            textGo.transform.localScale = Vector3.one * 0.5f;
            var tm = textGo.AddComponent<TextMesh>();
            tm.text = label;
            tm.fontSize = 64;
            tm.characterSize = 0.18f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            textGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f); // face the camera

            return zone;
        }

        DraggableCrate MakeCrate(string name, Vector3 pos)
        {
            GameObject go;
            if (cratePrefabOverride != null)
            {
                go = Instantiate(cratePrefabOverride);
                if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.localScale = Vector3.one * 1.8f;
                Tint(go, new Color(0.72f, 0.55f, 0.30f)); // wood-ish; BOTH crates identical
            }
            go.name = name;

            // Sit the crate ON the table. Use the collider's real extents so prefab
            // overrides (whose localScale is usually 1) don't sink into the table.
            var col = go.GetComponent<Collider>();
            float halfHeight = col != null ? col.bounds.extents.y : go.transform.localScale.y * 0.5f;
            pos.y = TableY + halfHeight + 0.01f;
            go.transform.position = pos;

            var crate = go.AddComponent<DraggableCrate>();
            crate.dragPlaneY = TableY;
            crate.Setup(go.transform.position);
            return crate;
        }

        static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null) r.material.color = c;
        }
    }
}
