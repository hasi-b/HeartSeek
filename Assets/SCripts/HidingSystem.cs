using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// MR hide-and-seek placement: hides movers (boxes → characters) behind real scene geometry,
/// on the floor, inside the scanned room, occluded from the seeker’s view when possible.
///
/// Requirements
/// • MRUK scene loaded; <see cref="MRUK.Instance"/> has a current room.
/// • MRUKLoader: enable mesh colliders for occlusion / inside tests (recommended).
/// • Assign <see cref="seekerViewTransform"/> to the player camera / XR rig camera.
/// • Tune <see cref="occlusionLayerMask"/> to environment only (exclude player / hiders).
/// </summary>
[DisallowMultipleComponent]
public class HidingSystem : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Hiders")]
    [Tooltip("Boxes now; character prefabs later. Pivot at feet recommended.")]
    public List<GameObject> characters = new();

    [Header("Passthrough")]
    public OVRPassthroughLayer passthroughLayer;

    [Header("Seeker")]
    [Tooltip("XR / main camera (eye height). Strongly recommended.")]
    [SerializeField] Transform seekerViewTransform;

    [Tooltip("If no camera at runtime, use this XZ in editor (Y ignored).")]
    [SerializeField] bool useEditorSeekerFallback = true;
    [SerializeField] Vector3 editorSeekerPosition = new Vector3(0f, 1.6f, -2.5f);

    [Header("Scene anchors used as hide targets")]
    [SerializeField] MRUKAnchor.SceneLabels hideAnchorLabels =
        MRUKAnchor.SceneLabels.STORAGE |
        MRUKAnchor.SceneLabels.TABLE |
        MRUKAnchor.SceneLabels.COUCH |
        MRUKAnchor.SceneLabels.WALL_FACE;

    [Tooltip("Include OTHER (e.g. Scene boxes) when primary labels are missing.")]
    [SerializeField] bool includeOtherFallback = true;

    [SerializeField] float minFurnitureSize = 0.3f;

    [Header("Placement shape")]
    [SerializeField] float behindClearance = 0.38f;
    [SerializeField] float lateralJitter = 0.85f;
    [SerializeField] float extraDepthJitter = 0.25f;
    [Tooltip("Variants per anchor (randomized each run).")]
    [SerializeField] int variantsPerAnchor = 5;

    [Header("Validation")]
    [SerializeField] float minSeparationH = 1.05f;
    [SerializeField] float minDistanceFromSeekerH = 1.35f;
    [Tooltip("Dot product: must be past obstacle from seeker (0 = perpendicular, 1 = aligned).")]
    [SerializeField] float minBehindDot = 0.35f;
    [SerializeField] int maxTriesPerHider = 96;

    [SerializeField] LayerMask occlusionLayerMask = ~0;
    [SerializeField] LayerMask groundLayers = ~0;
    [Tooltip("Layers treated as solid for “inside mesh” tests. Exclude hider prefabs.")]
    [SerializeField] LayerMask geometryTestLayers = ~0;

    [SerializeField] float bodyRadius = 0.22f;
    [SerializeField] float eyeHeightForRay = 1.45f;

    [Header("Ground")]
    [SerializeField] float groundYOffset = 0.02f;
    [SerializeField] float groundRayUp = 0.4f;
    [SerializeField] float groundRayDown = 4f;

    [Header("Timing")]
    [SerializeField] float blackoutSeconds = 3f;
    [SerializeField] float revealDelaySeconds = 1f;

    [Header("Debug")]
    [SerializeField] bool drawGizmos = true;

    // ── Runtime ──────────────────────────────────────────────────────────────

    MRUKRoom _room;
    readonly List<Vector3> _placedFeetXZ = new();
    readonly List<(Vector3 pos, bool ideal)> _gizmo = new();
    System.Random _rng;

    // ── Unity ────────────────────────────────────────────────────────────────

    void Start()
    {
        MRUK.Instance.RegisterSceneLoadedCallback(OnRoomLoaded);
    }

    void OnRoomLoaded()
    {
        _room = MRUK.Instance.GetCurrentRoom();
        Debug.Log("[HidingSystem] Room loaded.");
        StartCoroutine(HideSequence());
    }

    IEnumerator HideSequence()
    {
        if (passthroughLayer != null)
            passthroughLayer.hidden = true;

        yield return new WaitForSeconds(blackoutSeconds);

        PlaceAllHiders();

        yield return new WaitForSeconds(revealDelaySeconds);

        if (passthroughLayer != null)
            passthroughLayer.hidden = false;
    }

    [ContextMenu("Place hiders now")]
    public void PlaceAllHiders()
    {
        if ((_room == null && MRUK.Instance != null))
            _room = MRUK.Instance.GetCurrentRoom();

        if (_room == null)
        {
            Debug.LogWarning("[HidingSystem] No MRUK room.");
            return;
        }

        if (characters == null || characters.Count == 0)
        {
            Debug.LogWarning("[HidingSystem] No objects in characters list.");
            return;
        }

        _rng = new System.Random(Environment.TickCount ^ GetInstanceID() ^ Guid.NewGuid().GetHashCode());
        _placedFeetXZ.Clear();
        _gizmo.Clear();

        Vector3 seekerEye = GetSeekerEyeWorld();
        float floorRefY = GetFloorReferenceY();

        var candidates = BuildCandidates(seekerEye, floorRefY);
        if (candidates.Count == 0)
        {
            Debug.LogWarning("[HidingSystem] No candidates. Enable OTHER fallback or check Space Setup.");
            return;
        }

        Shuffle(candidates);

        for (int i = 0; i < characters.Count; i++)
        {
            bool ok = TryPlaceHider(characters[i], seekerEye, floorRefY, candidates, out Vector3 feetWorld);
            if (!ok)
            {
                feetWorld = TryEmergencySpot(seekerEye, floorRefY);
                Debug.LogWarning($"[HidingSystem] Hider {i} used emergency placement.");
            }

            characters[i].transform.position = feetWorld;
            FaceAwayFromSeeker(characters[i], seekerEye);
            _placedFeetXZ.Add(new Vector3(feetWorld.x, 0f, feetWorld.z));
            _gizmo.Add((feetWorld, ok));
        }
    }

    // ── Candidate generation ───────────────────────────────────────────────────

    readonly struct Candidate
    {
        public readonly Vector3 FeetXZ;
        public readonly float FloorY;
        public readonly MRUKAnchor Anchor;

        public Candidate(Vector3 feetXZ, float floorY, MRUKAnchor anchor)
        {
            FeetXZ = feetXZ;
            FloorY = floorY;
            Anchor = anchor;
        }
    }

    List<Candidate> BuildCandidates(Vector3 seekerEye, float floorRefY)
    {
        var list = new List<Candidate>();
        var anchors = CollectHideAnchors();

        if (anchors.Count == 0)
            return list;

        Vector3 seekerXZ = Flatten(seekerEye);

        foreach (var anchor in anchors)
        {
            Vector3 center = anchor.GetAnchorCenter();
            Vector3 toAnchorH = HorizontalNormalize(center - seekerEye);

            if (toAnchorH.sqrMagnitude < 1e-6f)
                continue;

            for (int v = 0; v < variantsPerAnchor; v++)
            {
                Vector3 lateral = Vector3.Cross(Vector3.up, toAnchorH).normalized;
                float lat = NextFloat(-lateralJitter, lateralJitter);
                float extra = behindClearance + NextFloat(0f, extraDepthJitter);

                float half = GetHalfExtentPast(anchor, toAnchorH);
                Vector3 baseXZ = Flatten(center) + toAnchorH * (half + extra) + lateral * lat;

                Vector3 feetWorld = SnapFeetToGround(baseXZ, floorRefY);
                list.Add(new Candidate(new Vector3(feetWorld.x, 0f, feetWorld.z), feetWorld.y, anchor));
            }
        }

        return list;
    }

    List<MRUKAnchor> CollectHideAnchors()
    {
        var result = new List<MRUKAnchor>();
        if (_room == null)
            return result;

        foreach (var a in _room.GetRoomAnchors())
        {
            if (!a.HasAnyLabel(hideAnchorLabels))
                continue;
            if (!PassesSize(a))
                continue;
            result.Add(a);
        }

        if (result.Count > 0 || !includeOtherFallback)
            return result;

        foreach (var a in _room.GetRoomAnchors())
        {
            if (!a.HasAnyLabel(MRUKAnchor.SceneLabels.OTHER))
                continue;
            if (!a.VolumeBounds.HasValue)
                continue;
            if (!PassesSize(a))
                continue;
            result.Add(a);
        }

        return result;
    }

    bool PassesSize(MRUKAnchor a)
    {
        if (a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            return true;
        Vector3 s = a.GetAnchorSize();
        return Mathf.Max(s.x, s.y, s.z) >= minFurnitureSize;
    }

    float GetHalfExtentPast(MRUKAnchor anchor, Vector3 worldDirH)
    {
        worldDirH.y = 0f;
        if (worldDirH.sqrMagnitude < 1e-4f)
            return 0.4f;
        worldDirH.Normalize();

        if (anchor.VolumeBounds.HasValue)
        {
            Vector3 localDir = anchor.transform.InverseTransformDirection(worldDirH);
            Vector3 e = anchor.VolumeBounds.Value.extents;
            return Mathf.Abs(localDir.x) * e.x +
                   Mathf.Abs(localDir.y) * e.y +
                   Mathf.Abs(localDir.z) * e.z;
        }

        Vector3 size = anchor.GetAnchorSize();
        return Mathf.Max(size.x, size.z) * 0.5f;
    }

    // ── Placement pass ─────────────────────────────────────────────────────────────

    bool TryPlaceHider(
        GameObject hider,
        Vector3 seekerEye,
        float floorRefY,
        List<Candidate> candidates,
        out Vector3 feetWorld)
    {
        feetWorld = Vector3.zero;
        int n = 0;

        foreach (var c in candidates)
        {
            if (n++ >= maxTriesPerHider)
                break;

            feetWorld = new Vector3(c.FeetXZ.x, c.FloorY, c.FeetXZ.z);

            if (!_room.IsPositionInRoom(feetWorld, testVerticalBounds: true))
                continue;

            if (IsInsideBadVolume(feetWorld))
                continue;

            if (!IsGeometricallyBehind(seekerEye, c.Anchor, feetWorld))
                continue;

            feetWorld = SnapFeetToGround(c.FeetXZ, feetWorld.y);

            if (IsInsideSolidGeometry(feetWorld))
                continue;

            if (HorizontalDistance(feetWorld, seekerEye) < minDistanceFromSeekerH)
                continue;

            if (!HasSeparation(feetWorld))
                continue;

            if (!IsOccludedFromSeeker(seekerEye, feetWorld))
                continue;

            return true;
        }

        return false;
    }

    Vector3 TryEmergencySpot(Vector3 seekerEye, float floorRefY)
    {
        for (int i = 0; i < 40; i++)
        {
            if (!_room.GenerateRandomPositionOnSurface(
                    MRUK.SurfaceType.FACING_UP,
                    0.35f,
                    new LabelFilter(MRUKAnchor.SceneLabels.FLOOR),
                    out Vector3 p,
                    out Vector3 _))
                continue;

            p.y = floorRefY + groundYOffset;
            p = SnapFeetToGround(new Vector3(p.x, 0f, p.z), p.y);

            if (!_room.IsPositionInRoom(p, true))
                continue;
            if (IsInsideSolidGeometry(p))
                continue;
            if (HorizontalDistance(p, seekerEye) < minDistanceFromSeekerH * 0.5f)
                continue;
            if (!HasSeparation(p))
                continue;

            return p;
        }

        return SnapFeetToGround(Flatten(seekerEye) + Vector3.forward * 2f, floorRefY + groundYOffset);
    }

    // ── Rules ───────────────────────────────────────────────────────────────────

    bool IsGeometricallyBehind(Vector3 seekerEye, MRUKAnchor anchor, Vector3 feetWorld)
    {
        Vector3 seekerXZ = Flatten(seekerEye);
        Vector3 feetXZ = Flatten(feetWorld);

        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
        {
            Vector3 wall = anchor.transform.position;
            wall.y = 0f;
            Vector3 toWall = wall - seekerXZ;
            Vector3 past = feetXZ - wall;
            if (toWall.sqrMagnitude < 1e-4f || past.sqrMagnitude < 1e-4f)
                return false;
            return Vector3.Dot(toWall.normalized, past.normalized) >= minBehindDot;
        }

        Vector3 ac = anchor.GetAnchorCenter();
        ac.y = 0f;
        Vector3 toOb = ac - seekerXZ;
        Vector3 pastOb = feetXZ - ac;
        if (toOb.sqrMagnitude < 1e-4f || pastOb.sqrMagnitude < 1e-4f)
            return false;
        return Vector3.Dot(toOb.normalized, pastOb.normalized) >= minBehindDot;
    }

    bool IsInsideBadVolume(Vector3 worldPos)
    {
        if (_room.IsPositionInSceneVolume(
                worldPos,
                out MRUKAnchor vol,
                testVerticalBounds: true,
                distanceBuffer: 0f) &&
            vol != null &&
            vol.HasAnyLabel(MRUKAnchor.SceneLabels.BED))
            return true;

        return false;
    }

    bool IsOccludedFromSeeker(Vector3 seekerEye, Vector3 feetWorld)
    {
        Vector3 target = feetWorld + Vector3.up * eyeHeightForRay;
        Vector3 dir = target - seekerEye;
        float dist = dir.magnitude;
        if (dist < 0.15f)
            return false;

        dir /= dist;

        const float skin = 0.12f;
        if (!Physics.Raycast(
                seekerEye,
                dir,
                out RaycastHit hit,
                dist - skin,
                occlusionLayerMask,
                QueryTriggerInteraction.Ignore))
            return false;

        float upDot = Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up));
        if (upDot > 0.85f)
            return false;

        Vector3 b = hit.collider.bounds.size;
        if (Mathf.Max(b.x, b.y, b.z) < 0.12f)
            return false;

        return true;
    }

    bool IsInsideSolidGeometry(Vector3 feetWorld)
    {
        float r = bodyRadius;
        Vector3 chest = feetWorld + Vector3.up * 0.85f;

        foreach (var c in Physics.OverlapSphere(chest, r, geometryTestLayers, QueryTriggerInteraction.Ignore))
        {
            if (c.bounds.size.y < 0.12f)
                continue;
            return true;
        }

        foreach (var c in Physics.OverlapSphere(feetWorld + Vector3.up * 0.12f, r * 0.65f, geometryTestLayers, QueryTriggerInteraction.Ignore))
        {
            if (c.bounds.size.y < 0.12f)
                continue;
            return true;
        }

        return false;
    }

    bool HasSeparation(Vector3 feetWorld)
    {
        Vector3 xz = Flatten(feetWorld);
        foreach (var p in _placedFeetXZ)
        {
            if (Vector3.Distance(xz, p) < minSeparationH)
                return false;
        }

        return true;
    }

    Vector3 SnapFeetToGround(Vector3 xz, float referenceY)
    {
        float x = xz.x;
        float z = xz.z;
        Vector3 o = new Vector3(x, referenceY + groundRayUp, z);
        if (Physics.Raycast(o, Vector3.down, out RaycastHit hit, groundRayDown, groundLayers, QueryTriggerInteraction.Ignore))
            return new Vector3(x, hit.point.y + groundYOffset, z);

        float fy = GetFloorReferenceY();
        return new Vector3(x, fy + groundYOffset, z);
    }

    float GetFloorReferenceY()
    {
        if (_room?.FloorAnchor != null)
            return _room.FloorAnchor.transform.position.y;
        return 0f;
    }

    Vector3 GetSeekerEyeWorld()
    {
        if (seekerViewTransform != null)
            return seekerViewTransform.position;

        if (Camera.main != null)
            return Camera.main.transform.position;

        foreach (var c in FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (c != null && c.enabled)
                return c.transform.position;
        }

        if (useEditorSeekerFallback)
            return editorSeekerPosition;

        Debug.LogWarning("[HidingSystem] No seeker camera — assign Seeker View Transform.");
        return new Vector3(0f, 1.6f, 0f);
    }

    static void FaceAwayFromSeeker(GameObject obj, Vector3 seekerEye)
    {
        Vector3 d = obj.transform.position - seekerEye;
        d.y = 0f;
        if (d.sqrMagnitude > 0.0001f)
            obj.transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
    }

    static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);

    static Vector3 HorizontalNormalize(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    float NextFloat(float a, float b) => (float)(_rng.NextDouble() * (b - a) + a);

    void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;

        foreach (var (pos, ideal) in _gizmo)
        {
            Gizmos.color = ideal ? new Color(0.2f, 1f, 0.3f, 0.9f) : new Color(1f, 0.4f, 0f, 0.9f);
            Gizmos.DrawSphere(pos, 0.1f);
        }
    }
}
