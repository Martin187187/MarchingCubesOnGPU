using System;
using UnityEngine;

[RequireComponent(typeof(TerrainController))]
public class TerrainTool : MonoBehaviour
{
    [Header("Refs")]
    public TerrainController terrain;
    public Camera cam;

    [Header("Active Preset")]
    public TerrainToolPreset preset;

    [Header("Preset Hotkeys (optional)")]
    public TerrainToolPreset[] quickPresets;
    public KeyCode[] quickPresetKeys = { KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7 };

    [Header("Snapping (toggles + modifier key)")]
    public bool  snapPositionToggle    = false;
    public bool  snapYawToggle         = false;
    public float yawSnapStepDegrees    = 15f;
    public KeyCode snapModifierPrimary   = KeyCode.LeftAlt;
    public KeyCode snapModifierSecondary = KeyCode.RightAlt;
    public bool requireModifierForSnapping = true;

    [Header("Type-anchoring (toggle + optional modifier)")]
    public bool typeAnchorToggle = false;
    public KeyCode typeAnchorModifierPrimary   = KeyCode.LeftShift;
    public KeyCode typeAnchorModifierSecondary = KeyCode.RightShift;
    public bool requireModifierForTypeAnchor = true;

    [Header("Other Hotkeys")]
    public KeyCode keyToggleBrush   = KeyCode.B;
    public KeyCode keyTogglePosSnap = KeyCode.P;
    public KeyCode keyToggleYawSnap = KeyCode.R;
    public KeyCode keyToggleTypeAnchor = KeyCode.T;

    [Header("Scroll Scaling")]
    [Tooltip("Step applied per mouse wheel tick. Hold Shift for fine steps (step * fineMultiplier).")]
    public float scrollStep = 0.5f;
    public float fineMultiplier = 0.2f;

    // ----- internal -----
    private bool hasAnchor;
    private Vector3 anchorPos;
    private bool prevTypeAnchorActive;
    private TerrainType anchoredBreakType;

    private bool   yawLocked;
    private float  cachedYawDeg;

    private GameObject brushRoot, sphereVis, cubeVis;
    private bool brushVisible = true;

    // runtime-only, derived from preset; wheel edits these (never touches asset)
    private float currentRadius;
    private Vector3 currentBoxSize;

    void Awake()
    {
        if (!terrain) terrain = GetComponent<TerrainController>();
        if (!cam) cam = Camera.main;
        CreateBrushVisuals();
        PullSizesFromPreset();
        ApplyPresetVisuals();
    }

    void OnDestroy()
    {
        if (brushRoot) Destroy(brushRoot);
    }

    void Update()
    {
        HandleHotkeys();
        if (!cam || preset == null) { ForceBrushHidden(true); return; }

        // None → no indicator and no edits
        if (preset.operation == ToolOperation.None)
        {
            ForceBrushHidden(true);
            return;
        }

        // respect explicit hide flag too
        ForceBrushHidden(preset.hideBrush);

        if (!Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out var hit)) return;
        Vector3 hoverPos = hit.point;

        // Snapping
        (bool posSnap, bool yawSnap) = GetSnapActive(preset);
        Vector3 snappedHoverPos = posSnap ? terrain.SnapToGrid(hoverPos) : hoverPos;

        // Anchor lifecycle
        HandleAnchorLifecycle(preset, snappedHoverPos, yawSnap);

        // Type anchor lifecycle (Break only)
        bool typeAnchorActive = GetTypeAnchorActive(preset);
        if (typeAnchorActive && !prevTypeAnchorActive)
            anchoredBreakType = terrain.terrainType;
        prevTypeAnchorActive = typeAnchorActive;

        // Wheel scaling (runtime only, clamped to preset range)
        HandleScrollScale(preset);

        // Brush transform
        Vector3 brushPos = (preset.anchored && hasAnchor) ? anchorPos : snappedHoverPos;
        UpdateBrushVisualTransform(brushPos, yawSnap, preset);

