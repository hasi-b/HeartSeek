using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class RoomDebugVisualizer : MonoBehaviour
{
    public Transform playerHead;
    public float minClearance = 0.35f;

    private MRUKRoom room;
    private float floorY;
    private Vector2 playerXZ;
    private List<GameObject> spawned = new();

    void Start()
    {
        Invoke("TryVisualize", 3f);
    }

    void TryVisualize()
    {
        room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Invoke("TryVisualize", 1f);
            return;
        }

        floorY = room.FloorAnchor
            .transform.position.y;

        Vector3 head = playerHead != null
            ? playerHead.position
            : Camera.main.transform.position;

        playerXZ = new Vector2(head.x, head.z);

        Debug.Log("Viz FloorY=" + floorY);

        ClearAll();
        Visualize();
    }

    void Visualize()
    {
        // White = player at floor level
        MakeSphere(
            new Vector3(
                playerXZ.x,
                floorY + 0.05f,
                playerXZ.y),
            0.15f, Color.white);

        foreach (var anchor in
            room.GetRoomAnchors())
        {
            if (!IsFurniture(anchor)) continue;

            GetDims(anchor,
                out Vector3 fwd, out Vector3 rgt,
                out float halfW, out float halfD);

            Vector3 pos = anchor.transform.position;

            // Blue = furniture center
            MakeSphere(pos, 0.1f, Color.blue);

            // Cyan = floor below furniture
            MakeSphere(
                new Vector3(pos.x,
                    floorY + 0.02f, pos.z),
                0.06f, Color.cyan);

            float charHalfH = 0.35f;
            float spotY = floorY + charHalfH;
            float gap = 0.5f;

            var sides = new (string n,
                Vector3 d, float h)[]
            {
                ("FRONT", fwd,  halfD),
                ("BACK",  -fwd, halfD),
                ("LEFT",  -rgt, halfW),
                ("RIGHT", rgt,  halfW),
            };

            bool anyGreen = false;

            foreach (var s in sides)
            {
                Vector3 candidate = new Vector3(
                    pos.x + s.d.x * (s.h + gap),
                    spotY,
                    pos.z + s.d.z * (s.h + gap));

                string reason = Check(
                    candidate, anchor,
                    fwd, rgt, halfD, halfW);

                Color c;
                if (reason == "OK")
                {
                    c = Color.green;
                    anyGreen = true;
                    DrawLine(pos, candidate,
                        Color.green);
                }
                else if (reason.StartsWith(
                    "NO_SPACE"))
                    c = Color.magenta;
                else if (reason.StartsWith(
                    "NOT_OCCLUDED"))
                    c = Color.cyan;
                else if (reason == "INSIDE")
                    c = new Color(1f, 0.5f, 0f);
                else if (reason.StartsWith(
                    "FURNITURE_TOO_SHORT"))
                    c = new Color(0.5f, 0f, 0.5f);
                else
                    c = Color.red;

                MakeSphere(candidate, 0.09f, c);
            }

            if (!anyGreen)
                MakeSphere(
                    pos + Vector3.up * 0.4f,
                    0.1f, Color.yellow);
        }

        Debug.Log("Viz done: "
            + spawned.Count + " markers");
    }

    string Check(
        Vector3 spot, MRUKAnchor anchor,
        Vector3 fwd, Vector3 rgt,
        float halfD, float halfW)
    {
        // Inside check — both axis combos
        Vector3 toSpot = new Vector3(
            spot.x - anchor.transform.position.x,
            0f,
            spot.z - anchor.transform.position.z);

        float projD = Mathf.Abs(
            Vector3.Dot(toSpot, fwd));
        float projW = Mathf.Abs(
            Vector3.Dot(toSpot, rgt));

        bool inside =
            (projD < halfD && projW < halfW) ||
            (projD < halfW && projW < halfD);

        if (inside) return "INSIDE";

        // Clearance
        Vector2 spotXZ = new Vector2(
            spot.x, spot.z);
        Vector2 approach =
            (spotXZ - playerXZ).normalized;

        foreach (var other in
            room.GetRoomAnchors())
        {
            if (other == anchor) continue;
            if (other.HasLabel("CEILING")) continue;
            if (other.HasLabel("FLOOR")) continue;

            Vector2 otherXZ = new Vector2(
                other.transform.position.x,
                other.transform.position.z);

            float dotB = Vector2.Dot(
                (otherXZ - spotXZ).normalized,
                approach);

            if (dotB > 0.6f
                && !other.HasLabel("WALL_FACE"))
                continue;

            float dist = other.HasLabel("WALL_FACE")
                ? WallDist(spot, other)
                : FurnDist(spot, other);

            if (dist < minClearance)
                return "NO_SPACE("
                    + other.AnchorLabels[0]
                    + " " + dist.ToString("F2")
                    + "m)";
        }

        // Height check
        float furnTop =
            anchor.transform.position.y;

        if (anchor.VolumeBounds.HasValue)
        {
            float wH = Mathf.Abs(
                anchor.VolumeBounds.Value.size.y
                * anchor.transform.lossyScale.y);
            furnTop = anchor.transform.position.y
                + wH * 0.5f;
        }

        if (furnTop < spot.y + 0.1f)
            return "FURNITURE_TOO_SHORT(top="
                + furnTop.ToString("F2") + ")";

        // Occlusion XZ
        Vector2 furnXZ = new Vector2(
            anchor.transform.position.x,
            anchor.transform.position.z);

        float furnH = Mathf.Max(halfD, halfW);
        Vector2 toSpot2 = spotXZ - playerXZ;
        float len = toSpot2.magnitude;
        if (len < 0.3f) return "TOO_CLOSE";

        Vector2 dir = toSpot2 / len;
        float proj = Vector2.Dot(
            furnXZ - playerXZ, dir);

        if (proj <= 0.1f || proj >= len - 0.1f)
            return "NOT_OCCLUDED";

        if (Vector2.Distance(
            playerXZ + dir * proj, furnXZ)
            >= furnH)
            return "NOT_OCCLUDED";

        return "OK";
    }

    void GetDims(MRUKAnchor anchor,
        out Vector3 fwd, out Vector3 rgt,
        out float halfW, out float halfD)
    {
        fwd = anchor.transform.forward;
        rgt = anchor.transform.right;
        fwd.y = 0f;
        rgt.y = 0f;
        if (fwd.magnitude < 0.01f)
            fwd = Vector3.forward;
        if (rgt.magnitude < 0.01f)
            rgt = Vector3.right;
        fwd.Normalize();
        rgt.Normalize();

        halfW = 0.3f;
        halfD = 0.3f;

        if (!anchor.VolumeBounds.HasValue)
            return;

        Bounds b = anchor.VolumeBounds.Value;
        Vector3 sc = anchor.transform.lossyScale;

        halfW = Mathf.Clamp(
            Mathf.Abs(b.size.x * sc.x) * 0.5f,
            0.1f, 2f);
        halfD = Mathf.Clamp(
            Mathf.Abs(b.size.z * sc.z) * 0.5f,
            0.1f, 2f);
    }

    float WallDist(
        Vector3 spot, MRUKAnchor wall)
    {
        Vector3 n = wall.transform.forward;
        n.y = 0f;
        if (n.magnitude < 0.01f)
            n = Vector3.forward;
        n.Normalize();
        Vector3 d = new Vector3(
            spot.x - wall.transform.position.x,
            0f,
            spot.z - wall.transform.position.z);
        return Mathf.Abs(Vector3.Dot(d, n));
    }

    float FurnDist(
        Vector3 spot, MRUKAnchor other)
    {
        if (!other.VolumeBounds.HasValue)
            return Vector2.Distance(
                new Vector2(spot.x, spot.z),
                new Vector2(
                    other.transform.position.x,
                    other.transform.position.z));

        Vector3 of = other.transform.forward;
        Vector3 or2 = other.transform.right;
        of.y = 0f; of.Normalize();
        or2.y = 0f; or2.Normalize();

        Bounds b = other.VolumeBounds.Value;
        Vector3 sc = other.transform.lossyScale;
        float hD = Mathf.Abs(b.size.z * sc.z)
            * 0.5f;
        float hW = Mathf.Abs(b.size.x * sc.x)
            * 0.5f;

        Vector3 oPos = other.transform.position;
        Vector3 ts = new Vector3(
            spot.x - oPos.x, 0f,
            spot.z - oPos.z);

        Vector3 cl = oPos
            + of * Mathf.Clamp(
                Vector3.Dot(ts, of), -hD, hD)
            + or2 * Mathf.Clamp(
                Vector3.Dot(ts, or2), -hW, hW);

        return Vector2.Distance(
            new Vector2(spot.x, spot.z),
            new Vector2(cl.x, cl.z));
    }

    bool IsFurniture(MRUKAnchor anchor)
    {
        var skip = new HashSet<string>
        {
            "FLOOR","CEILING","WALL_FACE",
            "WALL_ART","WINDOW_FRAME",
            "DOOR_FRAME","INVISIBLE_WALL_FACE",
            "FLOOR_BELOW_OBJECT",
            "CEILING_ABOVE_OBJECT"
        };
        foreach (var l in anchor.AnchorLabels)
            if (skip.Contains(l.ToUpper()))
                return false;
        return true;
    }

    void MakeSphere(
        Vector3 pos, float size, Color color)
    {
        var go = GameObject.CreatePrimitive(
            PrimitiveType.Sphere);
        go.transform.position = pos;
        go.transform.localScale =
            Vector3.one * size;
        Destroy(go.GetComponent<Collider>());
        var r = go.GetComponent<Renderer>();
        var mat = new Material(r.sharedMaterial);
        mat.color = color;
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor",
            color * 0.8f);
        r.material = mat;
        spawned.Add(go);
    }

    void DrawLine(
        Vector3 from, Vector3 to, Color color)
    {
        var go = new GameObject("Line");
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
        lr.startWidth = 0.01f;
        lr.endWidth = 0.01f;
        lr.useWorldSpace = true;
        var m = new Material(Shader.Find(
            "Universal Render Pipeline/Unlit"));
        if (m != null) m.color = color;
        lr.material = m;
        spawned.Add(go);
    }

    void ClearAll()
    {
        foreach (var g in spawned)
            if (g != null) Destroy(g);
        spawned.Clear();
    }
}