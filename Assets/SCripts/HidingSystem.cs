using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Meta.XR.MRUtilityKit;

public class HidingSystem : MonoBehaviour
{
    [Header("Characters")]
    public List<GameObject> characters = new();

    [Header("References")]
    public OVRPassthroughLayer passthroughLayer;
    public Transform playerHead;
    public Transform rightController;
    public Transform leftController;

    [Header("Place Zone")]
    public Transform placeZone;
    public float placeRadius = 0.4f;

    [Header("Effects")]
    public GameObject confettiPrefab;

    [Header("Settings")]
    public float hideDelay = 5f;
    public float minClearance = 0.3f;
    public float minPlayerDist = 1.5f;
    public float minSpotSeparation = 1.0f;
    public float grabDistance = 0.5f;

    [Header("Flow Timing")]
    public float introDuration = 3f;
    public float fadeDuration = 1f;
    public float countdownStepDuration = 1f;
    public float revealMessageDuration = 2f;

    [Header("Intro Spawn")]
    public float introDistance = 1.5f;
    public float introSpacing = 0.5f;

    [Header("UI")]
    public Color blackColor = Color.black;
    public float worldTextHeight = 0.4f;
    public float worldTextScale = 0.003f;

    [Header("Haptics")]
    public bool hapticsEnabled = true;
    public float hapticMaxDistance = 2.5f;
    public float hapticContinuousDistance = 0.3f;
    public float sideThreshold = 0.25f;

    [Header("Debug")]
    public bool visualizeSpots = true;

    // ──── runtime state ────
    private MRUKRoom room;
    private float floorY;

    private List<GameObject> hiddenChars = new();
    private List<GameObject> foundChars = new();
    private List<GameObject> placedChars = new();
    private List<Vector3> debugUsedSpots = new();

    // grab state
    private GameObject grabbedChar = null;
    private bool isGrabbing = false;

    // UI
    private Canvas fadeCanvas;
    private Image fadeImage;
    private TextMeshProUGUI centerText;
    private List<GameObject> worldTexts = new();

    // Haptics
    private float leftPulseTimer = 0f;
    private float rightPulseTimer = 0f;
    private bool leftPulseOn = false;
    private bool rightPulseOn = false;

    // Sonar visuals
    private GameObject leftSonar;
    private GameObject rightSonar;
    private ParticleSystem leftSonarPS;
    private ParticleSystem rightSonarPS;
    private float leftHapticIntensity = 0f;
    private float rightHapticIntensity = 0f;

    // Place zone visual
    private GameObject placeZoneVisual;

    void Start()
    {
        BuildUI();
        CreateSonarEffects();
        CreatePlaceZoneVisual();
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

        AttachCanvasToPlayer();

        StartCoroutine(GameFlow());
    }

    // ═══════════════════════════
    // UI SETUP
    // ═══════════════════════════

    void BuildUI()
    {
        var canvasGO = new GameObject("FadeCanvas");

        fadeCanvas = canvasGO.AddComponent<Canvas>();
        fadeCanvas.renderMode = RenderMode.WorldSpace;
        fadeCanvas.sortingOrder = 9999;

        var canvasRT = canvasGO.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(2000, 1200);
        canvasRT.pivot = new Vector2(0.5f, 0.5f);
        canvasRT.anchoredPosition = Vector2.zero;

        var imgGO = new GameObject("FadeImage");
        imgGO.transform.SetParent(canvasGO.transform, false);
        fadeImage = imgGO.AddComponent<Image>();
        fadeImage.color = new Color(0, 0, 0, 0);
        fadeImage.raycastTarget = false;

        var imgRT = fadeImage.rectTransform;
        imgRT.anchorMin = Vector2.zero;
        imgRT.anchorMax = Vector2.one;
        imgRT.pivot = new Vector2(0.5f, 0.5f);
        imgRT.anchoredPosition = Vector2.zero;
        imgRT.offsetMin = new Vector2(-2000, -2000);
        imgRT.offsetMax = new Vector2(2000, 2000);

        var txtGO = new GameObject("CenterText");
        txtGO.transform.SetParent(canvasGO.transform, false);

        centerText = txtGO.AddComponent<TextMeshProUGUI>();
        centerText.alignment = TextAlignmentOptions.Center;
        centerText.horizontalAlignment = HorizontalAlignmentOptions.Center;
        centerText.verticalAlignment = VerticalAlignmentOptions.Middle;
        centerText.fontSize = 200;
        centerText.color = Color.white;
        centerText.text = "";
        centerText.fontStyle = FontStyles.Bold;
        centerText.raycastTarget = false;

        var txtRT = centerText.rectTransform;
        txtRT.anchorMin = new Vector2(0.5f, 0.5f);
        txtRT.anchorMax = new Vector2(0.5f, 0.5f);
        txtRT.pivot = new Vector2(0.5f, 0.5f);
        txtRT.sizeDelta = new Vector2(2000, 600);
        txtRT.anchoredPosition = Vector2.zero;
        txtRT.localPosition = new Vector3(0, 0, -0.01f);
        txtRT.localScale = Vector3.one;
    }

