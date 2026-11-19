using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// AR Color Blaster - self-healing controller. Attach to the AR Camera.
/// - Repairs missing AR components at runtime (session, input manager, raycast/plane managers)
/// - Forces plane detection ON (horizontal + vertical), world camera, auto focus
/// - Shows live AR diagnostics on screen so you can SEE why tracking fails
/// - Works with the new Input System, the legacy Input Manager, or both
/// - Surface fallbacks: plane -> feature point -> 2 m in front of you
/// - Paint splats on the target (ring scoring) AND on real surfaces
/// </summary>
public class ARColorBlasterController : MonoBehaviour
{
    [Header("AR References (auto-found if empty)")]
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private ARPlaneManager planeManager;

    [Header("Gameplay")]
    [SerializeField] private int magazineSize = 20;
    [SerializeField] private float projectileSpeed = 18f;
    [SerializeField] private float targetHeight = 0.9f;
    [SerializeField] private float targetWidth = 0.9f;
    [SerializeField] private float targetBoardHeight = 1.15f;
    [SerializeField] private float paintMarkSize = 0.11f;
    [SerializeField] private float maxShootDistance = 30f;
    [SerializeField] private int maxPaintMarks = 250;

    private static readonly List<ARRaycastHit> Hits = new List<ARRaycastHit>();

    private readonly List<GameObject> paintMarks = new List<GameObject>();
    private readonly List<GameObject> targets = new List<GameObject>();
    private readonly List<RectTransform> uiBlockers = new List<RectTransform>();
    private readonly List<Outline> swatchOutlines = new List<Outline>();

    private readonly Color[] colors =
    {
        new Color(1f, 0.12f, 0.12f),
        new Color(0.08f, 0.42f, 1f),
        new Color(0.08f, 0.85f, 0.28f),
        new Color(1f, 0.78f, 0.04f),
        new Color(0.65f, 0.12f, 1f),
        new Color(1f, 0.25f, 0.58f),
        Color.white
    };

    // Scene references
    private Camera cam;
    private ARSession arSession;
    private XROrigin xrOrigin;
    private Transform trackablesParent;

    // Game state
    private Color selectedColor;
    private int ammo;
    private int score;
    private bool reloading;

    // UI
    private Font uiFont;
    private Text scoreText;
    private Text ammoText;
    private Text hintText;
    private Text diagText;

    // Gun
    private Transform gunRoot;
    private Transform gunMuzzle;
    private Transform gunColorPart;
    private Vector3 gunRestPosition;
    private float gunKick;

    // Reticle
    private GameObject surfaceReticle;
    private Material reticlePlaneMaterial;
    private Material reticlePointMaterial;
    private bool reticleShowsPlane = true;

    // Diagnostics
    private float nextDiagTime;
    private float trackingStartTime = -1f;
    private Vector3 camStartPosition;
    private bool camStartSet;
    private readonly List<string> setupNotes = new List<string>();
    private Material planeFallbackMaterial;

    // Shared resources
    private static Sprite[] splatSprites;
    private static Shader litShader;
    private static Shader unlitShader;
    private static bool shadersResolved;

    // =====================================================================
    // LIFECYCLE
    // =====================================================================

    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

        selectedColor = colors[0];
        ammo = magazineSize;

        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Screen.autorotateToPortrait = false;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.orientation = ScreenOrientation.AutoRotation;

