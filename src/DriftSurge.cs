using System.Collections.Generic;
using UnityEngine;

// DRIFT SURGE — low-poly drift-circuit time attack built on a MINI-TURBO BOOST ECONOMY.
// One control: STEER (arrows / A,D / hold-drag pointer & touch). Throttle is automatic.
//
// The distinct core (vs the studio's drift-apex = free style-score + self-ghost, and
// tandem-drift = chase battle): here a drift is FUEL, not points. Holding a slide charges a
// boost meter up through three tiers — SPARK -> BLAZE -> NOVA. The tighter/faster and the LONGER
// you hold the slide, the higher the tier. Release the drift on corner exit to CASH the charge as
// a forward SURGE (speed burst + FOV kick + neon trail). Chain surges corner-to-corner without
// spinning out or ploughing the grass to grow a BOOST CHAIN multiplier; land enough clean NOVA-grade
// surges and FEVER ignites (score x2 + gold bloom). The reading every corner: "how long dare I hold
// this slide before the exit to reach the next tier?" Beat your BEST lap; a translucent GHOST of it
// races you. 30 seconds in you're already deciding whether to greed a NOVA out of the hairpin.
//
// Built entirely in code (CreatePrimitive + a couple procedural meshes) so it renders reliably in
// WebGL with engine-code stripping disabled. NO Rigidbody/colliders: the car is pure Transform-driven
// (arcade drift model integrated by hand); all track tests are distance/projection checks against a
// sampled centerline. HUD/underglow use Sprites/Default (URP/Unlit strips in WebGL builds). Coexists
// with Juice (sfx/bgm/particles) & AutoShot.
public class DriftSurge : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__DriftSurge");
        go.AddComponent<DriftSurge>();
        DontDestroyOnLoad(go);
    }

    // ---- scene refs ----
    Transform carT;       // root: position + yaw(heading)
    Transform carVisual;  // child: cosmetic roll/pitch
    Transform underGlow;  // flat quad under the car, tinted by charge tier
    Transform ghostT;     // ghost car root
    Transform cam; Camera camComp;
    TextMesh hudLap, hudBest, hudSpeed, hudScore, tierText, chainText, bannerText, dbg;
    Transform barBg, barFill; Transform[] barTick = new Transform[2];
    Renderer barFillR, glowR, feverR;

    // ---- track (sampled closed centerline) ----
    Vector3[] pts;        // centerline points (y=0)
    Vector3[] leftN;      // unit left-normal per point
    float[] cum;          // cumulative arc length
    float trackLen;
    int N;
    Vector3 startPos, startFwd;   // start/finish line plane (robust lap detection)
    float prevSd;                 // previous signed distance past the start plane
    const float HALF_W = 7.4f;        // road half-width (asphalt) — wide enough to hold a drift line
    const float SOFT_W = 9.6f;        // beyond this = full grass penalty

    // ---- car state ----
    Vector3 pos;            // car ground position (y=0)
    float heading;          // body yaw, deg (0 = +Z)
    float velAngle;         // travel direction, deg
    float speed;            // m/s, >=0
    int nearIdx;            // current nearest centerline index
    bool halfFlag;          // passed track midpoint this lap (lap-gate guard)
    float steerInput;       // -1..1 resolved each frame
    float camYaw;           // smoothed camera yaw
    float fovPunch;

    // ---- boost economy (the core) ----
    bool drifting;
    float charge;           // seconds-of-good-slide accumulated in the current drift
    float driftHold;        // time since the slide dropped below threshold (release grace)
    float offRoadT;         // time spent deep off-road (forgives brief edge clips)
    int chargeTier;         // 0 none / 1 SPARK / 2 BLAZE / 3 NOVA — highest reached this slide
    float boostSpeed;       // extra m/s cap granted by the last surge (decays)
    float boostGlow;        // 0..1 visual boost intensity (decays)
    int boostChain;         // clean surges linked in a row
    bool fever;
    float trailT;
    const float T1 = 0.35f, T2 = 0.95f, T3 = 1.8f;     // charge thresholds; NOVA caps at T3
    const float DRIFT_DEG = 10f;                       // |slip| above this counts as a slide
    const int FEVER_CHAIN = 3;

    // ---- scoring / timing ----
    int score, best;
    float comboFlash;
    float lapTime, lastLap, bestLap;
    int lapCount = 1;
    float bannerTimer, tierFlash;
    float smokeT, sessionT;

    // ---- ghost recording ----
    struct Sample { public Vector3 pos; public float yaw; }
    readonly List<Sample> recCur = new List<Sample>();
    List<Sample> ghost;
    float recT; const float REC_DT = 0.045f;

    // ---- decorations ----
    class Cone { public Transform t; public Vector3 p; public bool knocked; }
    readonly List<Cone> cones = new List<Cone>();

    // ---- HUD layout (aspect-adaptive) ----
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    // ---- tuning ----
    const float MAX_SPEED = 32f, GRASS_MAX = 13f, ACCEL = 24f;
    const float TURN_RATE = 152f;

    bool attract = true;
    bool showDbg;

    static readonly Color C_SPARK = new Color(0.35f, 0.75f, 1f);
    static readonly Color C_BLAZE = new Color(1f, 0.6f, 0.15f);
    static readonly Color C_NOVA  = new Color(1f, 0.35f, 0.95f);
    // pentatonic climb per tier (C major pentatonic across tiers/chain)
    static readonly float[] PENTA = { 523.25f, 587.33f, 659.25f, 783.99f, 880f, 1046.5f, 1174.66f, 1318.5f };

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);
        foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            if (mr.gameObject.scene.IsValid() && mr.name.StartsWith("Cube")) Destroy(mr.gameObject);

        bestLap = PlayerPrefs.GetFloat("driftsurge_bestlap", 0f);
        best = PlayerPrefs.GetInt("driftsurge_bestscore", 0);

        BuildEnvironment();
        BuildTrack();
        BuildCamera();
        BuildCar();
        BuildGhost();
        BuildCones();
        BuildHud();

        pos = pts[0];
        heading = velAngle = HeadingFromTo(pts[0], pts[1 % N]);
        nearIdx = 0;
        prevSd = Vector3.Dot(pos - startPos, startFwd);
        camYaw = heading;
        SyncCar();
        UpdateCamera(0.0001f, true);
    }

    // ===================================================================== materials / meshes
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.2f, bool emissive = false)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 0.7f);
        }
        return m;
    }

    // Unlit, WebGL-safe (Sprites/Default survives stripping). Used for HUD, glow, tints.
    static Material MatUnlit(Color c)
    {
        var sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        var m = new Material(sh);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        m.color = c;
        return m;
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    static Mesh _cone;
    static Mesh ConeMesh()
    {
        if (_cone != null) return _cone;
        int seg = 10; var v = new List<Vector3>(); var tri = new List<int>();
        v.Add(new Vector3(0, 1f, 0));
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f));
        }
        int baseC = v.Count; v.Add(Vector3.zero);
        for (int i = 0; i < seg; i++)
        {
            int a = 1 + i, b = 1 + (i + 1) % seg;
            tri.Add(0); tri.Add(b); tri.Add(a);
            tri.Add(baseC); tri.Add(a); tri.Add(b);
        }
        _cone = new Mesh(); _cone.SetVertices(v); _cone.SetTriangles(tri, 0);
        _cone.RecalculateNormals(); _cone.RecalculateBounds();
        return _cone;
    }

    GameObject MeshObj(Mesh m, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = new GameObject("m");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = lscale;
        g.AddComponent<MeshFilter>().sharedMesh = m;
        g.AddComponent<MeshRenderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    // ===================================================================== world
    Material asphaltMat, lineMat, grassMat, bodyMat, ghostMat, coneMat, treeMat, trunkMat, tireMat;

    void BuildEnvironment()
    {
        asphaltMat = Mat(new Color(0.13f, 0.14f, 0.17f), 0.05f, 0.35f);
        lineMat    = Mat(new Color(0.95f, 0.95f, 0.98f), 0f, 0.1f);
        grassMat   = Mat(new Color(0.10f, 0.20f, 0.26f), 0f, 0.05f);   // cool teal turf (neon palette)
        bodyMat    = Mat(new Color(0.95f, 0.85f, 0.2f), 0.35f, 0.72f); // vivid yellow racer
        ghostMat   = Mat(new Color(0.5f, 0.9f, 1f), 0f, 0.4f, true);
        coneMat    = Mat(new Color(1f, 0.5f, 0.08f), 0f, 0.3f, true);
        treeMat    = Mat(new Color(0.14f, 0.42f, 0.5f), 0f, 0.05f);
        trunkMat   = Mat(new Color(0.2f, 0.22f, 0.28f), 0f, 0.05f);
        tireMat    = Mat(new Color(0.06f, 0.06f, 0.07f), 0f, 0.2f);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.85f, 0.9f, 1f);
        sun.intensity = 1.15f;
        sun.transform.rotation = Quaternion.Euler(52f, 40f, 0f);
        sun.shadows = LightShadows.Soft;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.35f, 0.45f, 0.72f);
        RenderSettings.ambientEquatorColor = new Color(0.28f, 0.35f, 0.46f);
        RenderSettings.ambientGroundColor  = new Color(0.10f, 0.14f, 0.18f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.16f, 0.22f, 0.34f);
        RenderSettings.fogStartDistance = 130f;
        RenderSettings.fogEndDistance = 360f;

        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var gc = g.GetComponent<Collider>(); if (gc != null) Destroy(gc);
        g.name = "Ground";
        g.transform.localScale = new Vector3(900f, 1f, 900f);
        g.transform.position = new Vector3(0, -0.55f, 0);
        g.GetComponent<Renderer>().sharedMaterial = grassMat;
    }

    void BuildTrack()
    {
        // closed loop control points: ellipse + per-corner radial variation (a couple of tight
        // hairpins to reward long NOVA slides, a couple of fast sweepers). No self-intersection.
        float[] var12 = { 0.05f, -0.17f, 0.14f, -0.21f, 0.12f, -0.08f, 0.22f, -0.22f, 0.08f, 0.18f, -0.14f, -0.02f };
        int K = var12.Length;
        float baseR = 76f;
        var cp = new Vector3[K];
        for (int i = 0; i < K; i++)
        {
            float a = i * Mathf.PI * 2f / K;
            float r = baseR * (1f + var12[i]);
            cp[i] = new Vector3(Mathf.Cos(a) * r * 1.16f, 0f, Mathf.Sin(a) * r * 0.9f);
        }

        const int SEG = 16;
        N = K * SEG;
        pts = new Vector3[N];
        int idx = 0;
        for (int s = 0; s < K; s++)
        {
            Vector3 p0 = cp[(s - 1 + K) % K], p1 = cp[s], p2 = cp[(s + 1) % K], p3 = cp[(s + 2) % K];
            for (int j = 0; j < SEG; j++)
            {
                float t = (float)j / SEG;
                pts[idx++] = CatmullRom(p0, p1, p2, p3, t);
            }
        }

        leftN = new Vector3[N];
        cum = new float[N];
        float acc = 0f;
        for (int i = 0; i < N; i++)
        {
            Vector3 fwd = (pts[(i + 1) % N] - pts[(i - 1 + N) % N]); fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            leftN[i] = new Vector3(-fwd.z, 0f, fwd.x);
            cum[i] = acc;
            acc += (pts[(i + 1) % N] - pts[i]).magnitude;
        }
        trackLen = acc;
        startPos = pts[0];
        startFwd = (pts[1 % N] - pts[0]); startFwd.y = 0f; startFwd.Normalize();

        BuildRoadMesh();
        BuildStartLine();
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    void BuildRoadMesh()
    {
        var rv = new Vector3[N * 2];
        var rt = new int[N * 6];
        for (int i = 0; i < N; i++)
        {
            rv[i * 2 + 0] = pts[i] + leftN[i] * HALF_W;
            rv[i * 2 + 1] = pts[i] - leftN[i] * HALF_W;
        }
        for (int i = 0; i < N; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = ((i + 1) % N) * 2, d = ((i + 1) % N) * 2 + 1;
            int o = i * 6;
            rt[o + 0] = a; rt[o + 1] = c; rt[o + 2] = b;
            rt[o + 3] = b; rt[o + 4] = c; rt[o + 5] = d;
        }
        var road = new Mesh { name = "road" }; road.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        road.vertices = rv; road.triangles = rt; road.RecalculateNormals(); road.RecalculateBounds();
        var rgo = new GameObject("Road");
        rgo.transform.position = new Vector3(0, 0.02f, 0);
        rgo.AddComponent<MeshFilter>().sharedMesh = road;
        rgo.AddComponent<MeshRenderer>().sharedMaterial = asphaltMat;

        // glowing cyan rumble ribbons on both edges + dashed center
        var rumble = Mat(new Color(0.2f, 0.85f, 1f), 0f, 0.3f, true);
        BuildEdgeRibbon(HALF_W - 0.3f, 0.34f, rumble, 0.035f);
        BuildEdgeRibbon(-(HALF_W - 0.3f), 0.34f, rumble, 0.035f);
        BuildDashedCenter();
    }

    void BuildEdgeRibbon(float offset, float width, Material mat, float y)
    {
        var rv = new Vector3[N * 2];
        var rt = new int[N * 6];
        for (int i = 0; i < N; i++)
        {
            rv[i * 2 + 0] = pts[i] + leftN[i] * (offset + width * 0.5f);
            rv[i * 2 + 1] = pts[i] + leftN[i] * (offset - width * 0.5f);
        }
        for (int i = 0; i < N; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = ((i + 1) % N) * 2, d = ((i + 1) % N) * 2 + 1;
            int o = i * 6;
            rt[o + 0] = a; rt[o + 1] = c; rt[o + 2] = b;
            rt[o + 3] = b; rt[o + 4] = c; rt[o + 5] = d;
        }
        var m = new Mesh { name = "edge" }; m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = rv; m.triangles = rt; m.RecalculateNormals(); m.RecalculateBounds();
        var go = new GameObject("Edge");
        go.transform.position = new Vector3(0, y, 0);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
    }

    void BuildDashedCenter()
    {
        var dashMat = Mat(new Color(0.8f, 0.75f, 0.3f), 0f, 0.1f);
        for (int i = 0; i < N; i += 6)
        {
            Vector3 fwd = (pts[(i + 1) % N] - pts[i]); fwd.y = 0; fwd.Normalize();
            var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = q.GetComponent<Collider>(); if (col) Destroy(col);
            q.transform.position = pts[i] + Vector3.up * 0.04f;
            q.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            q.transform.localScale = new Vector3(0.22f, 0.02f, 2.2f);
            q.GetComponent<Renderer>().sharedMaterial = dashMat;
        }
    }

    void BuildStartLine()
    {
        Vector3 fwd = (pts[1 % N] - pts[0]); fwd.y = 0; fwd.Normalize();
        var black = Mat(new Color(0.05f, 0.05f, 0.05f), 0f, 0.1f);
        int cells = 8;
        for (int c = 0; c < cells; c++)
        {
            float f = (c / (float)cells - 0.5f) * 2f;
            for (int row = 0; row < 2; row++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = q.GetComponent<Collider>(); if (col) Destroy(col);
                q.transform.position = pts[0] + leftN[0] * (f * HALF_W) + fwd * (row * 0.9f - 0.45f) + Vector3.up * 0.05f;
                q.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                q.transform.localScale = new Vector3(HALF_W * 2f / cells, 0.02f, 0.9f);
                q.GetComponent<Renderer>().sharedMaterial = ((c + row) % 2 == 0) ? lineMat : black;
            }
        }
        for (int side = -1; side <= 1; side += 2)
        {
            var p = new GameObject("post");
            p.transform.position = pts[0] + leftN[0] * (side * (HALF_W + 1.4f));
            Prim(PrimitiveType.Cylinder, p.transform, new Vector3(0, 3f, 0), new Vector3(0.4f, 3f, 0.4f), new Color(0.85f, 0.9f, 0.95f));
            Prim(PrimitiveType.Cube, p.transform, new Vector3(side * -1.6f, 6.2f, 0), new Vector3(3.4f, 1f, 0.3f), new Color(0.2f, 0.85f, 1f), Mat(new Color(0.2f, 0.85f, 1f), 0, 0.4f, true));
        }
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.09f, 0.12f, 0.2f);
        camComp.fieldOfView = 60f;
        camComp.farClipPlane = 600f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
    }

    void BuildCar()
    {
        carT = new GameObject("Car").transform;
        carVisual = new GameObject("CarVisual").transform;
        carVisual.SetParent(carT, false);
        BuildCarBody(carVisual, bodyMat);

        // flat underglow quad (tinted by charge tier / boost)
        var gq = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var col = gq.GetComponent<Collider>(); if (col) Destroy(col);
        gq.name = "underglow";
        gq.transform.SetParent(carVisual, false);
        gq.transform.localPosition = new Vector3(0, 0.06f, -0.1f);
        gq.transform.localRotation = Quaternion.Euler(90f, 0, 0);
        gq.transform.localScale = new Vector3(3.4f, 5.2f, 1f);
        glowR = gq.GetComponent<Renderer>();
        glowR.material = MatUnlit(new Color(0.35f, 0.75f, 1f, 0f));
        underGlow = gq.transform;
    }

    void BuildGhost()
    {
        ghostT = new GameObject("Ghost").transform;
        var gv = new GameObject("GhostVisual").transform;
        gv.SetParent(ghostT, false);
        BuildCarBody(gv, ghostMat);
        ghostT.gameObject.SetActive(false);
    }

    void BuildCarBody(Transform root, Material body)
    {
        Prim(PrimitiveType.Cube, root, new Vector3(0, 0.55f, 0.1f), new Vector3(1.9f, 0.6f, 4.0f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.05f, -0.25f), new Vector3(1.6f, 0.55f, 2.1f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.07f, 0.85f), new Vector3(1.45f, 0.5f, 0.12f), new Color(0.1f, 0.13f, 0.18f));
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.07f, -1.32f), new Vector3(1.45f, 0.5f, 0.12f), new Color(0.1f, 0.13f, 0.18f));
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.18f, -2.0f), new Vector3(1.8f, 0.12f, 0.5f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0.65f, 0.98f, -1.95f), new Vector3(0.12f, 0.4f, 0.2f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(-0.65f, 0.98f, -1.95f), new Vector3(0.12f, 0.4f, 0.2f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0.55f, 0.6f, 2.02f), new Vector3(0.5f, 0.28f, 0.06f), default, Mat(new Color(1f, 0.95f, 0.7f), 0, 0.5f, true));
        Prim(PrimitiveType.Cube, root, new Vector3(-0.55f, 0.6f, 2.02f), new Vector3(0.5f, 0.28f, 0.06f), default, Mat(new Color(1f, 0.95f, 0.7f), 0, 0.5f, true));
        // rear tail lights
        Prim(PrimitiveType.Cube, root, new Vector3(0.55f, 0.6f, -2.02f), new Vector3(0.5f, 0.24f, 0.06f), default, Mat(new Color(1f, 0.2f, 0.15f), 0, 0.5f, true));
        Prim(PrimitiveType.Cube, root, new Vector3(-0.55f, 0.6f, -2.02f), new Vector3(0.5f, 0.24f, 0.06f), default, Mat(new Color(1f, 0.2f, 0.15f), 0, 0.5f, true));
        float wx = 1.02f, wz = 1.35f, wy = 0.38f;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                var w = Prim(PrimitiveType.Cylinder, root, new Vector3(sx * wx, wy, sz * wz), new Vector3(0.42f, 0.16f, 0.42f), default, tireMat);
                w.transform.localRotation = Quaternion.Euler(0, 0, 90f);
            }
    }

    void BuildCones()
    {
        for (int i = 0; i < N; i += 11)
        {
            float side = ((i / 11) % 2 == 0) ? 1f : -1f;
            Vector3 p = pts[i] + leftN[i] * side * (HALF_W - 1.1f);
            var go = new GameObject("cone");
            go.transform.position = p;
            MeshObj(ConeMesh(), go.transform, Vector3.zero, new Vector3(0.5f, 0.85f, 0.5f), default, coneMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 0.18f, 0), new Vector3(0.7f, 0.12f, 0.7f), Color.white);
            cones.Add(new Cone { t = go.transform, p = p });
        }
        for (int i = 0; i < N; i += 9)
        {
            Vector3 p = pts[i] + leftN[i] * ((i % 18 == 0) ? 1f : -1f) * Random.Range(16f, 34f);
            var go = new GameObject("tree");
            go.transform.position = p;
            Prim(PrimitiveType.Cylinder, go.transform, new Vector3(0, 1f, 0), new Vector3(0.5f, 1f, 0.5f), default, trunkMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 1.5f, 0), new Vector3(3.2f, 3.2f, 3.2f), default, treeMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 3.2f, 0), new Vector3(2.4f, 2.6f, 2.4f), default, treeMat);
        }
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    Transform HudQuad(Color c, out Renderer r)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var col = q.GetComponent<Collider>(); if (col) Destroy(col);
        q.transform.SetParent(cam, false);
        r = q.GetComponent<Renderer>();
        r.material = MatUnlit(c);
        return q.transform;
    }

    void BuildHud()
    {
        // charge bar
        Renderer rb;
        barBg   = HudQuad(new Color(0.06f, 0.09f, 0.16f, 0.68f), out rb);
        barTick[0] = HudQuad(new Color(1f, 1f, 1f, 0.5f), out rb);
        barTick[1] = HudQuad(new Color(1f, 1f, 1f, 0.5f), out rb);
        barFill = HudQuad(C_SPARK, out barFillR);

        // full-screen fever tint (behind text, in front of world via camera-space z)
        HudQuad(new Color(1f, 0.8f, 0.2f, 0f), out feverR);

        hudLap   = MakeText(0.085f, Color.white, TextAnchor.UpperLeft);
        hudScore = MakeText(0.062f, new Color(1f, 0.85f, 0.3f), TextAnchor.UpperLeft);
        hudBest  = MakeText(0.055f, new Color(0.7f, 0.9f, 1f), TextAnchor.UpperRight);
        hudSpeed = MakeText(0.06f, new Color(0.9f, 0.95f, 1f), TextAnchor.LowerRight);
        tierText = MakeText(0.07f, Color.white, TextAnchor.MiddleCenter);
        chainText= MakeText(0.11f, new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleCenter);
        bannerText=MakeText(0.14f, Color.white, TextAnchor.MiddleCenter);
        dbg = MakeText(0.04f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        tierText.text = ""; chainText.text = ""; bannerText.text = "";
        AdjustHud();
        RefreshHud();
    }

    float barW, barH, barCX, barCY;

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 6.0f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.16f, 1.3f);
        float ix = halfW * 0.95f;

        // charge bar: full-width thin strip pinned at the very top edge (a boost gauge)
        barW = halfW * 1.9f;
        barH = 0.26f * hudScale;
        barCX = 0f; barCY = halfH * 0.93f;
        barBg.localPosition = new Vector3(barCX, barCY, HUD_Z);
        barBg.localScale = new Vector3(barW, barH, 1f);
        float[] tf = { T1 / T3, T2 / T3 };
        for (int i = 0; i < 2; i++)
        {
            barTick[i].localPosition = new Vector3(-barW * 0.5f + barW * tf[i], barCY, HUD_Z + 0.01f);
            barTick[i].localScale = new Vector3(0.03f * hudScale, barH, 1f);
        }

        // text row below the strip
        float iy = halfH * 0.78f;
        hudLap.transform.localPosition   = new Vector3(-ix, iy, HUD_Z); hudLap.characterSize  = 0.08f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy - 0.66f * hudScale, HUD_Z); hudScore.characterSize = 0.062f * hudScale;
        hudBest.transform.localPosition  = new Vector3( ix, iy, HUD_Z); hudBest.characterSize  = 0.052f * hudScale;
        hudSpeed.transform.localPosition = new Vector3( ix, -halfH * 0.9f, HUD_Z); hudSpeed.characterSize = 0.06f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -halfH * 0.45f, HUD_Z); dbg.characterSize = 0.04f * hudScale;
        tierText.transform.localPosition = new Vector3(0, iy + 0.05f * hudScale, HUD_Z);
        tierText.characterSize = 0.05f * hudScale;

        chainText.transform.localPosition = new Vector3(0, -halfH * 0.20f, HUD_Z);
        if (comboFlash <= 0f) chainText.characterSize = 0.085f * hudScale;
        feverR.transform.localPosition = new Vector3(0, 0, HUD_Z + 0.5f);
        feverR.transform.localScale = new Vector3(halfW * 2.4f, halfH * 2.4f, 1f);
    }

    void UpdateChargeBar()
    {
        float frac = Mathf.Clamp01(charge / T3);
        float fillW = Mathf.Max(0.0001f, barW * frac);
        barFill.localPosition = new Vector3(-barW * 0.5f + fillW * 0.5f, barCY, HUD_Z + 0.02f);
        barFill.localScale = new Vector3(fillW, barH * 0.62f, 1f);
        Color tc = chargeTier >= 3 ? C_NOVA : chargeTier >= 2 ? C_BLAZE : chargeTier >= 1 ? C_SPARK : new Color(0.4f, 0.55f, 0.7f);
        // pulse when at a fresh tier
        float pulse = tierFlash > 0f ? 1f + tierFlash * 0.8f : 1f;
        barFillR.material.color = tc * pulse;
        string tn = chargeTier >= 3 ? "NOVA" : chargeTier >= 2 ? "BLAZE" : chargeTier >= 1 ? "SPARK" : (charge > 0.05f ? "CHARGING" : "DRIFT TO CHARGE");
        tierText.text = tn;
        tierText.color = chargeTier >= 1 ? tc : new Color(0.6f, 0.72f, 0.85f);
    }

    void RefreshHud()
    {
        if (hudLap)   hudLap.text   = "LAP " + lapCount + "   " + Fmt(lapTime);
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudBest)  hudBest.text  = "BEST  " + best + (bestLap > 0f ? "\nLAP  " + Fmt(bestLap) : "");
        if (hudSpeed) hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }
    static string Fmt(float t) { int m = (int)(t / 60f); float s = t - m * 60f; return string.Format("{0}:{1:00.00}", m, s); }

    // ===================================================================== input
    void GatherInput()
    {
        float key = Input.GetAxisRaw("Horizontal");
        float pointer = 0f; bool pressed = false; float px = 0f;
        if (Input.touchCount > 0) { pressed = true; px = Input.GetTouch(0).position.x; }
        else if (Input.GetMouseButton(0)) { pressed = true; px = Input.mousePosition.x; }
        if (pressed)
        {
            float n = (px / Mathf.Max(1f, Screen.width)) * 2f - 1f;
            pointer = Mathf.Clamp(n * 1.7f, -1f, 1f);
        }
        float raw = Mathf.Abs(key) > 0.01f ? key : pointer;

        // hand control to the player on real steering/pointer input only (F1 & other keys must NOT
        // kill the attract demo, so QA can watch the debug overlay while the autopilot drives).
        if (Mathf.Abs(raw) > 0.01f || Input.GetMouseButton(0) || Input.touchCount > 0) attract = false;
        if (attract) raw = AutoSteer();

        steerInput = Mathf.Clamp(raw, -1f, 1f);
    }

    float AutoSteer()
    {
        // aim a few samples ahead, and ANTICIPATE the upcoming bend so the demo turns in hard enough
        // to break traction and drift the corners (showing off the charge->surge core), not just carve.
        int look = (nearIdx + 6) % N;
        int look2 = (nearIdx + 13) % N;
        float want = HeadingFromTo(pos, pts[look]);
        float diff = Mathf.DeltaAngle(heading, want);
        float bend = Mathf.DeltaAngle(want, HeadingFromTo(pts[look], pts[look2]));
        return Mathf.Clamp((diff + bend * 0.4f) / 17f, -1f, 1f);
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;
        sessionT += dt;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        GatherInput();

        UpdateTrackPosition();
        float lateral = Vector3.Dot(pos - pts[nearIdx], leftN[nearIdx]);
        float absLat = Mathf.Abs(lateral);
        bool onRoad = absLat < HALF_W;
        float grassFactor = Mathf.Clamp01((absLat - HALF_W) / (SOFT_W - HALF_W));

        // robust lap detection: arm at the track midpoint (broad range, can't be skipped), then fire
        // when the car actually crosses the START PLANE forward near the line (index-jitter-proof).
        if (nearIdx > (int)(N * 0.35f) && nearIdx < (int)(N * 0.65f)) halfFlag = true;
        float sd = Vector3.Dot(pos - startPos, startFwd);
        float slat = Vector3.Dot(pos - startPos, leftN[0]);
        if (halfFlag && prevSd < 0f && sd >= 0f && Mathf.Abs(slat) < HALF_W * 1.6f)
            CompleteLap();
        prevSd = sd;

        lapTime += dt;

        // ================= arcade drift model =================
        float driftAngle = Mathf.DeltaAngle(velAngle, heading);
        float absDrift = Mathf.Abs(driftAngle);

        // throttle: ease speed toward a cap. boostSpeed adds surge headroom on top.
        float cap = Mathf.Lerp(MAX_SPEED, GRASS_MAX, grassFactor);
        cap *= 1f - Mathf.Clamp01(absDrift / 70f) * 0.32f;   // big slides scrub base speed
        cap += boostSpeed;                                    // <-- surge lets you exceed normal top speed
        if (speed < cap) speed = Mathf.MoveTowards(speed, cap, (ACCEL + boostSpeed * 3f) * dt);
        else             speed = Mathf.MoveTowards(speed, cap, ACCEL * 1.5f * dt);
        boostSpeed = Mathf.MoveTowards(boostSpeed, 0f, 11f * dt);
        boostGlow  = Mathf.MoveTowards(boostGlow, 0f, 1.6f * dt);

        // steering -> heading
        float spAuth = Mathf.Clamp01(speed / 7f) * Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((speed - 18f) / 18f));
        heading += steerInput * TURN_RATE * spAuth * dt;
        heading = Norm(heading);

        // grip: gentle steer = tight carve (high grip); hard steer at speed = break away & slide.
        // (higher low-end grip => slides stay controllable and snap back on exit instead of ploughing wide)
        float grip = Mathf.Lerp(230f, 66f, Mathf.Abs(steerInput));
        grip *= Mathf.Lerp(1f, 0.6f, grassFactor);
        velAngle = Mathf.MoveTowardsAngle(velAngle, heading, grip * dt);
        driftAngle = Mathf.DeltaAngle(velAngle, heading);
        bool spunHard = Mathf.Abs(driftAngle) > 44f;   // bound the slide so drifts don't plough wide
        if (spunHard) velAngle = Norm(heading - Mathf.Sign(driftAngle) * 44f);

        Vector3 dir = Dir(velAngle);
        pos += dir * speed * dt;
        pos.y = 0f;

        // soft world boundary: the circuit fits within ~110m of origin. A car that ploughs far into
        // the grass is stopped at a 150m ring and gently DEFLECTED back toward the interior (never
        // pinned to zero speed) so it always recovers instead of soft-locking against the wall.
        float distO = Mathf.Sqrt(pos.x * pos.x + pos.z * pos.z);
        bool hitWall = distO > 150f;
        if (hitWall)
        {
            pos *= 150f / distO;
            float inwardAng = HeadingFromTo(pos, Vector3.zero);
            velAngle = Mathf.MoveTowardsAngle(velAngle, inwardAng, 260f * dt);
            heading  = Mathf.MoveTowardsAngle(heading,  inwardAng, 260f * dt);
            if (speed > GRASS_MAX) speed = GRASS_MAX;
        }

        SyncCar();
        UpdateBoostEconomy(driftAngle, onRoad, grassFactor, hitWall, dt);
        UpdateCones();
        UpdateGhost();
        RecordGhost(dt);
        UpdateCamera(dt, false);
        TickHud(dt);
        UpdateChargeBar();
        if (showDbg) UpdateDbg(lateral, driftAngle, grassFactor);
    }

    void UpdateTrackPosition()
    {
        float bestD = float.MaxValue; int bi = nearIdx;
        for (int k = -2; k <= 14; k++)
        {
            int i = ((nearIdx + k) % N + N) % N;
            float d = (pos - pts[i]).sqrMagnitude;
            if (d < bestD) { bestD = d; bi = i; }
        }
        nearIdx = bi;
    }

    void SyncCar()
    {
        carT.position = pos;
        carT.rotation = Quaternion.Euler(0, heading, 0);
        float driftA = Mathf.DeltaAngle(velAngle, heading);
        float roll = Mathf.Clamp(-steerInput * 7f - driftA * 0.12f, -16f, 16f);
        float pitch = -Mathf.Clamp01(speed / MAX_SPEED) * 2.5f - boostGlow * 2f;
        carVisual.localRotation = Quaternion.Slerp(carVisual.localRotation, Quaternion.Euler(pitch, 0, roll), 1f - Mathf.Exp(-10f * Time.deltaTime));
    }

    // ===================================================================== the core: charge -> surge
    void UpdateBoostEconomy(float driftAngle, bool onRoad, float grassFactor, bool hitWall, float dt)
    {
        float absDrift = Mathf.Abs(driftAngle);
        // charge while the car is actually sliding OR being cranked hard through a fast corner
        // (arcade "hold the drift" feel: the whole committed corner builds charge, apex slides most).
        bool slideNow = onRoad && speed > 10f && (absDrift > DRIFT_DEG || (Mathf.Abs(steerInput) > 0.5f && speed > 15f));

        // chain-break mistakes: PLOUGH deep grass (sustained, not a brief edge clip) or slam the
        // world boundary -> drop charge & chain. Brushing the rumble edge mid-drift is forgiven.
        if (grassFactor > 0.95f) offRoadT += dt; else offRoadT = Mathf.MoveTowards(offRoadT, 0f, dt * 2f);
        if (offRoadT > 0.7f || hitWall)
        {
            if (charge > 0.05f || boostChain > 0) BreakChain(hitWall);
            offRoadT = 0f;
        }

        if (slideNow)
        {
            if (!drifting) { drifting = true; }
            driftHold = 0f;
            // charge faster the tighter (bigger slip / harder crank) and faster you slide
            float gain = (Mathf.Clamp01(absDrift / 38f) * 1.1f + Mathf.Clamp01(Mathf.Abs(steerInput)) * 0.7f + 0.4f) * Mathf.Clamp01(speed / 15f);
            charge = Mathf.Min(charge + gain * dt, T3);
            int t = charge >= T3 ? 3 : charge >= T2 ? 2 : charge >= T1 ? 1 : 0;
            if (t > chargeTier)
            {
                chargeTier = t;
                tierFlash = 1f;
                Juice.Blip(PENTA[Mathf.Clamp(t - 1, 0, PENTA.Length - 1)] * 0.7f, 0.08f, 0.35f);
            }
            // tire smoke tinted by tier
            smokeT -= dt;
            if (smokeT <= 0f)
            {
                smokeT = 0.03f;
                Color sc = chargeTier >= 3 ? C_NOVA : chargeTier >= 2 ? C_BLAZE : chargeTier >= 1 ? C_SPARK : new Color(0.85f, 0.85f, 0.9f);
                sc.a = 0.9f;
                Vector3 rear = pos - Dir(heading) * 1.6f + Vector3.up * 0.3f;
                Juice.Pop(rear, sc, 4);
            }
            Juice.Shake(Mathf.Min(0.03f + absDrift * 0.0016f, 0.14f));
        }
        else if (drifting)
        {
            driftHold += dt;
            if (driftHold > 0.14f) ReleaseSurge();   // corner exit / straightened up -> cash it
        }

        // boost trail while surging
        if (boostGlow > 0.15f)
        {
            trailT -= dt;
            if (trailT <= 0f)
            {
                trailT = 0.035f;
                Color bc = Color.Lerp(C_SPARK, C_NOVA, boostGlow);
                bc.a = 0.85f;
                Vector3 rear = pos - Dir(heading) * (2.1f + Random.value) + Vector3.up * (0.4f + Random.value * 0.6f);
                Juice.Pop(rear, bc, 3);
            }
        }

        // underglow tint follows charge (idle) or boost (surging)
        Color glow = chargeTier >= 3 ? C_NOVA : chargeTier >= 2 ? C_BLAZE : chargeTier >= 1 ? C_SPARK : C_SPARK;
        float glowA = Mathf.Max(boostGlow, drifting ? Mathf.Clamp01(charge / T3) * 0.7f : 0f);
        if (fever) glow = Color.Lerp(glow, new Color(1f, 0.85f, 0.3f), 0.5f);
        if (glowR) { var gc = glow; gc.a = glowA * 0.85f; glowR.material.color = gc; }

        if (tierFlash > 0f) tierFlash = Mathf.MoveTowards(tierFlash, 0f, dt * 3f);
    }

    void ReleaseSurge()
    {
        drifting = false;
        int tier = chargeTier;
        charge = 0f; chargeTier = 0;
        if (tier < 1) { driftHold = 0f; return; }

        boostChain++;
        int chainMultI = Mathf.Min(boostChain, 12);
        float chainMult = 1f + (chainMultI - 1) * 0.2f;   // x1 .. x3.2

        // surge headroom & feel scale with tier
        float add = tier == 3 ? 15f : tier == 2 ? 9f : 5f;
        boostSpeed = Mathf.Max(boostSpeed, add);
        boostGlow = Mathf.Clamp01(0.4f + tier * 0.25f);
        fovPunch = Mathf.Max(fovPunch, 3f + tier * 3f);
        Juice.Shake(0.12f + tier * 0.05f);

        int gained = Mathf.RoundToInt(40 * tier * chainMult * (fever ? 2f : 1f));
        score += gained;
        comboFlash = 1f;

        string tn = tier == 3 ? "NOVA" : tier == 2 ? "BLAZE" : "SPARK";
        Color tc = tier == 3 ? C_NOVA : tier == 2 ? C_BLAZE : C_SPARK;
        FloatText(tn + " SURGE  +" + gained + (boostChain > 1 ? "   x" + chainMult.ToString("0.0") : ""), tc);
        Juice.Score(pos + Vector3.up * 1.2f);
        int noteIx = Mathf.Clamp(tier - 1 + Mathf.Min(boostChain - 1, 5), 0, PENTA.Length - 1);
        Juice.Blip(PENTA[noteIx], 0.09f, 0.45f);
        Juice.Blip(PENTA[noteIx] * 1.5f, 0.06f, 0.22f);

        // NOVA-grade links drive FEVER
        if (!fever && boostChain >= FEVER_CHAIN)
        {
            fever = true;
            Banner("FEVER!\nSCORE x2", new Color(1f, 0.85f, 0.3f), 1.6f);
            Juice.Blip(1046.5f, 0.12f, 0.5f);
            fovPunch = Mathf.Max(fovPunch, 8f);
        }

        if (score > best) { best = score; PlayerPrefs.SetInt("driftsurge_bestscore", best); PlayerPrefs.Save(); }
        RefreshHud();
        driftHold = 0f;
    }

    void BreakChain(bool hard)
    {
        bool had = fever || boostChain >= 2;
        charge = 0f; chargeTier = 0; drifting = false; driftHold = 0f;
        boostChain = 0;
        if (fever) { fever = false; }
        if (had)
        {
            FloatText("CHAIN LOST", new Color(1f, 0.4f, 0.4f));
            Juice.Blip(150f, 0.18f, 0.4f);
            if (hard) Juice.Shake(0.25f);
        }
    }

    // ===================================================================== laps
    void CompleteLap()
    {
        halfFlag = false;
        float t = lapTime;
        lastLap = t;
        bool nb = bestLap <= 0f || t < bestLap;
        if (nb)
        {
            bestLap = t;
            PlayerPrefs.SetFloat("driftsurge_bestlap", bestLap); PlayerPrefs.Save();
            ghost = new List<Sample>(recCur);
            ghostT.gameObject.SetActive(ghost.Count > 1);
        }
        lapCount++;
        lapTime = 0f;
        recCur.Clear(); recT = 0f;
        Juice.Score(pos + Vector3.up * 1.5f);
        Juice.Blip(900f, 0.09f, 0.45f); Juice.Blip(1350f, 0.08f, 0.35f);
        Juice.Shake(0.2f);
        fovPunch = Mathf.Max(fovPunch, 6f);
        Banner((nb ? "NEW BEST LAP!\n" : "LAP " + (lapCount - 1) + "\n") + Fmt(t), nb ? new Color(1f, 0.85f, 0.3f) : Color.white, 2.0f);
        RefreshHud();
    }

    // ===================================================================== ghost
    void RecordGhost(float dt)
    {
        recT += dt;
        if (recT >= REC_DT)
        {
            recT = 0f;
            if (recCur.Count < 4000) recCur.Add(new Sample { pos = pos, yaw = heading });
        }
    }

    void UpdateGhost()
    {
        if (ghost == null || ghost.Count < 2) { if (ghostT.gameObject.activeSelf) ghostT.gameObject.SetActive(false); return; }
        if (!ghostT.gameObject.activeSelf) ghostT.gameObject.SetActive(true);
        float f = lapTime / REC_DT;
        int i = Mathf.Clamp((int)f, 0, ghost.Count - 2);
        float frac = Mathf.Clamp01(f - i);
        Vector3 gp = Vector3.Lerp(ghost[i].pos, ghost[i + 1].pos, frac);
        float gy = Mathf.LerpAngle(ghost[i].yaw, ghost[i + 1].yaw, frac);
        ghostT.position = gp;
        ghostT.rotation = Quaternion.Euler(0, gy, 0);
    }

    // ===================================================================== cones
    void UpdateCones()
    {
        for (int i = 0; i < cones.Count; i++)
        {
            var c = cones[i];
            if (c.knocked || c.t == null) continue;
            Vector3 d = c.p - pos; d.y = 0f;
            if (d.sqrMagnitude < 2.4f * 2.4f && speed > 8f)
            {
                c.knocked = true;
                var fl = c.t.gameObject.AddComponent<Flyer>();
                Vector3 push = (d.sqrMagnitude > 0.01f ? d.normalized : Dir(velAngle));
                fl.Init(push * (3f + speed * 0.15f) + Vector3.up * 4f + Dir(velAngle) * speed * 0.2f);
                Juice.Blip(220f, 0.08f, 0.3f);
                Juice.Pop(c.p + Vector3.up * 0.4f, new Color(1f, 0.6f, 0.1f), 6);
                score += 25; comboFlash = 0.6f;
                if (score > best) { best = score; PlayerPrefs.SetInt("driftsurge_bestscore", best); PlayerPrefs.Save(); }
                RefreshHud();
            }
        }
    }

    // ===================================================================== camera / hud tick
    void UpdateCamera(float dt, bool snap)
    {
        if (cam == null) return;
        float targetYaw = velAngle;
        camYaw = snap ? targetYaw : Mathf.LerpAngle(camYaw, targetYaw, 1f - Mathf.Exp(-6f * dt));
        Vector3 back = Dir(camYaw);
        float dist = 10.5f + Mathf.Clamp01(speed / MAX_SPEED) * 2.5f + boostGlow * 1.5f;
        Vector3 want = pos - back * dist + Vector3.up * 4.8f;
        cam.position = snap ? want : Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-8f * dt));
        Vector3 look = pos + back * 6f + Vector3.up * 1.0f;
        Quaternion q = Quaternion.LookRotation(look - cam.position, Vector3.up);
        cam.rotation = snap ? q : Quaternion.Slerp(cam.rotation, q, 1f - Mathf.Exp(-9f * dt));

        fovPunch = Mathf.Lerp(fovPunch, 0f, 5f * dt);
        float baseFov = 58f + Mathf.Clamp01(speed / MAX_SPEED) * 14f + boostGlow * 6f;
        camComp.fieldOfView = Mathf.Clamp(baseFov + fovPunch, 50f, 90f);
        AdjustHud();
    }

    void TickHud(float dt)
    {
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.0f;
            float s = 0.085f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.5f);
            if (chainText) chainText.characterSize = s;
        }
        if (bannerTimer > 0f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
        // chain display
        if (boostChain >= 2)
        {
            chainText.text = "CHAIN x" + boostChain + (fever ? "  FEVER" : "");
            chainText.color = fever ? new Color(1f, 0.85f, 0.3f) : new Color(0.7f, 0.9f, 1f);
        }
        else chainText.text = "";
        // fever tint pulse
        if (feverR)
        {
            float target = fever ? 0.1f + 0.05f * Mathf.Sin(sessionT * 6f) : 0f;
            var c = feverR.material.color; c.a = Mathf.Lerp(c.a, target, 6f * dt); feverR.material.color = c;
        }
        hudLap.text = "LAP " + lapCount + "   " + Fmt(lapTime);
        hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }

    void FloatText(string s, Color c)
    {
        bannerText.transform.localPosition = new Vector3(0f, -halfH * 0.56f, HUD_Z);
        bannerText.characterSize = 0.095f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = 1.1f;
    }

    void Banner(string s, Color c, float dur)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.34f, HUD_Z);
        bannerText.characterSize = 0.14f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }

    void UpdateDbg(float lateral, float driftAngle, float grass)
    {
        dbg.text = string.Format(
            "spd {0:0.0} boost {1:0.0}  steer {2:0.00}\ndrift {3:0.0} head {4:0.0} vel {5:0.0}\nidx {6}/{7} lat {8:0.0} grass {9:0.00}\ncharge {10:0.00} tier {11} chain {12} fever {13}\nlap {14} t {15:0.00} best {16:0.00}\nscore {17} attract {18} fps {19:0}",
            speed, boostSpeed, steerInput, driftAngle, heading, velAngle, nearIdx, N, lateral, grass,
            charge, chargeTier, boostChain, fever, lapCount, lapTime, bestLap,
            score, attract, 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
    }

    // ===================================================================== math helpers
    static float Norm(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; else if (deg < -180f) deg += 360f; return deg; }
    static Vector3 Dir(float deg) { float r = deg * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)); }
    static float HeadingFromTo(Vector3 a, Vector3 b) { Vector3 d = b - a; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
}

// short-lived tumbling object (knocked cone) — pure transform, self-destructs.
public class Flyer : MonoBehaviour
{
    Vector3 vel; Vector3 spin; float age, life = 1.6f;
    public void Init(Vector3 v) { vel = v; spin = new Vector3(Random.Range(-400f, 400f), Random.Range(-400f, 400f), Random.Range(-400f, 400f)); }
    void Update()
    {
        float dt = Time.deltaTime; age += dt;
        vel.y -= 16f * dt;
        transform.position += vel * dt;
        transform.Rotate(spin * dt, Space.World);
        if (transform.position.y < 0f && vel.y < 0f) { vel.y = -vel.y * 0.4f; vel.x *= 0.6f; vel.z *= 0.6f; }
        if (age >= life) Destroy(gameObject);
    }
}