    void AttachCanvasToPlayer()
    {
        if (fadeCanvas == null) return;

        Transform anchor = playerHead != null ? playerHead : Camera.main.transform;
        if (anchor == null) return;

        fadeCanvas.transform.SetParent(anchor, false);
        fadeCanvas.transform.localPosition = new Vector3(0f, 0f, 1.5f);
        fadeCanvas.transform.localRotation = Quaternion.identity;
        fadeCanvas.transform.localScale = Vector3.one * 0.002f;
    }

    GameObject CreateWorldText(Transform target, string content)
    {
        var go = new GameObject("WorldText_" + target.name);
        go.transform.SetParent(target, false);
        go.transform.localPosition = Vector3.up * worldTextHeight;
        go.transform.localScale = Vector3.one * worldTextScale;

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;

        var txtGO = new GameObject("Text");
        txtGO.transform.SetParent(go.transform, false);
        var tmp = txtGO.AddComponent<TextMeshProUGUI>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 80;
        tmp.color = Color.white;
        tmp.text = content;
        tmp.fontStyle = FontStyles.Bold;

        var rt = tmp.rectTransform;
        rt.sizeDelta = new Vector2(800, 200);
        rt.anchoredPosition = Vector2.zero;

        tmp.outlineColor = Color.black;
        tmp.outlineWidth = 0.3f;

        return go;
    }

    // ═══════════════════════════
    // SONAR VISUALS
    // ═══════════════════════════

    void CreateSonarEffects()
    {
        leftSonar = CreateSonarParticle("LeftSonar");
        rightSonar = CreateSonarParticle("RightSonar");

        leftSonarPS = leftSonar.GetComponent<ParticleSystem>();
        rightSonarPS = rightSonar.GetComponent<ParticleSystem>();
    }