        EnsureARSetup();
    }

    private void Start()
    {
        if (cam == null)
        {
            Debug.LogError("[ARBlaster] AR Camera not found. Attach this script to the AR Camera.");
            return;
        }

        camStartPosition = cam.transform.position;

        BuildReticle();
        BuildGun();
        BuildUI();
        RefreshStatus();

        foreach (string note in setupNotes)
            Debug.LogWarning("[ARBlaster] " + note);
    }

    private void Update()
    {
        if (cam == null) return;

        UpdateSurfaceReticle();
        UpdateDiagnostics();
        UpdateGunKick();

        Vector2 tapPosition;
        if (PollTap(out tapPosition) && !IsOverUI(tapPosition))
            TryPlaceTarget(tapPosition);
    }

    private void OnDestroy()
    {
        if (planeManager != null)
            planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
    }

    // =====================================================================
    // AR SELF-REPAIR
    // =====================================================================

    private void EnsureARSetup()
    {
        // 1. AR Session + Input Manager
        arSession = FindFirstObjectByType<ARSession>();
        if (arSession == null)
        {
            arSession = new GameObject("AR Session").AddComponent<ARSession>();
            setupNotes.Add("AR Session was missing - created one.");
        }
        arSession.enabled = true;

        if (FindFirstObjectByType<ARInputManager>() == null)
        {
            arSession.gameObject.AddComponent<ARInputManager>();
            setupNotes.Add("AR Input Manager was missing - added one.");
        }

        // 2. XR Origin
        xrOrigin = FindFirstObjectByType<XROrigin>();
        if (xrOrigin == null)
        {
            setupNotes.Add("XR Origin NOT FOUND. Add GameObject > XR > XR Origin (Mobile AR).");
            return;
        }
        trackablesParent = xrOrigin.TrackablesParent;

        // 3. Raycast manager
        if (raycastManager == null) raycastManager = FindFirstObjectByType<ARRaycastManager>();
        if (raycastManager == null)
        {
            raycastManager = xrOrigin.gameObject.AddComponent<ARRaycastManager>();
            setupNotes.Add("AR Raycast Manager was missing - added one.");
        }
        raycastManager.enabled = true;

        // 4. Plane manager - force detection ON
        if (planeManager == null) planeManager = FindFirstObjectByType<ARPlaneManager>();
        if (planeManager == null)
        {
            planeManager = xrOrigin.gameObject.AddComponent<ARPlaneManager>();
            setupNotes.Add("AR Plane Manager was missing - added one.");
        }
        planeManager.enabled = true;
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
        planeManager.trackablesChanged.AddListener(OnPlanesChanged);

        // 5. Camera components
        if (cam != null)
        {
            ARCameraManager cameraManager = cam.GetComponent<ARCameraManager>();
            if (cameraManager == null)
            {
                setupNotes.Add("AR Camera Manager missing on the AR Camera - camera feed/tracking may fail.");
            }
            else
            {
                cameraManager.enabled = true;
                cameraManager.requestedFacingDirection = CameraFacingDirection.World;
                cameraManager.autoFocusRequested = true;
            }

#if ENABLE_INPUT_SYSTEM
            // 6. Camera must follow the phone. If no Tracked Pose Driver exists, add a fallback.
            bool hasPoseDriver = false;
            foreach (Component c in cam.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name.Contains("TrackedPoseDriver"))
                    hasPoseDriver = true;
            }

            if (!hasPoseDriver && cam.GetComponent<ARPoseFallback>() == null)
            {
                cam.gameObject.AddComponent<ARPoseFallback>();
                setupNotes.Add("No Tracked Pose Driver on AR Camera - added fallback pose driver.");
            }
#endif
        }
    }

    // If the Plane Prefab slot is empty, planes exist but are invisible. Give them a visual.
    private void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
    {
        if (planeManager == null || planeManager.planePrefab != null) return;

        foreach (ARPlane plane in args.added)
        {
            if (plane == null) continue;
            GameObject go = plane.gameObject;
            if (go.GetComponent<ARPlaneMeshVisualizer>() != null) continue;

            if (planeFallbackMaterial == null)
            {
                Shader s = Shader.Find("Sprites/Default");
                if (s != null)
                {
                    planeFallbackMaterial = new Material(s);
                    planeFallbackMaterial.color = new Color(0.2f, 0.9f, 1f, 0.25f);
                }
            }

            go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            if (planeFallbackMaterial != null) mr.sharedMaterial = planeFallbackMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.AddComponent<ARPlaneMeshVisualizer>();
        }
    }

    // =====================================================================
    // INPUT (new Input System, legacy, or both)
    // =====================================================================

    private bool PollTap(out Vector2 position)
    {
        position = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame)
        {
            position = touchscreen.primaryTouch.position.ReadValue();
            return true;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            position = mouse.position.ReadValue();
            return true;
        }
        return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                position = touch.position;
                return true;
            }
            return false;
        }

        if (Input.GetMouseButtonDown(0))
        {
            position = Input.mousePosition;
            return true;
        }
        return false;
#else
        return false;
