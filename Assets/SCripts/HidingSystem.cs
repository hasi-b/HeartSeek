using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class HidingSystem : MonoBehaviour
{
    [Header("Characters")]
    public List<GameObject> characters = new();

    [Header("References")]
    public OVRPassthroughLayer passthroughLayer;
    public Transform playerHead;
    public Transform rightController;

    [Header("Settings")]
    public float hideDelay = 5f;
    public float blackScreenDuration = 3f;
    public float minClearance = 0.35f;

    [Header("Debug")]
    public bool visualizeSpots = true;

    private MRUKRoom room;
    private float floorY;
    private Vector2 playerXZ;

    private List<GameObject> hiddenChars = new();
    private List<Vector3> debugSpots = new();

    private int foundCount = 0;

    void Start()
    {
        MRUK.Instance.RegisterSceneLoadedCallback(OnRoomLoaded);
    }

    void OnRoomLoaded()
    {
        room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogError("No room");
            return;
        }

        foreach (var c in characters)
            c.transform.SetParent(null);

        floorY = room.FloorAnchor.transform.position.y;

        Vector3 head = playerHead != null
            ? playerHead.position
            : Camera.main.transform.position;

        playerXZ = new Vector2(head.x, head.z);

        StartCoroutine(GameFlow());
    }

    IEnumerator GameFlow()
    {
        yield return new WaitForSeconds(hideDelay);

        if (passthroughLayer != null)
            passthroughLayer.hidden = true;

        HideAll();

        yield return new WaitForSeconds(blackScreenDuration);

        if (passthroughLayer != null)
            passthroughLayer.hidden = false;

        Debug.Log("Hunt begins — " + hiddenChars.Count + " hidden");
    }

    void HideAll()
    {
        debugSpots.Clear();

        var furniture = GetFurniture();
        if (furniture.Count == 0)
        {
            Debug.LogWarning("No furniture found");
            return;
        }

        var allSpots = new List<HidingSpot>();

        foreach (var anchor in furniture)
        {
            var spots = GetSpotsFor(anchor);
            allSpots.AddRange(spots);
        }

        Debug.Log("Valid spots: " + allSpots.Count);

        if (allSpots.Count == 0)
        {
            Debug.LogWarning("No valid hiding spots");
            return;
        }

        allSpots.Sort((a, b) => b.score.CompareTo(a.score));

        hiddenChars.Clear();
        var used = new List<Vector3>();

        foreach (var c in characters)
        {
            bool placed = false;

            foreach (var spot in allSpots)
            {
                if (TooClose(spot.position, used, 0.5f))
                    continue;

                c.transform.position = spot.position;

                Vector3 toPlayer = new Vector3(
                    playerXZ.x - spot.position.x,
                    0f,
                    playerXZ.y - spot.position.z);

                if (toPlayer.magnitude > 0.01f)
                    c.transform.rotation = Quaternion.LookRotation(toPlayer);

                hiddenChars.Add(c);
                used.Add(spot.position);
                placed = true;

                Debug.Log("PLACED: " + c.name + " at " + spot.position);
                break;
            }

            if (!placed)
                Debug.LogWarning("FAILED: " + c.name);
        }
    }

    // ═══════════════════════════
    // SPOT GENERATION
    // ═══════════════════════════

    List<HidingSpot> GetSpotsFor(MRUKAnchor anchor)
    {
        var valid = new List<HidingSpot>();

        GetFurnitureDimensions(anchor,
            out _, out _,
            out float halfW,
            out float halfD);

        Vector3 center = anchor.transform.position;
        float charHalfH = GetCharHalfHeight();

        float radius = Mathf.Max(halfW, halfD) + 0.4f;

        int samples = 12;

        for (int i = 0; i < samples; i++)
        {
            float angle = (i / (float)samples) * Mathf.PI * 2f;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

            Vector3 candidate = center + dir * radius;

            if (!Physics.Raycast(candidate + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 2f))
                continue;

            if (Mathf.Abs(hit.point.y - floorY) > 0.2f)
                continue;

            candidate.y = hit.point.y + charHalfH;

            string result = ValidateSpot(candidate, anchor);

            if (result == "OK")
            {
                debugSpots.Add(candidate);

                valid.Add(new HidingSpot
                {
                    position = candidate,
                    score = CalcScore(candidate)
                });
            }
        }

        return valid;
    }

    // ═══════════════════════════
    // VALIDATION
    // ═══════════════════════════

    string ValidateSpot(Vector3 spot, MRUKAnchor anchor)
    {
        Vector3 head = playerHead != null
            ? playerHead.position
            : Camera.main.transform.position;

        // 0. too close
        if (Vector3.Distance(head, spot) < 1.2f)
            return "TOO_CLOSE";

        // 1. too far from furniture
        float distToAnchor = Vector3.Distance(
            new Vector3(spot.x, 0, spot.z),
            new Vector3(anchor.transform.position.x, 0, anchor.transform.position.z)
        );

        if (distToAnchor > 2.0f)
            return "TOO_FAR";

        // 2. INSIDE FURNITURE (fixed proper check)
        if (IsInsideFurniture(anchor, spot))
            return "INSIDE_FURNITURE";

        foreach (var other in room.GetRoomAnchors())
        {
            if (other == anchor) continue;
            if (!IsFurniture(other)) continue;

            if (IsInsideFurniture(other, spot))
                return "INSIDE_OTHER_FURNITURE";
        }

        // 3. clearance
        Collider[] near = Physics.OverlapSphere(spot, minClearance);
        foreach (var col in near)
        {
            if (col.GetComponentInParent<MRUKAnchor>() != null)
                continue;

            if (!col.isTrigger)
                return "TOO_TIGHT";
        }

        // 4. visibility (must be occluded by same furniture)
        Vector3 dir = (spot - head).normalized;
        float dist = Vector3.Distance(head, spot);

        if (Physics.Raycast(head, dir, out RaycastHit hit, dist))
        {
            MRUKAnchor hitAnchor =
                hit.collider.GetComponentInParent<MRUKAnchor>();

            if (hitAnchor == null)
                return "VISIBLE";

            if (hitAnchor.HasLabel("WALL_FACE"))
                return "BLOCKED_BY_WALL";

            if (hitAnchor != anchor)
                return "WRONG_OCCLUDER";
        }
        else
        {
            return "VISIBLE";
        }

        return "OK";
    }

    // ═══════════════════════════
    // FIXED: INSIDE CHECK
    // ═══════════════════════════

    bool IsInsideFurniture(MRUKAnchor anchor, Vector3 worldPoint)
    {
        if (!anchor.VolumeBounds.HasValue)
            return false;

        Bounds b = anchor.VolumeBounds.Value;

        Vector3 local = anchor.transform.InverseTransformPoint(worldPoint);

        Vector3 scaledSize = Vector3.Scale(b.size, anchor.transform.lossyScale);

        Vector3 half = scaledSize * 0.5f - Vector3.one * 0.05f;

        return Mathf.Abs(local.x) < half.x &&
               Mathf.Abs(local.y) < half.y &&
               Mathf.Abs(local.z) < half.z;
    }

    // ═══════════════════════════
    // FIX: MISSING METHOD RESTORED
    // ═══════════════════════════

    void GetFurnitureDimensions(
        MRUKAnchor anchor,
        out Vector3 fwd,
        out Vector3 rgt,
        out float halfW,
        out float halfD)
    {
        fwd = anchor.transform.forward;
        rgt = anchor.transform.right;

        fwd.y = 0f;
        rgt.y = 0f;

        fwd.Normalize();
        rgt.Normalize();

        halfW = 0.3f;
        halfD = 0.3f;

        if (!anchor.VolumeBounds.HasValue)
            return;

        Bounds b = anchor.VolumeBounds.Value;
        Vector3 sc = anchor.transform.lossyScale;

        halfW = Mathf.Abs(b.size.x * sc.x) * 0.5f;
        halfD = Mathf.Abs(b.size.z * sc.z) * 0.5f;
    }

    // ═══════════════════════════
    // DISCOVERY
    // ═══════════════════════════

    void Update()
    {
        if (hiddenChars.Count == 0) return;

        if (OVRInput.GetDown(
            OVRInput.Button.PrimaryIndexTrigger,
            OVRInput.Controller.RTouch))
            TryFind();
    }

    void TryFind()
    {
        if (rightController == null) return;

        Ray ray = new Ray(rightController.position, rightController.forward);

        foreach (var c in new List<GameObject>(hiddenChars))
        {
            if (c == null) continue;

            Vector3 toChar = c.transform.position - ray.origin;
            float proj = Vector3.Dot(toChar, ray.direction);
            if (proj < 0f) continue;

            float dist = Vector3.Distance(
                ray.origin + ray.direction * proj,
                c.transform.position);

            if (dist < 0.5f)
            {
                hiddenChars.Remove(c);
                foundCount++;

                Debug.Log("FOUND: " + c.name);
                break;
            }
        }
    }

    // ═══════════════════════════
    // HELPERS
    // ═══════════════════════════

    float CalcScore(Vector3 spot)
    {
        return Vector2.Distance(
            new Vector2(spot.x, spot.z),
            playerXZ) * 5f + Random.Range(0f, 1f);
    }

    float GetCharHalfHeight()
    {
        if (characters.Count == 0) return 0.35f;

        var r = characters[0].GetComponentInChildren<Renderer>();
        return r != null ? r.bounds.extents.y : 0.35f;
    }

    bool TooClose(Vector3 spot, List<Vector3> used, float min)
    {
        foreach (var u in used)
            if (Vector3.Distance(spot, u) < min)
                return true;
        return false;
    }

    bool IsFurniture(MRUKAnchor anchor)
    {
        var skip = new HashSet<string>
        {
            "FLOOR","CEILING","WALL_FACE",
            "WINDOW_FRAME","DOOR_FRAME"
        };

        foreach (var l in anchor.AnchorLabels)
            if (skip.Contains(l.ToUpper()))
                return false;

        return true;
    }

    List<MRUKAnchor> GetFurniture()
    {
        var list = new List<MRUKAnchor>();

        if (room == null) return list;

        foreach (var a in room.GetRoomAnchors())
            if (IsFurniture(a))
                list.Add(a);

        return list;
    }

    // ═══════════════════════════
    // DEBUG
    // ═══════════════════════════

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !visualizeSpots)
            return;

        Gizmos.color = Color.green;
        foreach (var s in debugSpots)
            Gizmos.DrawSphere(s, 0.1f);

        Gizmos.color = Color.yellow;
        foreach (var c in characters)
            if (c != null)
                Gizmos.DrawWireSphere(c.transform.position, 0.15f);
    }
}

public class HidingSpot
{
    public Vector3 position;
    public float score;
}