    GameObject CreateSonarParticle(string name)
    {
        var go = new GameObject(name);
        var ps = go.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.loop = true;
        main.startLifetime = 0.8f;
        main.startSpeed = 0f;
        main.startSize = 0.05f;
        main.maxParticles = 20;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = new Color(0.3f, 0.7f, 1f, 0.6f);
        main.playOnAwake = false;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new ParticleSystem.Burst[] {
            new ParticleSystem.Burst(0f, 8)
        });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.01f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
            new AnimationCurve(
                new Keyframe(0f, 0.2f),
                new Keyframe(0.5f, 1f),
                new Keyframe(1f, 1.5f)
            ));

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[] {
                new GradientColorKey(new Color(0.3f, 0.7f, 1f), 0f),
                new GradientColorKey(new Color(0.5f, 0.9f, 1f), 0.5f),
                new GradientColorKey(new Color(0.3f, 0.7f, 1f), 1f)
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(0.8f, 0f),
                new GradientAlphaKey(0.4f, 0.5f),
                new GradientAlphaKey(0f, 1f)
            }
        );
        colorOverLifetime.color = grad;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.material = new Material(Shader.Find("Particles/Standard Unlit"));
        renderer.material.SetFloat("_Mode", 1);
        renderer.material.SetColor("_Color", new Color(0.3f, 0.7f, 1f, 0.6f));

        return go;
    }

    void UpdateSonarVisuals()
    {
        if (leftController != null)
            leftSonar.transform.position = leftController.position;
        if (rightController != null)
            rightSonar.transform.position = rightController.position;

        UpdateSonarPulse(leftSonarPS, leftHapticIntensity, leftPulseOn);
        UpdateSonarPulse(rightSonarPS, rightHapticIntensity, rightPulseOn);
    }

    void UpdateSonarPulse(ParticleSystem ps, float intensity, bool pulseOn)
    {
        if (intensity <= 0f)
        {
            if (ps.isPlaying) ps.Stop();
            return;
        }

        var main = ps.main;

        float alpha = Mathf.Lerp(0.2f, 0.8f, intensity);
        main.startColor = new Color(0.3f, 0.7f, 1f, alpha);
        main.startSize = Mathf.Lerp(0.03f, 0.1f, intensity);
        main.startLifetime = Mathf.Lerp(0.3f, 0.8f, intensity);

        if (pulseOn && !ps.isPlaying)
        {
            ps.Play();
        }
        else if (!pulseOn && intensity < 0.9f)
        {
            if (ps.isPlaying) ps.Stop();
        }
    }

    void CreatePlaceZoneVisual()
    {
        if (placeZone == null) return;

        placeZoneVisual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        placeZoneVisual.name = "PlaceZoneVisual";
        placeZoneVisual.transform.SetParent(placeZone, false);
        placeZoneVisual.transform.localPosition = Vector3.zero;
        placeZoneVisual.transform.localScale = new Vector3(
            placeRadius * 2f, 0.01f, placeRadius * 2f);

        var col = placeZoneVisual.GetComponent<Collider>();
        if (col != null) Destroy(col);

        var mat = new Material(Shader.Find("Particles/Standard Unlit"));
        mat.SetColor("_Color", new Color(0.3f, 1f, 0.3f, 0.3f));
        placeZoneVisual.GetComponent<Renderer>().material = mat;

        placeZoneVisual.SetActive(false);
    }

    // ═══════════════════════════
    // CONFETTI
    // ═══════════════════════════

    void SpawnConfetti(Vector3 position)
    {
        if (confettiPrefab == null) return;

        var go = Instantiate(confettiPrefab, position, Quaternion.identity);
        Destroy(go, 5f);
    }

    // ═══════════════════════════
    // GAME FLOW
    // ═══════════════════════════

    IEnumerator GameFlow()
    {
        yield return new WaitForSeconds(hideDelay);

        SpawnCharactersInFront();

        yield return StartCoroutine(ShowIntroMessage());

        yield return StartCoroutine(FadeToBlack());

        SetCharactersVisible(false);

        if (passthroughLayer != null)
            passthroughLayer.hidden = true;

        yield return StartCoroutine(ShowCountdown());

        HideAll();

        SetCharactersVisible(true);

        if (passthroughLayer != null)
            passthroughLayer.hidden = false;

        yield return StartCoroutine(FadeFromBlack());

        yield return StartCoroutine(ShowCenterMessage("Find us!", revealMessageDuration));

        Debug.Log("Hunt begins — " + hiddenChars.Count + " hidden");
    }

    IEnumerator EndGameSequence()
    {
        yield return new WaitForSeconds(1f);

        yield return StartCoroutine(FadeToBlack());

        centerText.text = "You found them all!";
        yield return new WaitForSeconds(3f);
        centerText.text = "";

        yield return StartCoroutine(FadeFromBlack());

        if (placeZoneVisual != null)
            placeZoneVisual.SetActive(false);
    }

    // ═══════════════════════════
    // FLOW STEPS
    // ═══════════════════════════

    void SpawnCharactersInFront()
    {
        Vector3 playerPos = GetPlayerHead();
        Vector3 forward = GetPlayerForward();
        forward.y = 0;
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward);

        int n = characters.Count;
        float totalWidth = (n - 1) * introSpacing;

        for (int i = 0; i < n; i++)
        {
            var c = characters[i];
            if (c == null) continue;

            float offset = (i * introSpacing) - (totalWidth * 0.5f);

            Vector3 pos = playerPos
                + forward * introDistance
                + right * offset;
            pos.y = playerPos.y;

            c.transform.position = pos;

            Vector3 toPlayer = playerPos - pos;
            toPlayer.y = 0f;
            if (toPlayer.magnitude > 0.01f)
                c.transform.rotation = Quaternion.LookRotation(toPlayer);
        }
    }

    IEnumerator ShowIntroMessage()
    {
        foreach (var old in worldTexts) if (old != null) Destroy(old);
        worldTexts.Clear();

        foreach (var c in characters)
        {
            if (c == null) continue;
            worldTexts.Add(CreateWorldText(c.transform, "We will hide now!"));
        }

        float elapsed = 0f;
        while (elapsed < introDuration)
        {
            FaceWorldTextsToPlayer();
            elapsed += Time.deltaTime;
            yield return null;
        }

        foreach (var wt in worldTexts) if (wt != null) Destroy(wt);
        worldTexts.Clear();
    }

    void FaceWorldTextsToPlayer()
    {
        Vector3 playerPos = GetPlayerHead();
        foreach (var wt in worldTexts)
        {
            if (wt == null) continue;
            Vector3 dir = wt.transform.position - playerPos;
            dir.y = 0f;
            if (dir.magnitude > 0.01f)
                wt.transform.rotation = Quaternion.LookRotation(dir);
        }
    }

    IEnumerator FadeToBlack()
    {
        yield return Fade(0f, 1f, fadeDuration);
    }

    IEnumerator FadeFromBlack()
    {
        yield return Fade(1f, 0f, fadeDuration);
    }

    IEnumerator Fade(float from, float to, float duration)
    {
        float elapsed = 0f;
        Color c = blackColor;
        while (elapsed < duration)
        {
            float a = Mathf.Lerp(from, to, elapsed / duration);
            c.a = a;
            fadeImage.color = c;
            elapsed += Time.deltaTime;
            yield return null;
        }
        c.a = to;
        fadeImage.color = c;
    }

    IEnumerator ShowCountdown()
    {
        centerText.text = "Hiding...";
        yield return new WaitForSeconds(countdownStepDuration);

        for (int i = 3; i >= 1; i--)
        {
            centerText.text = i.ToString();
            yield return new WaitForSeconds(countdownStepDuration);
        }

        centerText.text = "";
    }

    IEnumerator ShowCenterMessage(string msg, float duration)
    {
        centerText.text = msg;
        yield return new WaitForSeconds(duration);
        centerText.text = "";
    }

    void SetCharactersVisible(bool visible)
    {
        foreach (var c in characters)
        {
            if (c == null) continue;
            var renderers = c.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
                r.enabled = visible;
        }
    }

    // ═══════════════════════════
    // HIDE LOGIC
    // ═══════════════════════════

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

        allSpots.Sort((a, b) => b.score.CompareTo(a.score));

        hiddenChars.Clear();
        foundChars.Clear();
        placedChars.Clear();
        var used = new List<Vector3>();

        foreach (var c in characters)
        {
            bool placed = TryPlaceCharacter(c, allSpots, used, playerPos);
            if (!placed)
                Debug.LogWarning("FAILED: " + c.name);
        }
    }

    bool TryPlaceCharacter(GameObject c, List<HidingSpot> allSpots,
                           List<Vector3> used, Vector3 playerPos)
    {
        int topN = Mathf.Min(10, allSpots.Count);
        var topSpots = allSpots.GetRange(0, topN);
        Shuffle(topSpots);

        foreach (var spot in topSpots)
        {
            if (TooClose(spot.position, used, minSpotSeparation)) continue;
            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);
            Debug.Log("PLACED: " + c.name + " (score: " + spot.score.ToString("F1") + ")");
            return true;
        }

        foreach (var spot in allSpots)
        {
            if (TooClose(spot.position, used, minSpotSeparation)) continue;
            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);
            Debug.Log("PLACED: " + c.name + " (fallback)");
            return true;
        }

        float relaxedSep = minSpotSeparation * 0.5f;
        foreach (var spot in allSpots)
        {
            if (TooClose(spot.position, used, relaxedSep)) continue;
            PlaceCharacterAt(c, spot, playerPos);
            used.Add(spot.position);
            hiddenChars.Add(c);
            debugUsedSpots.Add(spot.position);
            Debug.Log("PLACED: " + c.name + " (relaxed)");
            return true;
        }

        return false;
    }

    void PlaceCharacterAt(GameObject c, HidingSpot spot, Vector3 playerPos)
    {
        c.transform.position = spot.position;
        Vector3 toPlayer = playerPos - spot.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude > 0.01f)
            c.transform.rotation = Quaternion.LookRotation(toPlayer);
    }

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

                Vector3 pos = anchor.transform.position + dirWorld * (edgeDist + 0.3f);
                pos.y = floorY + charHalfH;

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

        if (!anchor.VolumeBounds.HasValue) return valid;

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

            float edgeDist = GetBoxEdgeDistance(halfW, halfD, dirLocal);
            float tuckDistance = edgeDist + Random.Range(0.12f, 0.35f);

            Vector3 dirWorld = anchor.transform.TransformDirection(dirLocal);
            dirWorld.y = 0f;
            dirWorld.Normalize();

            Vector3 candidate = center + dirWorld * tuckDistance;

            if (!Physics.Raycast(candidate + Vector3.up * 0.5f, Vector3.down,
                out RaycastHit hit, 2f))
                continue;

            if (Mathf.Abs(hit.point.y - floorY) > 0.2f) continue;

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

    float CalcDiscretionScore(Vector3 spot, MRUKAnchor anchor,
                              Vector3 playerPos, float charHalfH)
    {
        float score = 0f;

        Vector3 furnitureToSpot = (spot - anchor.transform.position);
        Vector3 playerToFurniture = (anchor.transform.position - playerPos);
        furnitureToSpot.y = 0;
        playerToFurniture.y = 0;

        if (furnitureToSpot.magnitude > 0.01f && playerToFurniture.magnitude > 0.01f)
        {
            float dot = Vector3.Dot(furnitureToSpot.normalized, playerToFurniture.normalized);
            score += dot * 10f;
        }

        int occlusionCount = CountOccludedRays(spot, charHalfH, playerPos);
        score += occlusionCount * 2f;

        float wallProximity = GetWallProximity(spot, 1.0f);
        score += (1f - wallProximity) * 5f;

        Vector3 playerForward = GetPlayerForward();
        Vector3 toSpot = (spot - playerPos).normalized;
        toSpot.y = 0;
        playerForward.y = 0;
        if (playerForward.magnitude > 0.01f)
        {
            float viewDot = Vector3.Dot(playerForward.normalized, toSpot);
            score += (1f - viewDot) * 3f;
        }

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
        if (Vector3.Distance(playerPos, spot) < minPlayerDist) return "TOO_CLOSE";
        if (!room.IsPositionInRoom(spot, true)) return "OUTSIDE_ROOM";
        if (IsInsideFurniture(anchor, spot)) return "INSIDE_FURNITURE";

        foreach (var other in room.GetRoomAnchors())
        {
            if (other == anchor) continue;
            if (!IsFurniture(other)) continue;
            if (IsInsideFurniture(other, spot)) return "INSIDE_OTHER_FURNITURE";
        }

        Collider[] meshOverlaps = Physics.OverlapSphere(spot, 0.2f);
        foreach (var col in meshOverlaps)
        {
            MRUKAnchor overlapAnchor = col.GetComponentInParent<MRUKAnchor>();
            if (overlapAnchor != null && IsFurniture(overlapAnchor))
                return "OVERLAPPING_FURNITURE";
        }

        Collider[] near = Physics.OverlapSphere(spot, minClearance);
        foreach (var col in near)
        {
            if (col.GetComponentInParent<MRUKAnchor>() != null) continue;
            if (!col.isTrigger) return "TOO_TIGHT";
        }

        if (CountOccludedRays(spot, charHalfH, playerPos) < 3) return "VISIBLE";

        return "OK";
    }

    bool IsInsideFurniture(MRUKAnchor anchor, Vector3 worldPoint)
    {
        if (!anchor.VolumeBounds.HasValue) return false;
        Bounds b = anchor.VolumeBounds.Value;
        Vector3 local = anchor.transform.InverseTransformPoint(worldPoint);
        Vector3 half = b.extents - Vector3.one * 0.05f;
        return Mathf.Abs(local.x - b.center.x) < half.x &&
               Mathf.Abs(local.y - b.center.y) < half.y &&
               Mathf.Abs(local.z - b.center.z) < half.z;
    }

    // ═══════════════════════════
    // DISCOVERY + GRAB + PLACE
    // ═══════════════════════════

    void Update()
    {
        if (hapticsEnabled)
            UpdateHaptics();

        UpdateSonarVisuals();
        UpdateGrab();
        UpdatePlaceZoneVisual();

        if (hiddenChars.Count == 0 && foundChars.Count == 0 && grabbedChar == null)
            return;

        if (hiddenChars.Count > 0 &&
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
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
        foundChars.Add(c);

        SpawnConfetti(c.transform.position);

        Debug.Log("FOUND: " + c.name);

        if (placeZoneVisual != null && placeZone != null)
            placeZoneVisual.SetActive(true);
    }

    // ═══════════════════════════
    // GRAB SYSTEM
    // ═══════════════════════════

    void UpdateGrab()
    {
        if (rightController == null) return;

        bool gripHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool gripDown = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool gripUp = OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);

        if (gripDown && grabbedChar == null)
        {
            GameObject closest = null;
            float closestDist = grabDistance;

            foreach (var c in foundChars)
            {
                if (c == null) continue;
                float dist = Vector3.Distance(rightController.position, c.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = c;
                }
            }

            if (closest != null)
            {
                grabbedChar = closest;
                isGrabbing = true;
                foundChars.Remove(closest);
                Debug.Log("GRABBED: " + closest.name);
            }
        }

        if (isGrabbing && grabbedChar != null && gripHeld)
        {
            grabbedChar.transform.position = rightController.position +
                rightController.forward * 0.2f;

            Vector3 toPlayer = GetPlayerHead() - grabbedChar.transform.position;
            toPlayer.y = 0f;
            if (toPlayer.magnitude > 0.01f)
                grabbedChar.transform.rotation = Quaternion.LookRotation(toPlayer);
        }

        if (gripUp && isGrabbing && grabbedChar != null)
        {
            if (placeZone != null &&
                Vector3.Distance(grabbedChar.transform.position, placeZone.position) < placeRadius)
            {
                OnPlaced(grabbedChar);
            }
            else
            {
                foundChars.Add(grabbedChar);
                Debug.Log("DROPPED: " + grabbedChar.name);
            }

            grabbedChar = null;
            isGrabbing = false;
        }
    }

    void OnPlaced(GameObject c)
    {
        c.transform.position = placeZone.position;

        Vector3 toPlayer = GetPlayerHead() - c.transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude > 0.01f)
            c.transform.rotation = Quaternion.LookRotation(toPlayer);

        placedChars.Add(c);

        SpawnConfetti(c.transform.position + Vector3.up * 0.3f);

        Debug.Log("PLACED: " + c.name + " (" + placedChars.Count + "/" + characters.Count + ")");

        if (placedChars.Count >= characters.Count)
        {
            StartCoroutine(EndGameSequence());
        }
    }

    void UpdatePlaceZoneVisual()
    {
        if (placeZoneVisual == null || placeZone == null) return;

        bool shouldShow = (foundChars.Count > 0 || grabbedChar != null) &&
                          placedChars.Count < characters.Count;
        placeZoneVisual.SetActive(shouldShow);

        if (!shouldShow) return;

        float pulse = 1f + Mathf.Sin(Time.time * 3f) * 0.15f;
        placeZoneVisual.transform.localScale = new Vector3(
            placeRadius * 2f * pulse, 0.01f, placeRadius * 2f * pulse);

        bool charNear = false;
        if (grabbedChar != null &&
            Vector3.Distance(grabbedChar.transform.position, placeZone.position) < placeRadius * 2f)
            charNear = true;

        var mat = placeZoneVisual.GetComponent<Renderer>().material;
        Color col = charNear
            ? new Color(0.3f, 1f, 0.3f, 0.6f)
            : new Color(0.3f, 1f, 0.3f, 0.25f);
        mat.SetColor("_Color", col);
    }

    // ═══════════════════════════
    // HAPTICS
    // ═══════════════════════════

    void UpdateHaptics()
    {
        if (hiddenChars.Count == 0)
        {
            StopHaptics(OVRInput.Controller.LTouch);
            StopHaptics(OVRInput.Controller.RTouch);
            leftHapticIntensity = 0f;
            rightHapticIntensity = 0f;
            return;
        }

        Vector3 playerPos = GetPlayerHead();
        Vector3 forward = GetPlayerForward();
        forward.y = 0f;
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        float leftClosest = float.MaxValue;
        float rightClosest = float.MaxValue;

        foreach (var c in hiddenChars)
        {
            if (c == null) continue;

            Vector3 toChar = c.transform.position - playerPos;
            toChar.y = 0f;

            float dist = toChar.magnitude;
            if (dist > hapticMaxDistance) continue;

            float sideDot = Vector3.Dot(toChar.normalized, right);

            if (sideDot > sideThreshold)
            {
                if (dist < rightClosest) rightClosest = dist;
            }
            else if (sideDot < -sideThreshold)
            {
                if (dist < leftClosest) leftClosest = dist;
            }
            else
            {
                if (dist < leftClosest) leftClosest = dist;
                if (dist < rightClosest) rightClosest = dist;
            }
        }

        leftHapticIntensity = leftClosest < hapticMaxDistance
            ? Mathf.InverseLerp(hapticMaxDistance, hapticContinuousDistance, leftClosest)
            : 0f;
        rightHapticIntensity = rightClosest < hapticMaxDistance
            ? Mathf.InverseLerp(hapticMaxDistance, hapticContinuousDistance, rightClosest)
            : 0f;

        ApplyHaptics(OVRInput.Controller.LTouch, leftClosest, ref leftPulseTimer, ref leftPulseOn);
        ApplyHaptics(OVRInput.Controller.RTouch, rightClosest, ref rightPulseTimer, ref rightPulseOn);
    }

    void ApplyHaptics(OVRInput.Controller controller, float distance,
                      ref float pulseTimer, ref bool pulseOn)
    {
        if (distance > hapticMaxDistance)
        {
            StopHaptics(controller);
            pulseTimer = 0f;
            pulseOn = false;
            return;
        }

        if (distance < hapticContinuousDistance)
        {
            OVRInput.SetControllerVibration(1f, 0.9f, controller);
            pulseTimer = 0f;
            pulseOn = true;
            return;
        }

        float t = Mathf.InverseLerp(hapticMaxDistance, hapticContinuousDistance, distance);
        float pulseInterval = Mathf.Lerp(0.7f, 0.1f, t);
        float amplitude = Mathf.Lerp(0.2f, 0.85f, t);
        float frequency = Mathf.Lerp(0.4f, 1f, t);

        pulseTimer += Time.deltaTime;
        if (pulseTimer >= pulseInterval)
        {
            pulseTimer = 0f;
            pulseOn = !pulseOn;
        }

        if (pulseOn)
            OVRInput.SetControllerVibration(frequency, amplitude, controller);
        else
            OVRInput.SetControllerVibration(0f, 0f, controller);
    }

    void StopHaptics(OVRInput.Controller controller)
    {
        OVRInput.SetControllerVibration(0f, 0f, controller);
    }

    void OnDisable()
    {
        StopHaptics(OVRInput.Controller.LTouch);
        StopHaptics(OVRInput.Controller.RTouch);
    }

    // ═══════════════════════════
    // HELPERS
    // ═══════════════════════════

    Vector3 GetPlayerHead()
    {
        if (playerHead != null) return playerHead.position;
        return Camera.main.transform.position;
    }

    Vector3 GetPlayerForward()
    {
        if (playerHead != null) return playerHead.forward;
        if (Camera.main != null) return Camera.main.transform.forward;
        return Vector3.forward;
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
            if (Vector3.Distance(spot, u) < min) return true;
        return false;
    }

    bool IsFurniture(MRUKAnchor anchor)
    {
        var skip = new HashSet<string>
        {
            "FLOOR","CEILING","WALL_FACE","WINDOW_FRAME","DOOR_FRAME"
        };
        foreach (var l in anchor.AnchorLabels)
            if (skip.Contains(l.ToUpper())) return false;
        return true;
    }

    List<MRUKAnchor> GetFurniture()
    {
        var list = new List<MRUKAnchor>();
        if (room == null) return list;
        foreach (var a in room.GetRoomAnchors())
            if (IsFurniture(a)) list.Add(a);
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
            if (c != null) Gizmos.DrawWireSphere(c.transform.position, 0.15f);

        Gizmos.color = Color.red;
        Vector3 head = GetPlayerHead();
        foreach (var c in hiddenChars)
            if (c != null) Gizmos.DrawLine(head, c.transform.position);

        if (placeZone != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(placeZone.position, placeRadius);
        }
    }
}

public class HidingSpot
{
    public Vector3 position;
    public Vector3 anchorPos;
    public float score;
}