#endif
    }

    // Own rectangle test: reliable on the very first frame of a touch.
    private bool IsOverUI(Vector2 screenPosition)
    {
        for (int i = 0; i < uiBlockers.Count; i++)
        {
            RectTransform rt = uiBlockers[i];
            if (rt != null && rt.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(rt, screenPosition, null))
                return true;
        }
        return false;
    }

    // =====================================================================
    // SURFACE QUERY (plane -> feature point)
    // =====================================================================

    private bool TryGetSurface(Vector2 screenPosition, out Vector3 point, out Vector3 normal, out bool isPlane)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        isPlane = false;

        if (raycastManager == null || !raycastManager.enabled || cam == null)
            return false;

        Hits.Clear();
        if (!raycastManager.Raycast(screenPosition, Hits, TrackableType.AllTypes) || Hits.Count == 0)
            return false;

        int pick = -1;
        for (int i = 0; i < Hits.Count; i++)
        {
            if ((Hits[i].hitType & TrackableType.Planes) != 0)
            {
                pick = i;
                break;
            }
        }
        if (pick < 0) pick = 0;

        ARRaycastHit hit = Hits[pick];
        point = hit.pose.position;
        isPlane = (hit.hitType & TrackableType.Planes) != 0;

        Vector3 toCamera = cam.transform.position - point;
        normal = isPlane ? hit.pose.up : toCamera.normalized;
        if (Vector3.Dot(normal, toCamera) < 0f) normal = -normal;
        return true;
    }

    // =====================================================================
    // RETICLE
    // =====================================================================

    private void BuildReticle()
    {
        surfaceReticle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        surfaceReticle.name = "Surface Indicator";
        surfaceReticle.transform.localScale = new Vector3(0.18f, 0.004f, 0.18f);

        Collider col = surfaceReticle.GetComponent<Collider>();
        if (col != null) Destroy(col);

        reticlePlaneMaterial = CreateMaterial(new Color(1f, 0.9f, 0.05f), false);
        reticlePointMaterial = CreateMaterial(new Color(1f, 0.45f, 0.05f), false);

        Renderer r = surfaceReticle.GetComponent<Renderer>();
        if (reticlePlaneMaterial != null) r.sharedMaterial = reticlePlaneMaterial;
        surfaceReticle.SetActive(false);
    }

    private void UpdateSurfaceReticle()
    {
        if (surfaceReticle == null) return;

        Vector3 point, normal;
        bool isPlane;
        Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        if (TryGetSurface(center, out point, out normal, out isPlane))
        {
            surfaceReticle.transform.position = point + normal * 0.006f;
            surfaceReticle.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal);

            if (isPlane != reticleShowsPlane)
            {
                reticleShowsPlane = isPlane;
                Material m = isPlane ? reticlePlaneMaterial : reticlePointMaterial;
                if (m != null) surfaceReticle.GetComponent<Renderer>().sharedMaterial = m;
            }
            surfaceReticle.SetActive(true);
        }
        else
        {
            surfaceReticle.SetActive(false);
        }
    }

    // =====================================================================
    // TARGET PLACEMENT
    // =====================================================================

    private void TryPlaceTarget(Vector2 screenPosition)
    {
        Vector3 point, normal;
        bool isPlane;

        if (TryGetSurface(screenPosition, out point, out normal, out isPlane))
        {
            PlaceTargetBoard(point, normal);
            SetHint(isPlane
                ? "TARGET PLACED! Pick a colour and press SHOOT."
                : "TARGET PLACED on an estimated surface. Keep scanning for better tracking.");
        }
        else
        {
            PlaceTargetInFront();
        }
    }

    public void PlaceAtCrosshair()
    {
        if (cam == null) return;
        TryPlaceTarget(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
    }

    private void PlaceTargetInFront()
    {
        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 point = cam.transform.position + forward * 2f;
        point.y = cam.transform.position.y - 1.3f;

        PlaceTargetBoard(point, Vector3.up);
        SetHint("NO SURFACE FOUND - target placed 2 m ahead. Move the phone slowly over a textured surface.");
    }

    private void DestroyTargets()
    {
        foreach (GameObject t in targets)
            if (t != null) Destroy(t);
        targets.Clear();
    }

    private void PlaceTargetBoard(Vector3 surfacePoint, Vector3 surfaceNormal)
    {
        DestroyTargets();

        bool horizontal = surfaceNormal.y > 0.7f;
        Vector3 center = horizontal ? surfacePoint + Vector3.up * targetHeight : surfacePoint;

        Vector3 toCamera = cam.transform.position - center;
        toCamera.y = 0f;
        if (toCamera.sqrMagnitude < 0.001f) toCamera = Vector3.forward;

        GameObject board = new GameObject("AR Bullseye Target");
        board.transform.SetPositionAndRotation(center, Quaternion.LookRotation(toCamera.normalized, Vector3.up));

        // Anchored in tracking space (NOT parented to a plane: planes get merged/destroyed by ARCore).
        if (trackablesParent != null)
            board.transform.SetParent(trackablesParent, true);

        // Thin collider whose front face sits just in front of all rings.
        BoxCollider box = board.AddComponent<BoxCollider>();
        box.size = new Vector3(targetWidth, targetBoardHeight, 0.024f);
        box.center = new Vector3(0f, 0f, 0.004f);

        CreateBoardPart(board.transform, "Backboard",
            new Vector3(0f, 0f, -0.025f),
            new Vector3(targetWidth, targetBoardHeight, 0.05f),
            new Color(0.12f, 0.15f, 0.2f));

        CreateDisc(board.transform, "Ring Red", 0.78f, 0.003f, new Color(0.95f, 0.12f, 0.12f));
        CreateDisc(board.transform, "Ring Yellow", 0.60f, 0.006f, new Color(1f, 0.78f, 0.05f));
        CreateDisc(board.transform, "Ring Blue", 0.42f, 0.009f, new Color(0.08f, 0.42f, 1f));
        CreateDisc(board.transform, "Ring Green", 0.25f, 0.012f, new Color(0.08f, 0.85f, 0.28f));
        CreateDisc(board.transform, "Bullseye", 0.11f, 0.015f, Color.white);

        if (horizontal)
        {
            float supportHeight = Mathf.Max(0.05f, targetHeight - targetBoardHeight * 0.5f);
            Color metal = new Color(0.18f, 0.2f, 0.24f);

            CreateBoardPart(board.transform, "Support",
                new Vector3(0f, -(targetBoardHeight * 0.5f + supportHeight * 0.5f), -0.06f),
                new Vector3(0.07f, supportHeight, 0.07f), metal);

            CreateBoardPart(board.transform, "Base",
                new Vector3(0f, -targetHeight + 0.0175f, -0.06f),
                new Vector3(0.55f, 0.035f, 0.35f), metal);
        }

        board.AddComponent<ARBlasterTargetMarker>();
        targets.Add(board);
    }

    private void CreateDisc(Transform parent, string partName, float diameter, float localZ, Color color)
    {
        GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = partName;
        disc.transform.SetParent(parent, false);
        disc.transform.localPosition = new Vector3(0f, 0f, localZ);
        disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        disc.transform.localScale = new Vector3(diameter, 0.001f, diameter);

        Collider col = disc.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Material m = CreateMaterial(color, false);
        if (m != null) disc.GetComponent<Renderer>().sharedMaterial = m;
    }

    private void CreateBoardPart(Transform parent, string partName, Vector3 localPosition, Vector3 localScale, Color color)
    {
        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        part.name = partName;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = localScale;

        Collider col = part.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Material m = CreateMaterial(color, false);
        if (m != null) part.GetComponent<Renderer>().sharedMaterial = m;
    }

    // =====================================================================
    // SHOOTING
    // =====================================================================

    public void Shoot()
    {
        if (cam == null || reloading) return;

        if (ammo <= 0)
        {
            SetHint("OUT OF AMMO! Press RELOAD.");
            return;
        }

        ammo--;
        gunKick = 0.04f;
        RefreshStatus();

        Vector3 start = gunMuzzle != null
            ? gunMuzzle.position
            : cam.transform.position + cam.transform.forward * 0.4f;

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        // 1) Target board (scored)
        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, maxShootDistance))
        {
            ARBlasterTargetMarker marker = hit.collider.GetComponentInParent<ARBlasterTargetMarker>();
            if (marker != null)
            {
                string label;
                int points = ScoreFor(marker.transform, hit.point, out label);
                score += points;
                RefreshStatus();
                SetHint(label + " +" + points);

                StartCoroutine(FlyProjectile(start, hit.point, hit.normal, marker.transform, selectedColor, true));
                return;
            }
        }

        // 2) Real-world surface (paint it)
        Vector3 point, normal;
        bool isPlane;
        Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        if (TryGetSurface(center, out point, out normal, out isPlane))
        {
            SetHint("PAINTED THE SURFACE!");
            StartCoroutine(FlyProjectile(start, point, normal, trackablesParent, selectedColor, true));
            return;
        }

        // 3) Miss
        SetHint(targets.Count == 0
            ? "Nothing to hit yet. Press PLACE TARGET or tap a surface."
            : "MISSED! Aim the + at the target board.");
        StartCoroutine(FlyProjectile(start, ray.origin + ray.direction * 8f, Vector3.zero, null, selectedColor, false));
    }

    private int ScoreFor(Transform board, Vector3 worldPoint, out string label)
    {
        Vector3 local = board.InverseTransformPoint(worldPoint);
        float r = new Vector2(local.x, local.y).magnitude;

        if (r <= 0.055f) { label = "BULLSEYE!"; return 50; }
        if (r <= 0.125f) { label = "GREEN RING!"; return 30; }
        if (r <= 0.21f) { label = "BLUE RING!"; return 20; }
        if (r <= 0.30f) { label = "YELLOW RING!"; return 15; }
        if (r <= 0.39f) { label = "RED RING!"; return 10; }
        label = "BOARD HIT!";
        return 5;
    }

    private IEnumerator FlyProjectile(Vector3 start, Vector3 end, Vector3 normal, Transform markParent, Color color, bool leaveMark)
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "Paint Projectile";
        ball.transform.position = start;
        ball.transform.localScale = Vector3.one * 0.05f;

        Collider col = ball.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Material m = CreateMaterial(color, false);
        if (m != null) ball.GetComponent<Renderer>().sharedMaterial = m;

        float distance = Vector3.Distance(start, end);
        float duration = Mathf.Clamp(distance / projectileSpeed, 0.06f, 0.5f);
        float t = 0f;

        while (t < 1f && ball != null)
        {
            t += Time.deltaTime / duration;
            ball.transform.position = Vector3.Lerp(start, end, Mathf.Clamp01(t));
            yield return null;
        }

        if (ball != null) Destroy(ball);
        if (m != null) Destroy(m);

        if (leaveMark && normal != Vector3.zero)
            CreatePaintMark(end, normal, markParent, color);
    }

    private void CreatePaintMark(Vector3 position, Vector3 normal, Transform parent, Color color)
    {
        EnsureSplatSprites();

        GameObject mark = new GameObject("Paint Splat");
        mark.transform.SetPositionAndRotation(
            position + normal * 0.004f,
            Quaternion.LookRotation(-normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up) *
            Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));

        if (parent != null) mark.transform.SetParent(parent, true);
        else if (trackablesParent != null) mark.transform.SetParent(trackablesParent, true);

        float size = paintMarkSize * 2.6f * Random.Range(0.85f, 1.2f);
        mark.transform.localScale = Vector3.one * size;

        SpriteRenderer sr = mark.AddComponent<SpriteRenderer>();
        sr.sprite = splatSprites[Random.Range(0, splatSprites.Length)];
        sr.color = color;

        paintMarks.Add(mark);

        while (paintMarks.Count > maxPaintMarks)
        {
            if (paintMarks[0] != null) Destroy(paintMarks[0]);
            paintMarks.RemoveAt(0);
        }
    }

    // =====================================================================
    // PROCEDURAL SPLAT SPRITES
    // =====================================================================

    private static void EnsureSplatSprites()
    {
        if (splatSprites != null) return;
        splatSprites = new Sprite[5];
        for (int i = 0; i < splatSprites.Length; i++)
            splatSprites[i] = MakeSplat(1000 + i * 37);
    }

    private static Sprite MakeSplat(int seed)
    {
        const int S = 96;
        System.Random rng = new System.Random(seed);

        float baseRadius = 0.36f + (float)rng.NextDouble() * 0.06f;
        int lobes = 5;
        float[] amp = new float[lobes];
        float[] freq = new float[lobes];
        float[] phase = new float[lobes];
        for (int i = 0; i < lobes; i++)
        {
            amp[i] = 0.05f / (i + 1);
            freq[i] = 2 + i * 2 + rng.Next(0, 2);
            phase[i] = (float)rng.NextDouble() * 6.283f;
        }

        int spikeCount = rng.Next(5, 9);
        float spikePhase = (float)rng.NextDouble() * 6.283f;

        int dropCount = 12;
        float[] dx = new float[dropCount];
        float[] dy = new float[dropCount];
        float[] dr = new float[dropCount];
        for (int i = 0; i < dropCount; i++)
        {
            float a = (float)rng.NextDouble() * 6.283f;
            float d = 0.55f + (float)rng.NextDouble() * 0.35f;
            dx[i] = Mathf.Cos(a) * d;
            dy[i] = Mathf.Sin(a) * d;
            dr[i] = 0.02f + (float)rng.NextDouble() * 0.045f;
        }

        Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] pixels = new Color32[S * S];

        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float fx = (x + 0.5f) / S * 2f - 1f;
                float fy = (y + 0.5f) / S * 2f - 1f;
                float r = Mathf.Sqrt(fx * fx + fy * fy);
                float ang = Mathf.Atan2(fy, fx);

                float edge = baseRadius;
                for (int i = 0; i < lobes; i++)
                    edge += amp[i] * Mathf.Sin(freq[i] * ang + phase[i]);
                edge += 0.2f * Mathf.Pow(Mathf.Max(0f, Mathf.Sin(spikeCount * ang + spikePhase)), 8f);

                float alpha = Mathf.Clamp01((edge - r) * 30f);

                for (int i = 0; i < dropCount; i++)
                {
                    float ddx = fx - dx[i];
                    float ddy = fy - dy[i];
                    float dist = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                    alpha = Mathf.Max(alpha, Mathf.Clamp01((dr[i] - dist) * 40f));
                }

                pixels[y * S + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
    }

    // =====================================================================
    // VIRTUAL PAINT GUN
    // =====================================================================

    private void BuildGun()
    {
        if (cam == null) return;

        gunRoot = new GameObject("Virtual Paint Blaster").transform;
        gunRoot.SetParent(cam.transform, false);
        gunRestPosition = new Vector3(0.17f, -0.16f, 0.38f);
        gunRoot.localPosition = gunRestPosition;
        gunRoot.localRotation = Quaternion.Euler(2f, -6f, 0f);

        GunPart(PrimitiveType.Cube, "Frame", new Vector3(0f, 0f, 0f),
            new Vector3(0.06f, 0.07f, 0.20f), new Color(0.12f, 0.15f, 0.22f), Quaternion.identity);

        GunPart(PrimitiveType.Cube, "Top Stripe", new Vector3(0f, 0.037f, 0.01f),
            new Vector3(0.03f, 0.008f, 0.14f), new Color(1f, 0.55f, 0.1f), Quaternion.identity);

        GunPart(PrimitiveType.Cylinder, "Barrel", new Vector3(0f, 0.008f, 0.17f),
            new Vector3(0.034f, 0.07f, 0.034f), new Color(0.12f, 0.72f, 1f), Quaternion.Euler(90f, 0f, 0f));

        GunPart(PrimitiveType.Cube, "Grip", new Vector3(0f, -0.075f, -0.06f),
            new Vector3(0.045f, 0.09f, 0.05f), new Color(0.22f, 0.25f, 0.3f), Quaternion.Euler(-15f, 0f, 0f));

        gunColorPart = GunPart(PrimitiveType.Sphere, "Paint Chamber", new Vector3(0f, 0.075f, -0.02f),
            new Vector3(0.075f, 0.075f, 0.075f), selectedColor, Quaternion.identity);

        gunMuzzle = new GameObject("Muzzle").transform;
        gunMuzzle.SetParent(gunRoot, false);
        gunMuzzle.localPosition = new Vector3(0f, 0.008f, 0.26f);
    }

    private Transform GunPart(PrimitiveType type, string partName, Vector3 position, Vector3 scale, Color color, Quaternion rotation)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        part.name = partName;
        part.transform.SetParent(gunRoot, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        part.transform.localRotation = rotation;

        Collider col = part.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Material m = CreateMaterial(color, true);
        if (m != null) part.GetComponent<Renderer>().sharedMaterial = m;
        return part.transform;
    }

    private void UpdateGunKick()
    {
        if (gunRoot == null) return;
        gunKick = Mathf.Lerp(gunKick, 0f, Time.deltaTime * 14f);
        float sway = Mathf.Sin(Time.time * 1.6f) * 0.003f;
        gunRoot.localPosition = gunRestPosition + new Vector3(0f, sway, -gunKick);
    }

    // =====================================================================
    // MATERIALS
    // =====================================================================

    private static void ResolveShaders()
    {
        if (shadersResolved) return;
        shadersResolved = true;

        litShader = Shader.Find("Universal Render Pipeline/Lit");
        unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");
        if (unlitShader == null) unlitShader = Shader.Find("Standard");
        if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");
        if (litShader == null) litShader = unlitShader;
    }

    private Material CreateMaterial(Color color, bool lit)
    {
        ResolveShaders();
        Shader shader = lit ? litShader : unlitShader;
        if (shader == null)
        {
            Debug.LogError("[ARBlaster] No compatible shader found.");
            return null;
        }

        Material material = new Material(shader);
        SetMaterialColor(material, color);
        return material;
    }

    private static void SetMaterialColor(Material material, Color color)
    {
        if (material == null) return;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
    }

    // =====================================================================
    // DIAGNOSTICS HUD
    // =====================================================================

    private void UpdateDiagnostics()
    {
        if (diagText == null || Time.unscaledTime < nextDiagTime) return;
        nextDiagTime = Time.unscaledTime + 0.4f;

        ARSessionState state = ARSession.state;
        int planes = planeManager != null ? planeManager.trackables.count : -1;

        if (state == ARSessionState.SessionTracking && trackingStartTime < 0f)
            trackingStartTime = Time.time;

        if (cam != null && !camStartSet && state == ARSessionState.SessionTracking)
        {
            camStartPosition = cam.transform.position;
            camStartSet = true;
        }

        float moved = cam != null ? Vector3.Distance(cam.transform.position, camStartPosition) : 0f;

        string reason = state == ARSessionState.SessionTracking
            ? ARSession.notTrackingReason.ToString()
            : "-";

        string text =
            "AR: " + state +
            "\nPLANES: " + planes + "   LOST-REASON: " + reason +
            "\nCAMERA MOVED: " + moved.ToString("0.00") + " m";

        if (state == ARSessionState.Unsupported)
            text += "\nThis phone/ARCore is NOT supported.";
        else if (state == ARSessionState.NeedsInstall)
            text += "\nInstall 'Google Play Services for AR' from Play Store.";
        else if (state == ARSessionState.SessionTracking && planes == 0 &&
                 trackingStartTime > 0f && Time.time - trackingStartTime > 8f)
            text += "\nTIP: sweep phone slowly side-to-side over a TEXTURED surface (rug, table with objects).";
        else if (setupNotes.Count > 0)
            text += "\n" + setupNotes[0];

        diagText.text = text;
    }

    // =====================================================================
    // MOBILE UI
    // =====================================================================

    private Font GetFont()
    {
        Font f = null;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        catch (System.Exception) { }
        if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 16);
        return f;
    }

    private void EnsureEventSystem()
    {
        EventSystem es = FindFirstObjectByType<EventSystem>();
        if (es == null)
            es = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();

#if ENABLE_INPUT_SYSTEM
        StandaloneInputModule legacy = es.GetComponent<StandaloneInputModule>();
        if (legacy != null) Destroy(legacy);

        if (es.GetComponent<InputSystemUIInputModule>() == null)
        {
            InputSystemUIInputModule module = es.gameObject.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }
#else
        if (es.GetComponent<StandaloneInputModule>() == null)
            es.gameObject.AddComponent<StandaloneInputModule>();
#endif
    }

    private void BuildUI()
    {
        uiFont = GetFont();
        EnsureEventSystem();

        GameObject canvasGO = new GameObject("AR Color Blaster HUD",
            typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        Canvas canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        // Safe area container (avoids camera hole / notch / rounded corners).
        GameObject safeGO = new GameObject("SafeArea", typeof(RectTransform));
        RectTransform safe = safeGO.GetComponent<RectTransform>();
        safe.SetParent(canvasGO.transform, false);
        Rect sa = Screen.safeArea;
        safe.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
        safe.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
        safe.offsetMin = Vector2.zero;
        safe.offsetMax = Vector2.zero;

        Vector2 topLeft = new Vector2(0f, 1f);
        Vector2 topRight = new Vector2(1f, 1f);
        Vector2 topCenter = new Vector2(0.5f, 1f);
        Vector2 bottomLeft = new Vector2(0f, 0f);
        Vector2 bottomRight = new Vector2(1f, 0f);
        Vector2 bottomCenter = new Vector2(0.5f, 0f);
        Vector2 middle = new Vector2(0.5f, 0.5f);

        scoreText = MakeText(safe, "SCORE: 0", 34, topLeft, new Vector2(150f, -45f),
            new Vector2(280f, 60f), TextAnchor.MiddleLeft);
        ammoText = MakeText(safe, "AMMO: " + ammo, 34, topRight, new Vector2(-150f, -45f),
            new Vector2(280f, 60f), TextAnchor.MiddleRight);

        hintText = MakePanelText(safe, "Scan a surface, then press PLACE TARGET or tap it.", 24,
            topCenter, new Vector2(0f, -60f), new Vector2(1000f, 70f), TextAnchor.MiddleCenter);

        diagText = MakePanelText(safe, "AR: starting...", 20,
            topLeft, new Vector2(300f, -165f), new Vector2(560f, 120f), TextAnchor.UpperLeft);

        MakeText(safe, "+", 52, middle, Vector2.zero, new Vector2(80f, 80f), TextAnchor.MiddleCenter);

        // Colour palette
        MakeText(safe, "PAINT COLOUR", 22, bottomLeft, new Vector2(335f, 150f),
            new Vector2(500f, 40f), TextAnchor.MiddleCenter);

        for (int i = 0; i < colors.Length; i++)
        {
            int index = i;
            Button b = MakeButton(safe, "Swatch" + i, string.Empty, bottomLeft,
                new Vector2(65f + i * 95f, 85f), new Vector2(80f, 80f), colors[i], 20,
                delegate { SelectColor(index); });
            b.transition = Selectable.Transition.None;

            Outline outline = b.gameObject.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(6f, -6f);
            outline.enabled = (i == 0);
            swatchOutlines.Add(outline);
        }

        MakeButton(safe, "CLEAR", "CLEAR", bottomLeft, new Vector2(110f, 230f), new Vector2(170f, 64f),
            new Color(0.65f, 0.15f, 0.15f), 24, ClearAll);

        // Right side
        MakeButton(safe, "SHOOT", "SHOOT", bottomRight, new Vector2(-170f, 120f), new Vector2(260f, 130f),
            new Color(0.04f, 0.38f, 0.9f), 38, Shoot);

        MakeButton(safe, "RELOAD", "RELOAD", bottomRight, new Vector2(-170f, 265f), new Vector2(200f, 70f),
            new Color(0.1f, 0.55f, 0.27f), 26, Reload);

        MakeButton(safe, "PLACE", "PLACE TARGET", bottomCenter, new Vector2(0f, 75f), new Vector2(320f, 90f),
            new Color(0.85f, 0.55f, 0.05f), 28, PlaceAtCrosshair);
    }

    private RectTransform MakeRect(string objectName, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        return rt;
    }

    private Text AddText(RectTransform parent, string value, int fontSize, TextAnchor align)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(10f, 4f);
        rt.offsetMax = new Vector2(-10f, -4f);

        Text t = go.AddComponent<Text>();
        t.font = uiFont;
        t.text = value;
        t.fontSize = fontSize;
        t.fontStyle = FontStyle.Bold;
        t.color = Color.white;
        t.alignment = align;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;

        Outline o = go.AddComponent<Outline>();
        o.effectColor = new Color(0f, 0f, 0f, 0.85f);
        o.effectDistance = new Vector2(2f, -2f);
        return t;
    }

    private Text MakeText(Transform parent, string value, int fontSize, Vector2 anchor,
        Vector2 position, Vector2 size, TextAnchor align)
    {
        RectTransform rt = MakeRect("Text", parent, anchor, position, size);
        return AddText(rt, value, fontSize, align);
    }

    private Text MakePanelText(Transform parent, string value, int fontSize, Vector2 anchor,
        Vector2 position, Vector2 size, TextAnchor align)
    {
        RectTransform rt = MakeRect("Panel", parent, anchor, position, size);
        Image bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.5f);
        bg.raycastTarget = false;
        return AddText(rt, value, fontSize, align);
    }

    private Button MakeButton(Transform parent, string objectName, string label, Vector2 anchor,
        Vector2 position, Vector2 size, Color color, int fontSize, UnityEngine.Events.UnityAction action)
    {
        RectTransform rt = MakeRect(objectName, parent, anchor, position, size);
        Image image = rt.gameObject.AddComponent<Image>();
        image.color = color;

        Button button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(action);

        if (!string.IsNullOrEmpty(label))
            AddText(rt, label, fontSize, TextAnchor.MiddleCenter);

        uiBlockers.Add(rt);
        return button;
    }

    // =====================================================================
    // COLOUR / RELOAD / CLEAR / STATUS
    // =====================================================================

    private void SelectColor(int index)
    {
        selectedColor = colors[index];

        if (gunColorPart != null)
        {
            Renderer r = gunColorPart.GetComponent<Renderer>();
            if (r != null) SetMaterialColor(r.sharedMaterial, selectedColor);
        }

        for (int i = 0; i < swatchOutlines.Count; i++)
            if (swatchOutlines[i] != null) swatchOutlines[i].enabled = (i == index);

        SetHint("Colour selected! Aim the + and press SHOOT.");
    }

    private void Reload()
    {
        if (!reloading) StartCoroutine(ReloadRoutine());
    }

    private IEnumerator ReloadRoutine()
    {
        reloading = true;
        SetHint("RELOADING...");
        yield return new WaitForSeconds(1.1f);
        ammo = magazineSize;
        reloading = false;
        RefreshStatus();
        SetHint("RELOAD COMPLETE!");
    }

    private void ClearAll()
    {
        foreach (GameObject mark in paintMarks)
            if (mark != null) Destroy(mark);
        paintMarks.Clear();

        DestroyTargets();

        score = 0;
        RefreshStatus();
        SetHint("Target and paint cleared.");
    }

    private void SetHint(string message)
    {
        if (hintText != null) hintText.text = message;
    }

    private void RefreshStatus()
    {
        if (scoreText != null) scoreText.text = "SCORE: " + score;
        if (ammoText != null) ammoText.text = "AMMO: " + ammo;
    }
}

