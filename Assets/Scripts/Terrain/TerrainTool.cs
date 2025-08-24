using System.Collections.Generic;
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

    [Header("Raycast")]
    [Tooltip("Only these layers will be hit by the cursor ray. Defaults to the 'Terrain' layer.")]
    public LayerMask terrainRaycastMask;

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

    // Charge/cooldown state
    private float _holdStartTime = -1f;
    private bool  _primedThisHold = false;

    // Per-preset next-ready time (cooldown)
    private readonly Dictionary<TerrainToolPreset, float> _nextReadyTime = new();

    // Target tracking (for reset-on-move)
    private Vector3 _lastTargetWorld;
    private Vector3 _prevTargetWorld;
    private bool    _hadPrevTarget;

    // *** CHANGED *** (use linear epsilon; compare squared distance to squared epsilon)
    private const float TARGET_MOVE_EPS_LINEAR = 0.01f; // ~1 cm

    // *** CHANGED *** Preview throttling to reduce heavy preview writes
    private Vector3 _lastPreviewPos;
    private float   _lastPreviewTime = -1f;
    private const float PREVIEW_MIN_INTERVAL = 1f / 30f; // cap preview writes at ~30 Hz
    private const float PREVIEW_MOVE_FRAC_OF_RADIUS = 0.02f; // 2% of radius

    void Awake()
    {
        if (!terrain) terrain = GetComponent<TerrainController>();
        if (!cam) cam = Camera.main;

        // Default to a layer named "Terrain" if mask not set in inspector
        if (terrainRaycastMask == 0)
        {
            int idx = LayerMask.NameToLayer("Terrain");
            terrainRaycastMask = idx >= 0 ? (1 << idx) : LayerMask.GetMask("Terrain");
        }

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
        if (!cam || !preset) { ForceBrushHidden(true); return; }
        if (preset.operation == ToolOperation.None) { ForceBrushHidden(true); return; }

        ForceBrushHidden(preset.hideBrush);

        // Raycast only against allowed layers
        if (!Physics.Raycast(
                cam.ScreenPointToRay(Input.mousePosition),
                out var hit,
                Mathf.Infinity,
                terrainRaycastMask,
                QueryTriggerInteraction.Ignore))
            return;

        Vector3 hoverPos = hit.point;

        // Snapping
        (bool posSnap, bool yawSnap) = GetSnapActive(preset);
        Vector3 snappedHoverPos = posSnap ? terrain.SnapToGrid(hoverPos) : hoverPos;

        // Determine the current target (respect anchoring)
        Vector3 currentTarget = (preset.anchored && hasAnchor) ? anchorPos : snappedHoverPos;

        // Detect target movement (post-snap/anchor). If it moved, reset charge state & optionally cooldown.
        if (_hadPrevTarget && (currentTarget - _prevTargetWorld).sqrMagnitude > TARGET_MOVE_EPS_LINEAR * TARGET_MOVE_EPS_LINEAR) // *** CHANGED ***
        {
            ResetProgressOnTargetChange(currentTarget, Input.GetMouseButton(0)); // *** CHANGED *** pass isHeld
        }
        _prevTargetWorld = currentTarget;
        _hadPrevTarget = true;

        _lastTargetWorld = currentTarget;

        // Anchor lifecycle
        HandleAnchorLifecycle(preset, snappedHoverPos, yawSnap);

        // Type anchor lifecycle (Break only)
        bool typeAnchorActive = GetTypeAnchorActive(preset);
        if (typeAnchorActive && !prevTypeAnchorActive)
            anchoredBreakType = terrain.terrainType;
        prevTypeAnchorActive = typeAnchorActive;

        // Wheel scaling
        HandleScrollScale(preset);

        // Brush transform
        Vector3 brushPos = currentTarget;
        UpdateBrushVisualTransform(brushPos, yawSnap, preset);

        // Input edges
        if (Input.GetMouseButtonDown(0))
        {
            _holdStartTime = Time.time;
            _primedThisHold = !preset.enableChargeHold; // if no charge required, already primed

            // *** CHANGED *** Initialize preview throttle anchor
            _lastPreviewPos = brushPos;
            _lastPreviewTime = Time.time;
        }
        if (Input.GetMouseButtonUp(0))
        {
            _holdStartTime = -1f;
            _primedThisHold = false;

            // Clear crack preview when released (sphere/break only)
            if (preset.operation == ToolOperation.Break && preset.shape == BrushShape.Sphere)
                SendCrackProgress(_lastTargetWorld, 0f);
        }

        // If not held, we stop here—no preview writes at all when idle. *** CHANGED ***
        if (!Input.GetMouseButton(0)) return;

        // --------- CRACK PREVIEW DURING CHARGE / COOLDOWN (SPHERE/BREAK ONLY) ----------
        if (preset.operation == ToolOperation.Break && preset.shape == BrushShape.Sphere)
        {
            Vector3 p = _lastTargetWorld;

            // Charging phase: show cracks; when done, PRIME and continue to fire path
            if (preset.enableChargeHold && !_primedThisHold)
            {
                float need = Mathf.Max(0.0001f, preset.chargeTimeSeconds);
                float elapsed = Time.time - _holdStartTime;
                if (elapsed >= need)
                {
                    _primedThisHold = true;          // prime after charge
                    ThrottledCrackPreview(p, 1f);    // *** CHANGED *** throttled
                }
                else
                {
                    float prog = Mathf.Clamp01(elapsed / need);
                    ThrottledCrackPreview(p, prog);  // *** CHANGED *** throttled
                    return; // still charging; don't attempt cooldown fire yet
                }
            }

            // After primed: if still on cooldown, show cracks based on cooldown progress to next shot
            if (!IsToolReady(preset))
            {
                float prog01 = GetCooldownProgress01();
                ThrottledCrackPreview(p, prog01);     // *** CHANGED *** throttled
                return; // waiting for cooldown
            }
        }

        // --------- FIRE (DESTRUCTIVE APPLY) ----------
        if (IsToolReady(preset))
        {
            ApplyOperation(preset, snappedHoverPos);
            BumpCooldown(preset);

            // Require full charge before the NEXT shot (even while still holding)
            if (preset.enableChargeHold)
            {
                _primedThisHold = false;
                _holdStartTime  = Time.time;
            }
        }
    }

    // Called when the target world position changes
    // *** CHANGED ***  No hover-only crack writes here; optionally reset only what’s necessary.
    void ResetProgressOnTargetChange(Vector3 newTarget, bool isHeld)
    {
        // No preview writes here (was: SendCrackProgress(newTarget, 0f);)

        // Restart charging state only if mouse is held; otherwise just clear pending state
        if (isHeld)
        {
            _holdStartTime = Time.time;
            _primedThisHold = !preset.enableChargeHold;
        }
        else
        {
            _holdStartTime = -1f;
            _primedThisHold = false;
        }

        // Cooldown: optional reset on move. If this feels too punishing, remove this block.
        if (preset != null)
        {
            float cd = Mathf.Max(0f, preset.cooldownSeconds);
            if (cd > 0f)
            {
                float next = Time.time + cd;
                if (_nextReadyTime.ContainsKey(preset)) _nextReadyTime[preset] = next;
                else _nextReadyTime.Add(preset, next);
            }
            else
            {
                if (_nextReadyTime.ContainsKey(preset)) _nextReadyTime[preset] = Time.time;
            }
        }

        // *** CHANGED *** Reset preview throttle anchors on move to avoid a burst
        _lastPreviewPos = newTarget;
        _lastPreviewTime = Time.time;
    }

    // Preview-only cracks (no iso change): strength=0, forceReplace=true
    void SendCrackProgress(Vector3 worldPos, float progress01)
    {
        progress01 = Mathf.Clamp01(progress01);
        terrain.EditSphere(worldPos, currentRadius, 0f, preset.fillType, progress01, false, true);
    }

    // *** CHANGED *** Throttled preview writer
    void ThrottledCrackPreview(Vector3 worldPos, float progress01)
    {
        float now = Time.time;

        // movement threshold scales with radius (prevents thrash at tiny jitters)
        float moveThresh = Mathf.Max(0.002f, currentRadius * PREVIEW_MOVE_FRAC_OF_RADIUS);
        if ((worldPos - _lastPreviewPos).sqrMagnitude < moveThresh * moveThresh &&
            (now - _lastPreviewTime) < PREVIEW_MIN_INTERVAL)
        {
            return;
        }

        SendCrackProgress(worldPos, progress01);

        _lastPreviewPos = worldPos;
        _lastPreviewTime = now;
    }

    // ---------- cooldown helpers ----------
    bool IsToolReady(TerrainToolPreset p)
    {
        if (!_nextReadyTime.TryGetValue(p, out float readyAt)) return true;
        return Time.time >= readyAt;
    }

    void BumpCooldown(TerrainToolPreset p)
    {
        float cd = Mathf.Max(0f, p.cooldownSeconds);
        float next = Time.time + cd;
        if (_nextReadyTime.ContainsKey(p)) _nextReadyTime[p] = next;
        else _nextReadyTime.Add(p, next);
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

                    // Clear preview first so it doesn't mask the real edit this frame
                    SendCrackProgress(pos, 0f);

                    // Visual debris
                    PebbleSpawner.Instance?.SpawnPebbles(pos);

                    // REAL edit: progress=0, forceReplace=false (destroys / modifies voxels)
                    if (GetTypeAnchorActive(p))
                        terrain.EditSphere(pos, currentRadius, p.strength, anchoredBreakType, 0f, true,  false);
                    else
                        terrain.EditSphere(pos, currentRadius, p.strength, preset.fillType,     0f, false, false);
                }
                else
                {
                    if (hasAnchor)
                    {
                        float yaw = cachedYawDegOrLive(p);
                        EditCubeAt(anchorPos, currentBoxSize, yaw, p.strength);
                        PebbleSpawner.Instance?.SpawnPebbles(anchorPos);
                    }
                }
                break;
            }

            case ToolOperation.Build:
            {
                if (p.shape == BrushShape.Sphere)
                {
                    Vector3 pos = (preset.anchored && hasAnchor) ? anchorPos : targetPos;
                    // REAL build: progress=0, forceReplace=false
                    terrain.EditSphere(pos, currentRadius, Mathf.Abs(p.strength), preset.fillType, 0f, false, false);
                }
                else if (hasAnchor)
                {
                    EditCubeAt(anchorPos, currentBoxSize, cachedYawDegOrLive(p), Mathf.Abs(p.strength));
                }
                break;
            }

            case ToolOperation.Smooth:
            {
                Vector3 pos = (preset.anchored && hasAnchor) ? anchorPos : targetPos;
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
            float scaleFactor = 1f + delta;
            if (scaleFactor <= 0.0001f) scaleFactor = 0.0001f;

            Vector3 target = currentBoxSize * scaleFactor;

            target.x = Mathf.Clamp(target.x, p.boxSizeMin.x, p.boxSizeMax.x);
            target.y = Mathf.Clamp(target.y, p.boxSizeMin.y, p.boxSizeMax.y);
            target.z = Mathf.Clamp(target.z, p.boxSizeMin.z, p.boxSizeMax.z);

            target.x = Mathf.Max(0.01f, target.x);
            target.y = Mathf.Max(0.01f, target.y);
            target.z = Mathf.Max(0.01f, target.z);

            currentBoxSize = target;
        }

        ApplyPresetVisuals();
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
        if (!brushRoot || !preset) return;

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
        MakeTransparent(sphereVis?.GetComponent<MeshRenderer>(), a);
        MakeTransparent(cubeVis?.GetComponent<MeshRenderer>(), a);
    }

    // *** CHANGED *** pulled out local function to avoid potential allocations
    static void MakeTransparent(MeshRenderer mr, float a)
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
        _holdStartTime = -1f;
        _primedThisHold = false;
        _hadPrevTarget = false;

        // *** CHANGED *** reset preview throttle state
        _lastPreviewTime = -1f;

        PullSizesFromPreset();
        ApplyPresetVisuals();
    }

    void PullSizesFromPreset()
    {
        if (!preset) return;

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

    // ---------- UI helpers ----------
    public float GetChargeProgress01()
    {
        if (!preset) return 0f;
        if (!preset.enableChargeHold) return 1f;
        if (_holdStartTime < 0f) return 0f;
        float need = Mathf.Max(0.0001f, preset.chargeTimeSeconds);
        float t = Mathf.Clamp01((Time.time - _holdStartTime) / need);
        return _primedThisHold ? 1f : t;
    }

    public float GetCooldownRemainingSeconds()
    {
        if (!preset) return 0f;
        if (!_nextReadyTime.TryGetValue(preset, out float readyAt)) return 0f;
        return Mathf.Max(0f, readyAt - Time.time);
    }

    public float GetCooldownProgress01()
    {
        if (!preset) return 1f;
        float cd = Mathf.Max(0.0001f, preset.cooldownSeconds);
        if (!_nextReadyTime.TryGetValue(preset, out float readyAt)) return 1f;
        float remain = Mathf.Max(0f, readyAt - Time.time);
        return Mathf.Clamp01(1f - remain / cd);
    }

    public bool IsReadyNow() => IsToolReady(preset);
}
