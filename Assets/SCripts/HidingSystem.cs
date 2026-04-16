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
    public float minClearance = 0.3f;
    public float minPlayerDist = 1.5f;
    public float minSpotSeparation = 1.0f;

    [Header("Debug")]
    public bool visualizeSpots = true;

    private MRUKRoom room;
    private float floorY;

    private List<GameObject> hiddenChars = new();
    private List<Vector3> debugUsedSpots = new();
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

        StartCoroutine(GameFlow());
    }

    // ═══════════════════════════
    // GAME FLOW
    // ═══════════════════════════

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
        debugUsedSpots.Clear();

        Vector3 playerPos = GetPlayerHead();

        var furniture = GetFurniture();
        if (furniture.Count == 0)
        {
            Debug.LogWarning("No furniture found");
            return;
        }

        // shuffle furniture for variety
        Shuffle(furniture);

        var allSpots = new List<HidingSpot>();

        foreach (var anchor in furniture)
        {
            var spots = GetSpotsFor(anchor, playerPos);
            allSpots.AddRange(spots);
        }

        Debug.Log("Valid spots: " + allSpots.Count);

        if (allSpots.Count == 0)
        {
            Debug.LogWarning("No valid hiding spots — using fallback");
            PlaceFallback(furniture, playerPos);
            return;
        }

        allSpots.Sort((a, b) => b.score.CompareTo(a.score));

        hiddenChars.Clear();
        var used = new List<Vector3>();

        foreach (var c in characters)
        {
            bool placed = TryPlaceCharacter(c, allSpots, used, playerPos);

            if (!placed)
            {
                // fallback: pick any valid spot ignoring separation
                foreach (var spot in allSpots)
                {
                    PlaceCharacterAt(c, spot, playerPos);
                    used.Add(spot.position);
                    hiddenChars.Add(c);
                    debugUsedSpots.Add(spot.position);
                    placed = true;
                    Debug.Log("PLACED (fallback): " + c.name);
                    break;
                }
            }

            if (!placed)
                Debug.LogWarning("FAILED: " + c.name);
        }
    }

    bool TryPlaceCharacter(GameObject c, List<HidingSpot> allSpots,
                           List<Vector3> used, Vector3 playerPos)
    {
        // pick from top N with some randomness
        int topN = Mathf.Min(8, allSpots.Count);
        var topSpots = allSpots.GetRange(0, topN);
        Shuffle(topSpots);

        foreach (var spot in topSpots)
        {
            if (TooClose(spot.position, used, minSpotSeparation))
                continue;

            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);

            Debug.Log("PLACED: " + c.name + " at " + spot.position);
            return true;
        }

        return false;
    }

    void PlaceCharacterAt(GameObject c, HidingSpot spot, Vector3 playerPos)
    {
        c.transform.position = spot.position;

        // face the player (they should feel "watched")
        Vector3 toPlayer = playerPos - spot.position;
        toPlayer.y = 0f;

        if (toPlayer.magnitude > 0.01f)
            c.transform.rotation = Quaternion.LookRotation(toPlayer);
    }

    void PlaceFallback(List<MRUKAnchor> furniture, Vector3 playerPos)
    {
        // desperate fallback: just place beside each furniture item
        hiddenChars.Clear();
        float charHalfH = GetCharHalfHeight();

        int idx = 0;
        foreach (var c in characters)
        {
            if (idx >= furniture.Count) break;

            var anchor = furniture[idx % furniture.Count];
            Vector3 pos = anchor.transform.position;
            pos.y = floorY + charHalfH;
            pos += (pos - playerPos).normalized * 0.5f;

            var spot = new HidingSpot { position = pos, anchorPos = anchor.transform.position };
            PlaceCharacterAt(c, spot, playerPos);
            hiddenChars.Add(c);
            debugUsedSpots.Add(pos);
            idx++;
        }
    }

    // ═══════════════════════════
    // SPOT GENERATION
    // ═══════════════════════════

    List<HidingSpot> GetSpotsFor(MRUKAnchor anchor, Vector3 playerPos)
    {
        var valid = new List<HidingSpot>();

        Vector3 center = anchor.transform.position;
        float charHalfH = GetCharHalfHeight();

        if (!anchor.VolumeBounds.HasValue)
            return valid;

        Bounds b = anchor.VolumeBounds.Value;
        float halfW = b.extents.x;
        float halfD = b.extents.z;

        // sample at furniture edges, not in a fixed circle
        // this makes spots hug the furniture — more natural
        int sampleCount = 16;

        for (int i = 0; i < sampleCount; i++)
        {
            // random angle around the furniture
            float angle = Random.Range(0f, Mathf.PI * 2f);
            Vector3 dirLocal = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

            // project outward until just past the bounding box edge
            // then add small random distance for tucking behind
            float edgeDist = GetBoxEdgeDistance(halfW, halfD, dirLocal);
            float tuckDistance = edgeDist + Random.Range(0.15f, 0.4f);

            // transform local direction to world (respects furniture rotation)
            Vector3 dirWorld = anchor.transform.TransformDirection(dirLocal);
            dirWorld.y = 0f;
            dirWorld.Normalize();

            Vector3 candidate = center + dirWorld * tuckDistance;

            // snap to floor
            if (!Physics.Raycast(candidate + Vector3.up * 0.5f, Vector3.down,
                out RaycastHit hit, 2f))
                continue;

            if (Mathf.Abs(hit.point.y - floorY) > 0.2f)
                continue;

            candidate.y = hit.point.y + charHalfH;

            string result = ValidateSpot(candidate, anchor, playerPos, charHalfH);

            if (result == "OK")
            {
                // scoring
                float distScore = Vector3.Distance(
                    new Vector3(candidate.x, 0, candidate.z),
                    new Vector3(playerPos.x, 0, playerPos.z));

                // prefer being on opposite side of furniture from player
                Vector3 furnitureToSpot = (candidate - center);
                Vector3 furnitureToPlayer = (playerPos - center);
                furnitureToSpot.y = 0;
                furnitureToPlayer.y = 0;

                float dot = Vector3.Dot(
                    furnitureToSpot.normalized,
                    furnitureToPlayer.normalized);

                // strong bonus for being directly opposite
                float behindBonus = dot < -0.3f ? 4f : 0f;

                // bonus for being close to a wall (natural corner hiding)
                float wallBonus = IsNearWall(candidate, 0.6f) ? 2f : 0f;

                valid.Add(new HidingSpot
                {
                    position = candidate,
                    anchorPos = center,
                    score = distScore + behindBonus + wallBonus + Random.Range(0f, 8f)
                });
            }
        }

        return valid;
    }

    /// <summary>
    /// Distance from furniture center to the bounding box edge in a given direction.
    /// Makes spots hug the actual shape instead of circling at a fixed radius.
    /// </summary>
    float GetBoxEdgeDistance(float halfW, float halfD, Vector3 dir)
    {
        float absX = Mathf.Abs(dir.x);
        float absZ = Mathf.Abs(dir.z);

        if (absX < 0.001f && absZ < 0.001f) return 0f;

        // ray-box intersection in 2D
        float tx = absX > 0.001f ? halfW / absX : float.MaxValue;
        float tz = absZ > 0.001f ? halfD / absZ : float.MaxValue;

        return Mathf.Min(tx, tz);
    }

    bool IsNearWall(Vector3 point, float maxDist)
    {
        foreach (var anchor in room.GetRoomAnchors())
        {
            if (!anchor.HasLabel("WALL_FACE")) continue;

            // approximate distance to wall plane
            Vector3 wallCenter = anchor.transform.position;
            Vector3 wallNormal = anchor.transform.forward;
            float dist = Mathf.Abs(Vector3.Dot(point - wallCenter, wallNormal));

            if (dist < maxDist) return true;
        }
        return false;
    }

    // ═══════════════════════════
    // VALIDATION
    // ═══════════════════════════

    string ValidateSpot(Vector3 spot, MRUKAnchor anchor, Vector3 playerPos, float charHalfH)
    {
        // 0. too close to player
        if (Vector3.Distance(playerPos, spot) < minPlayerDist)
            return "TOO_CLOSE";

        // 1. inside the room
        if (!room.IsPositionInRoom(spot, true))
            return "OUTSIDE_ROOM";

        // 2. not inside any furniture bounding volume
        if (IsInsideFurniture(anchor, spot))
            return "INSIDE_FURNITURE";

        foreach (var other in room.GetRoomAnchors())
        {
            if (other == anchor) continue;
            if (!IsFurniture(other)) continue;
            if (IsInsideFurniture(other, spot))
                return "INSIDE_OTHER_FURNITURE";
        }

        // 3. not overlapping actual furniture mesh colliders
        float overlapRadius = 0.2f;
        Collider[] meshOverlaps = Physics.OverlapSphere(spot, overlapRadius);
        foreach (var col in meshOverlaps)
        {
            MRUKAnchor overlapAnchor = col.GetComponentInParent<MRUKAnchor>();
            if (overlapAnchor != null && IsFurniture(overlapAnchor))
                return "OVERLAPPING_FURNITURE";
        }

        // 4. general clearance
        Collider[] near = Physics.OverlapSphere(spot, minClearance);
        foreach (var col in near)
        {
            if (col.GetComponentInParent<MRUKAnchor>() != null)
                continue;
            if (!col.isTrigger)
                return "TOO_TIGHT";
        }

        // 5. occlusion
        if (!IsOccludedFromPlayer(spot, charHalfH, anchor, playerPos))
            return "VISIBLE";

        return "OK";
    }

    bool IsOccludedFromPlayer(Vector3 spot, float charHalfH, MRUKAnchor anchor, Vector3 playerPos)
    {
        Vector3[] checkPoints = new Vector3[]
        {
            spot + Vector3.up * charHalfH * 0.9f,
            spot,
            spot - Vector3.up * charHalfH * 0.9f,
            spot + Vector3.left * 0.15f,
            spot + Vector3.right * 0.15f
        };

        int occludedCount = 0;
        int required = 3;

        foreach (var point in checkPoints)
        {
            Vector3 dir = (point - playerPos).normalized;
            float dist = Vector3.Distance(playerPos, point);

            if (Physics.Raycast(playerPos, dir, out RaycastHit hit, dist - 0.05f))
            {
                MRUKAnchor hitAnchor = hit.collider.GetComponentInParent<MRUKAnchor>();

                if (hitAnchor == anchor)
                    occludedCount++;
                else if (hitAnchor != null && hitAnchor.HasLabel("WALL_FACE"))
                    return false;
                else if (hitAnchor != null && IsFurniture(hitAnchor))
                    occludedCount++;
            }
        }

        return occludedCount >= required;
    }

    // ═══════════════════════════
    // INSIDE FURNITURE CHECK
    // ═══════════════════════════

    bool IsInsideFurniture(MRUKAnchor anchor, Vector3 worldPoint)
    {
        if (!anchor.VolumeBounds.HasValue)
            return false;

        Bounds b = anchor.VolumeBounds.Value;
        Vector3 local = anchor.transform.InverseTransformPoint(worldPoint);

        Vector3 half = b.extents - Vector3.one * 0.05f;

        return Mathf.Abs(local.x - b.center.x) < half.x &&
               Mathf.Abs(local.y - b.center.y) < half.y &&
               Mathf.Abs(local.z - b.center.z) < half.z;
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

            Collider col = c.GetComponentInChildren<Collider>();
            if (col != null)
            {
                if (col.Raycast(ray, out RaycastHit charHit, 10f))
                {
                    Vector3 toHit = charHit.point - ray.origin;
                    if (Physics.Raycast(ray.origin, toHit.normalized,
                        out RaycastHit blocker, toHit.magnitude - 0.05f))
                    {
                        if (blocker.collider.transform.IsChildOf(c.transform)
                            || blocker.collider.transform == c.transform)
                        {
                            OnFound(c);
                            break;
                        }
                    }
                    else
                    {
                        OnFound(c);
                        break;
                    }
                }
            }
            else
            {
                Renderer rend = c.GetComponentInChildren<Renderer>();
                if (rend == null) continue;

                if (rend.bounds.IntersectRay(ray))
                {
                    float dist = Vector3.Distance(ray.origin, c.transform.position);
                    if (dist < 8f)
                    {
                        OnFound(c);
                        break;
                    }
                }
            }
        }
    }

    void OnFound(GameObject c)
    {
        hiddenChars.Remove(c);
        foundCount++;
        Debug.Log("FOUND: " + c.name + " (" + foundCount + " total)");

        if (hiddenChars.Count == 0)
            Debug.Log("ALL FOUND!");
    }

    // ═══════════════════════════
    // HELPERS
    // ═══════════════════════════

    Vector3 GetPlayerHead()
    {
        if (playerHead != null) return playerHead.position;
        return Camera.main.transform.position;
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

    void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ═══════════════════════════
    // DEBUG
    // ═══════════════════════════

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !visualizeSpots) return;

        // only actual used spots, not all candidates
        Gizmos.color = Color.green;
        foreach (var s in debugUsedSpots)
            Gizmos.DrawSphere(s, 0.1f);

        Gizmos.color = Color.yellow;
        foreach (var c in characters)
            if (c != null)
                Gizmos.DrawWireSphere(c.transform.position, 0.15f);

        Gizmos.color = Color.red;
        Vector3 head = GetPlayerHead();
        foreach (var c in hiddenChars)
            if (c != null)
                Gizmos.DrawLine(head, c.transform.position);
    }
}

public class HidingSpot
{
    public Vector3 position;
    public Vector3 anchorPos;
    public float score;
}