public class ARBlasterTargetMarker : MonoBehaviour
{
}

#if ENABLE_INPUT_SYSTEM
/// <summary>
/// Fallback camera pose driver. Only added when the AR Camera has no Tracked Pose Driver.
/// Reads the ARCore device pose from the Input System and applies it to the camera.
/// </summary>
public class ARPoseFallback : MonoBehaviour
{
    private Vector3Control positionControl;
    private QuaternionControl rotationControl;

    private void OnEnable() { Application.onBeforeRender += Apply; }
    private void OnDisable() { Application.onBeforeRender -= Apply; }
    private void Update() { Apply(); }

    private void Apply()
    {
        if (positionControl == null || rotationControl == null)
            FindControls();

        if (positionControl == null || rotationControl == null)
            return;

        transform.localPosition = positionControl.ReadValue();
        transform.localRotation = rotationControl.ReadValue();
    }

    private void FindControls()
    {
        foreach (InputDevice device in InputSystem.devices)
        {
            if (device.layout != "HandheldARInputDevice") continue;

            positionControl = device.TryGetChildControl<Vector3Control>("devicePosition");
            rotationControl = device.TryGetChildControl<QuaternionControl>("deviceRotation");
            if (positionControl != null && rotationControl != null) return;
        }
    }
}
#endif