        // Apply action while LMB held
        if (!Input.GetMouseButton(0)) return;
        ApplyOperation(preset, snappedHoverPos);
    }

    // ---------- behavior ----------
    void ApplyOperation(TerrainToolPreset p, Vector3 targetPos)
    {
        switch (p.operation)
        {
            case ToolOperation.Break:
            {
                if (p.shape == BrushShape.Sphere)
                {
                    Vector3 pos = (p.anchored && hasAnchor) ? anchorPos : targetPos;
                    if (GetTypeAnchorActive(p))
                        EditSphereAt(pos, p.strength, true, anchoredBreakType);
                    else
                        EditSphereAt(pos, p.strength, false, default);
                }
                else
                {
                    if (hasAnchor)
                        EditCubeAt(anchorPos, currentBoxSize, cachedYawDegOrLive(p), p.strength);
                }
                break;
            }

            case ToolOperation.Build:
            {
                if (p.shape == BrushShape.Sphere)
                {
                    Vector3 pos = (p.anchored && hasAnchor) ? anchorPos : targetPos;
                    EditSphereAt(pos, Mathf.Abs(p.strength), false, default);
                }
                else
                {
                    if (hasAnchor)
                        EditCubeAt(anchorPos, currentBoxSize, cachedYawDegOrLive(p), Mathf.Abs(p.strength));
                }
                break;
            }

            case ToolOperation.Smooth:
            {
                Vector3 pos = (p.anchored && hasAnchor) ? anchorPos : targetPos;
                terrain.SmoothSphere(pos, currentRadius, p.smoothStrength);
                break;
            }
        }
    }

    float cachedYawDegOrLive(TerrainToolPreset p)
    {
        if (p.lockYawWhileActive && yawLocked) return cachedYawDeg;
        float yawDeg = GetCameraYawDeg();
        bool yawSnapActive = GetSnapActive(p).yawSnap && yawSnapStepDegrees > 0.01f;
        if (yawSnapActive) yawDeg = SnapAngle(yawDeg, yawSnapStepDegrees);
        return NormalizeAngle360(yawDeg);
    }

    (bool posSnap, bool yawSnap) GetSnapActive(TerrainToolPreset p)
    {
        bool mod = IsSnapModifierHeld();
        bool ps = p.allowPositionSnap && (requireModifierForSnapping ? (snapPositionToggle && mod) : snapPositionToggle);
        bool ys = p.allowYawSnap      && (requireModifierForSnapping ? (snapYawToggle      && mod) : snapYawToggle);
        return (ps, ys);
    }

    bool GetTypeAnchorActive(TerrainToolPreset p)
    {
        if (p.operation != ToolOperation.Break) return false;
        if (!p.allowTypeAnchor) return false;
        bool mod = IsTypeAnchorModifierHeld();
        return requireModifierForTypeAnchor ? (typeAnchorToggle && mod) : typeAnchorToggle;
    }

    void HandleAnchorLifecycle(TerrainToolPreset p, Vector3 snappedHoverPos, bool yawSnapActive)
    {
        if (!p.anchored) { hasAnchor = false; yawLocked = false; return; }

        if (Input.GetMouseButtonDown(0))
        {
            anchorPos = snappedHoverPos;
            hasAnchor = true;

            if ((p.shape == BrushShape.Wall || p.shape == BrushShape.Floor) && p.lockYawWhileActive)
            {
                cachedYawDeg = GetCameraYawDeg();
                if (yawSnapActive && yawSnapStepDegrees > 0.01f)
                    cachedYawDeg = SnapAngle(cachedYawDeg, yawSnapStepDegrees);
                cachedYawDeg = NormalizeAngle360(cachedYawDeg);
                yawLocked = true;
            }
        }
        if (Input.GetMouseButtonUp(0))
        {
            hasAnchor = false;
            yawLocked = false;
        }
    }

    // Wheel resizing (runtime only)
    void HandleScrollScale(TerrainToolPreset p)
    {
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) < 1e-4f) return;

        float step = scrollStep * (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? fineMultiplier : 1f);
        float delta = scroll * step;

        if (p.shape == BrushShape.Sphere)
        {
            float minR = Mathf.Max(0.01f, Mathf.Min(p.sphereRadiusRange.x, p.sphereRadiusRange.y));
            float maxR = Mathf.Max(minR, Mathf.Max(p.sphereRadiusRange.x, p.sphereRadiusRange.y));
            currentRadius = Mathf.Clamp(currentRadius + delta, minR, maxR);
        }
        else
        {
            // multiplicative uniform scaling (mouse wheel)
            // delta is your "percent per tick": e.g. 0.1 => ±10% per tick (use Shift for fineMultiplier)
            float scaleFactor = 1f + delta;                  // delta from above (scroll * step)
            if (scaleFactor <= 0.0001f) scaleFactor = 0.0001f; // avoid flip/zero

            Vector3 target = currentBoxSize * scaleFactor;

            // clamp per axis to preset bounds
            target.x = Mathf.Clamp(target.x, p.boxSizeMin.x, p.boxSizeMax.x);
            target.y = Mathf.Clamp(target.y, p.boxSizeMin.y, p.boxSizeMax.y);
            target.z = Mathf.Clamp(target.z, p.boxSizeMin.z, p.boxSizeMax.z);

            // enforce positive minimum size
            target.x = Mathf.Max(0.01f, target.x);
            target.y = Mathf.Max(0.01f, target.y);
            target.z = Mathf.Max(0.01f, target.z);

            currentBoxSize = target;
        }


        ApplyPresetVisuals(); // refresh preview
    }

    void EditSphereAt(Vector3 pos, float strength, bool useTypeConstraint, TerrainType typeIfConstrained)
    {
        if (useTypeConstraint)
            terrain.EditSphere(pos, currentRadius, strength, typeIfConstrained, true);
        else
            terrain.EditSphere(pos, currentRadius, strength, preset.fillType);
    }

    void EditCubeAt(Vector3 pos, Vector3 size, float yawDeg, float strength)
    {
        Quaternion yawQ = Quaternion.Euler(0f, yawDeg, 0f);
        terrain.EditCube(pos, size, yawQ, strength, preset.fillType);
    }

    // ---------- visuals ----------
    void CreateBrushVisuals()
    {
        brushRoot = new GameObject("BrushVisual");
        brushRoot.transform.SetParent(null);

        sphereVis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereVis.name = "SphereVis";
        sphereVis.transform.SetParent(brushRoot.transform, false);
        var cs = sphereVis.GetComponent<Collider>(); if (cs) Destroy(cs);

        cubeVis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeVis.name = "CubeVis";
        cubeVis.transform.SetParent(brushRoot.transform, false);
        var cc = cubeVis.GetComponent<Collider>(); if (cc) Destroy(cc);

        SetBrushAlpha(0.25f);
    }

    void ApplyPresetVisuals()
    {
        if (!brushRoot || preset == null) return;

        bool noOp = preset.operation == ToolOperation.None;
        ForceBrushHidden(noOp || preset.hideBrush);

        bool useSphere = !noOp && preset.shape == BrushShape.Sphere;
        bool useCube   = !noOp && (preset.shape == BrushShape.Wall || preset.shape == BrushShape.Floor);

        if (sphereVis) sphereVis.SetActive(useSphere && brushVisible);
        if (cubeVis)   cubeVis.SetActive(useCube   && brushVisible);

        if (useSphere && sphereVis) sphereVis.transform.localScale = Vector3.one * currentRadius;
        if (useCube && cubeVis)     cubeVis.transform.localScale   = currentBoxSize;
    }

    void UpdateBrushVisualTransform(Vector3 pos, bool yawSnapActiveNow, TerrainToolPreset p)
    {
        if (!brushRoot) return;

        bool noOp = p.operation == ToolOperation.None;
        if (noOp || p.hideBrush) { ForceBrushHidden(true); return; }

        brushRoot.transform.position = pos;

        if (p.shape == BrushShape.Wall || p.shape == BrushShape.Floor)
        {
            if (cubeVis) cubeVis.transform.localScale = currentBoxSize;

            float yawDeg = (p.lockYawWhileActive && yawLocked) ? cachedYawDeg : GetCameraYawDeg();
            if (!(p.lockYawWhileActive && yawLocked) && yawSnapActiveNow && yawSnapStepDegrees > 0.01f)
                yawDeg = SnapAngle(yawDeg, yawSnapStepDegrees);
            brushRoot.transform.rotation = Quaternion.Euler(0f, NormalizeAngle360(yawDeg), 0f);
        }
        else
        {
            Vector3 toCam = cam.transform.position - pos; toCam.y = 0f;
            if (toCam.sqrMagnitude > 1e-6f)
                brushRoot.transform.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);

            if (sphereVis) sphereVis.transform.localScale = Vector3.one * currentRadius;
        }
    }

    void SetBrushAlpha(float a)
    {
        void MakeTransparent(MeshRenderer mr)
        {
            if (!mr || !mr.sharedMaterial) return;
            var mat = mr.sharedMaterial;
            if (!mat.HasProperty("_Color")) return;
            var c = mat.color; c.a = Mathf.Clamp01(a);
            mat.color = c;
            if (a < 0.999f)
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
        }
        MakeTransparent(sphereVis?.GetComponent<MeshRenderer>());
        MakeTransparent(cubeVis?.GetComponent<MeshRenderer>());
    }

    void ForceBrushHidden(bool hide) => brushRoot?.SetActive(!hide && brushVisible);

    // ---------- hotkeys / toggles ----------
    void HandleHotkeys()
    {
        // quick preset selection
        for (int i = 0; i < quickPresets.Length && i < quickPresetKeys.Length; i++)
        {
            if (quickPresets[i] != null && Input.GetKeyDown(quickPresetKeys[i]))
            {
                SetPreset(quickPresets[i]);
                break;
            }
        }

        if (Input.GetKeyDown(keyToggleBrush))
        {
            brushVisible = !brushVisible;
            ForceBrushHidden(preset == null || preset.hideBrush || !brushVisible || preset.operation == ToolOperation.None);
        }
        if (Input.GetKeyDown(keyTogglePosSnap)) snapPositionToggle = !snapPositionToggle;
        if (Input.GetKeyDown(keyToggleYawSnap)) snapYawToggle      = !snapYawToggle;

        if (Input.GetKeyDown(keyToggleTypeAnchor))
        {
            typeAnchorToggle = !typeAnchorToggle;
            if (typeAnchorToggle && !requireModifierForTypeAnchor &&
                preset != null && preset.operation == ToolOperation.Break && preset.allowTypeAnchor)
            {
                anchoredBreakType = terrain.terrainType;
            }
        }
    }

    public void SetPreset(TerrainToolPreset p)
    {
        preset = p;
        hasAnchor = false;
        yawLocked = false;
        PullSizesFromPreset();
        ApplyPresetVisuals();
    }

    void PullSizesFromPreset()
    {
        if (preset == null) return;

        float minR = Mathf.Max(0.01f, Mathf.Min(preset.sphereRadiusRange.x, preset.sphereRadiusRange.y));
        float maxR = Mathf.Max(minR, Mathf.Max(preset.sphereRadiusRange.x, preset.sphereRadiusRange.y));
        currentRadius = Mathf.Clamp(preset.sphereRadius, minR, maxR);

        currentBoxSize = new Vector3(
            Mathf.Clamp(preset.boxSize.x, preset.boxSizeMin.x, preset.boxSizeMax.x),
            Mathf.Clamp(preset.boxSize.y, preset.boxSizeMin.y, preset.boxSizeMax.y),
            Mathf.Clamp(preset.boxSize.z, preset.boxSizeMin.z, preset.boxSizeMax.z)
        );

        currentRadius = Mathf.Max(0.01f, currentRadius);
        currentBoxSize.x = Mathf.Max(0.01f, currentBoxSize.x);
        currentBoxSize.y = Mathf.Max(0.01f, currentBoxSize.y);
        currentBoxSize.z = Mathf.Max(0.01f, currentBoxSize.z);
    }

    bool IsSnapModifierHeld()
    {
        bool p = snapModifierPrimary   != KeyCode.None && Input.GetKey(snapModifierPrimary);
        bool s = snapModifierSecondary != KeyCode.None && Input.GetKey(snapModifierSecondary);
        return p || s;
    }

    bool IsTypeAnchorModifierHeld()
    {
        bool p = typeAnchorModifierPrimary   != KeyCode.None && Input.GetKey(typeAnchorModifierPrimary);
        bool s = typeAnchorModifierSecondary != KeyCode.None && Input.GetKey(typeAnchorModifierSecondary);
        return p || s;
    }

    float GetCameraYawDeg()
    {
        Vector3 f = cam.transform.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
        f.Normalize();
        return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    static float SnapAngle(float angleDeg, float stepDeg) => Mathf.Round(angleDeg / stepDeg) * stepDeg;
    static float NormalizeAngle360(float deg) { deg %= 360f; if (deg < 0f) deg += 360f; return deg; }

    // Optional UI hooks
    public void SetBrushVisible(bool vis)
    {
        brushVisible = vis;
        ForceBrushHidden(preset == null || preset.hideBrush || !brushVisible || preset.operation == ToolOperation.None);
    }
    public void SetSphereRadius(float r)  { currentRadius = Mathf.Max(0.01f, r); ApplyPresetVisuals(); }
    public void SetBoxSize(Vector3 s)
    {
        currentBoxSize = new(Mathf.Max(0.01f, s.x), Mathf.Max(0.01f, s.y), Mathf.Max(0.01f, s.z));
        ApplyPresetVisuals();
    }
}
