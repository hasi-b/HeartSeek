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
            Debug.LogWarning("No valid hiding spots — using relaxed fallback");
            allSpots = GetRelaxedSpots(furniture, playerPos);
        }

        if (allSpots.Count == 0)
        {
            Debug.LogError("Could not find any spots at all");
            return;
        }

        // best spots first (highest discretion score)
        allSpots.Sort((a, b) => b.score.CompareTo(a.score));

        hiddenChars.Clear();
        var used = new List<Vector3>();

        foreach (var c in characters)
        {
            bool placed = TryPlaceCharacter(c, allSpots, used, playerPos);

            if (!placed)
                Debug.LogWarning("FAILED: " + c.name + " — no spot with enough separation");
        }
    }

    /// <summary>
    /// Places a character in the best available spot that respects separation.
    /// Separation is ALWAYS respected — characters never overlap.
    /// </summary>
    bool TryPlaceCharacter(GameObject c, List<HidingSpot> allSpots,
                           List<Vector3> used, Vector3 playerPos)
    {
        // pick from top N with randomness, but require separation
        int topN = Mathf.Min(10, allSpots.Count);
        var topSpots = allSpots.GetRange(0, topN);
        Shuffle(topSpots);

        // first pass: try top spots with full separation
        foreach (var spot in topSpots)
        {
            if (TooClose(spot.position, used, minSpotSeparation))
                continue;

            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);

            Debug.Log("PLACED: " + c.name + " (score: " + spot.score.ToString("F1") + ")");
            return true;
        }

        // second pass: try all spots with full separation
        foreach (var spot in allSpots)
        {
            if (TooClose(spot.position, used, minSpotSeparation))
                continue;

            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);

            Debug.Log("PLACED: " + c.name + " (fallback, score: " + spot.score.ToString("F1") + ")");
            return true;
        }

        // third pass: relaxed separation (half distance) to avoid total failure
        float relaxedSep = minSpotSeparation * 0.5f;
        foreach (var spot in allSpots)
        {
            if (TooClose(spot.position, used, relaxedSep))
                continue;

            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);

            Debug.Log("PLACED: " + c.name + " (relaxed separation)");
            return true;
        }

        return false;
    }

    void PlaceCharacterAt(GameObject c, HidingSpot spot, Vector3 playerPos)
    {
        c.transform.position = spot.position;

        // face the player
        Vector3 toPlayer = playerPos - spot.position;
        toPlayer.y = 0f;

        if (toPlayer.magnitude > 0.01f)
            c.transform.rotation = Quaternion.LookRotation(toPlayer);
    }

    /// <summary>
    /// When strict validation fails, generate spots with relaxed rules
    /// so hiding always works, even in sparse rooms.
    /// </summary>
    List<HidingSpot> GetRelaxedSpots(List<MRUKAnchor> furniture, Vector3 playerPos)
    {
        var spots = new List<HidingSpot>();
        float charHalfH = GetCharHalfHeight();

        foreach (var anchor in furniture)
        {
            if (!anchor.VolumeBounds.HasValue) continue;

            Bounds b = anchor.VolumeBounds.Value;
            float halfW = b.extents.x;
            float halfD = b.extents.z;

            // 4 cardinal directions around each piece of furniture
            Vector3[] dirs = {
                new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
                new Vector3(0, 0, 1), new Vector3(0, 0, -1)
            };

            foreach (var dirLocal in dirs)
            {
                float edgeDist = GetBoxEdgeDistance(halfW, halfD, dirLocal);
                Vector3 dirWorld = anchor.transform.TransformDirection(dirLocal);
                dirWorld.y = 0f;
                dirWorld.Normalize();

                Vector3 pos = anchor.transform.position
                            + dirWorld * (edgeDist + 0.3f);
                pos.y = floorY + charHalfH;

                // basic sanity only
                if (Vector3.Distance(pos, playerPos) < minPlayerDist) continue;
                if (IsInsideFurniture(anchor, pos)) continue;

                spots.Add(new HidingSpot
                {
                    position = pos,
                    anchorPos = anchor.transform.position,
                    score = Vector3.Distance(pos, playerPos) + Random.Range(0f, 2f)
                });
            }
        }

        return spots;
    }

    // ═══════════════════════════
    // SPOT GENERATION
    // ═══════════════════════════

    List<HidingSpot> GetSpotsFor(MRUKAnchor anchor, Vector3 playerPos)
    {
        var valid = new List<HidingSpot>();

        if (!anchor.VolumeBounds.HasValue)
            return valid;

        Vector3 center = anchor.transform.position;
        float charHalfH = GetCharHalfHeight();

        Bounds b = anchor.VolumeBounds.Value;
        float halfW = b.extents.x;
        float halfD = b.extents.z;

        int sampleCount = 20;

        for (int i = 0; i < sampleCount; i++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            Vector3 dirLocal = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

            // hug the furniture edge with a small random tuck
            float edgeDist = GetBoxEdgeDistance(halfW, halfD, dirLocal);
            float tuckDistance = edgeDist + Random.Range(0.12f, 0.35f);

            Vector3 dirWorld = anchor.transform.TransformDirection(dirLocal);
            dirWorld.y = 0f;
            dirWorld.Normalize();

            Vector3 candidate = center + dirWorld * tuckDistance;

            if (!Physics.Raycast(candidate + Vector3.up * 0.5f, Vector3.down,
                out RaycastHit hit, 2f))
                continue;

            if (Mathf.Abs(hit.point.y - floorY) > 0.2f)
                continue;

            candidate.y = hit.point.y + charHalfH;

            string result = ValidateSpot(candidate, anchor, playerPos, charHalfH);

            if (result == "OK")
            {
                float score = CalcDiscretionScore(candidate, anchor, playerPos, charHalfH);
                valid.Add(new HidingSpot
                {
                    position = candidate,
                    anchorPos = center,
                    score = score
                });
            }
        }

        return valid;
    }

    /// <summary>
    /// Higher = more discreet hiding spot.
    /// Rewards: being behind furniture from player's POV, near walls,
    /// deep in corners, and having strong occlusion (all 5 rays blocked).
    /// </summary>
    float CalcDiscretionScore(Vector3 spot, MRUKAnchor anchor,
                              Vector3 playerPos, float charHalfH)
    {
        float score = 0f;

        // FACTOR 1: directly behind furniture (biggest factor)
        // dot product: -1 = directly behind, +1 = between furniture and player
        Vector3 furnitureToSpot = (spot - anchor.transform.position);
        Vector3 playerToFurniture = (anchor.transform.position - playerPos);
        furnitureToSpot.y = 0;
        playerToFurniture.y = 0;

        if (furnitureToSpot.magnitude > 0.01f && playerToFurniture.magnitude > 0.01f)
        {
            float dot = Vector3.Dot(
                furnitureToSpot.normalized,
                playerToFurniture.normalized);
            // dot = 1 means spot is directly behind from player's POV → best
            score += dot * 10f;
        }

        // FACTOR 2: occlusion quality (how many rays are blocked)
        int occlusionCount = CountOccludedRays(spot, charHalfH, playerPos);
        score += occlusionCount * 2f; // 0 to 10

        // FACTOR 3: near a wall (corners feel discreet)
        float wallProximity = GetWallProximity(spot, 1.0f);
        score += (1f - wallProximity) * 5f; // closer wall = higher score

        // FACTOR 4: NOT in the player's current view cone (prefer spots outside FOV)
        Vector3 playerForward = GetPlayerForward();
        Vector3 toSpot = (spot - playerPos).normalized;
        toSpot.y = 0;
        playerForward.y = 0;
        if (playerForward.magnitude > 0.01f)
        {
            float viewDot = Vector3.Dot(playerForward.normalized, toSpot);
            // viewDot = 1 means directly in front of player → bad
            score += (1f - viewDot) * 3f;
        }

        // small randomness to break ties
        score += Random.Range(0f, 1.5f);

        return score;
    }

    int CountOccludedRays(Vector3 spot, float charHalfH, Vector3 playerPos)
    {
        Vector3[] checkPoints = {
            spot + Vector3.up * charHalfH * 0.9f,
            spot,
            spot - Vector3.up * charHalfH * 0.9f,
            spot + Vector3.left * 0.15f,
            spot + Vector3.right * 0.15f
        };

        int count = 0;
        foreach (var point in checkPoints)
        {
            Vector3 dir = (point - playerPos).normalized;
            float dist = Vector3.Distance(playerPos, point);

            if (Physics.Raycast(playerPos, dir, out RaycastHit hit, dist - 0.05f))
            {
                MRUKAnchor hitAnchor = hit.collider.GetComponentInParent<MRUKAnchor>();
                if (hitAnchor != null && IsFurniture(hitAnchor))
                    count++;
            }
        }
        return count;
    }

    /// <summary>Returns 0 if touching wall, 1 if far from any wall.</summary>
    float GetWallProximity(Vector3 point, float maxDist)
    {
        float closest = maxDist;
        foreach (var anchor in room.GetRoomAnchors())
        {
            if (!anchor.HasLabel("WALL_FACE")) continue;

            Vector3 wallCenter = anchor.transform.position;
            Vector3 wallNormal = anchor.transform.forward;
            float dist = Mathf.Abs(Vector3.Dot(point - wallCenter, wallNormal));

            if (dist < closest) closest = dist;
        }
        return Mathf.Clamp01(closest / maxDist);
    }

    Vector3 GetPlayerForward()
    {
        if (playerHead != null) return playerHead.forward;
        if (Camera.main != null) return Camera.main.transform.forward;
        return Vector3.forward;
    }

    float GetBoxEdgeDistance(float halfW, float halfD, Vector3 dir)
    {
        float absX = Mathf.Abs(dir.x);
        float absZ = Mathf.Abs(dir.z);

        if (absX < 0.001f && absZ < 0.001f) return 0f;

        float tx = absX > 0.001f ? halfW / absX : float.MaxValue;
        float tz = absZ > 0.001f ? halfD / absZ : float.MaxValue;

        return Mathf.Min(tx, tz);
    }

    // ═══════════════════════════
    // VALIDATION
    // ═══════════════════════════

    string ValidateSpot(Vector3 spot, MRUKAnchor anchor, Vector3 playerPos, float charHalfH)
    {
        if (Vector3.Distance(playerPos, spot) < minPlayerDist)
            return "TOO_CLOSE";

        if (!room.IsPositionInRoom(spot, true))
            return "OUTSIDE_ROOM";

        if (IsInsideFurniture(anchor, spot))
            return "INSIDE_FURNITURE";

        foreach (var other in room.GetRoomAnchors())
        {
            if (other == anchor) continue;
            if (!IsFurniture(other)) continue;
            if (IsInsideFurniture(other, spot))
                return "INSIDE_OTHER_FURNITURE";
        }

        float overlapRadius = 0.2f;
        Collider[] meshOverlaps = Physics.OverlapSphere(spot, overlapRadius);
        foreach (var col in meshOverlaps)
        {
            MRUKAnchor overlapAnchor = col.GetComponentInParent<MRUKAnchor>();
            if (overlapAnchor != null && IsFurniture(overlapAnchor))
                return "OVERLAPPING_FURNITURE";
        }

        Collider[] near = Physics.OverlapSphere(spot, minClearance);
        foreach (var col in near)
        {
            if (col.GetComponentInParent<MRUKAnchor>() != null)
                continue;
            if (!col.isTrigger)
                return "TOO_TIGHT";
        }

        // require minimum occlusion
        if (CountOccludedRays(spot, charHalfH, playerPos) < 3)
            return "VISIBLE";

        return "OK";
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