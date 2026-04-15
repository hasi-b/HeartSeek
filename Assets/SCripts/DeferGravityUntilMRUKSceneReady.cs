using System.Collections;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// If the <see cref="Rigidbody"/> has <c>Use Gravity</c> enabled in the inspector, this keeps
/// <see cref="Rigidbody.useGravity"/> off until MRUK has finished loading the scene and (optionally)
/// EffectMesh has spawned at least one solid collider. Avoids falling before room geometry exists.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(-100)]
[DisallowMultipleComponent]
public class DeferGravityUntilMRUKSceneReady : MonoBehaviour
{
    [Tooltip("Wait until the EffectMesh root has at least one enabled, non-trigger collider with valid geometry.")]
    [SerializeField] bool waitForEffectMeshColliders = true;

    [Tooltip("If set, only this hierarchy is checked for colliders. Otherwise GameObject.Find by name below.")]
    [SerializeField] Transform effectMeshRootOverride;

    [Tooltip("Used when effectMeshRootOverride is null.")]
    [SerializeField] string effectMeshRootName = "[BuildingBlock] EffectMesh";

    [Tooltip("After MRUK scene load, stop waiting and apply gravity anyway.")]
    [SerializeField] float maxWaitForCollidersSeconds = 15f;

    Rigidbody _rb;
    bool _wantedGravity;
    bool _gravityApplied;
    bool _waitingCoroutine;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _wantedGravity = _rb.useGravity;
        if (!_wantedGravity)
            return;

        _rb.useGravity = false;
    }

    void Start()
    {
        if (!_wantedGravity || _gravityApplied)
            return;

        if (MRUK.Instance == null)
        {
            StartCoroutine(ApplyGravityWithoutMRUK());
            return;
        }

        MRUK.Instance.RegisterSceneLoadedCallback(OnMRUKSceneLoaded);

        if (MRUK.Instance.GetCurrentRoom() != null)
            OnMRUKSceneLoaded();
    }

    void OnMRUKSceneLoaded()
    {
        if (!_wantedGravity || _gravityApplied || _waitingCoroutine)
            return;

        _waitingCoroutine = true;
        StartCoroutine(AfterSceneLoadedFlow());
    }

    IEnumerator AfterSceneLoadedFlow()
    {
        if (waitForEffectMeshColliders)
        {
            float deadline = Time.realtimeSinceStartup + maxWaitForCollidersSeconds;
            while (Time.realtimeSinceStartup < deadline && !_gravityApplied)
            {
                if (HasReadyEffectMeshColliders())
                    break;
                yield return null;
            }
        }
        else
        {
            yield return null;
            yield return new WaitForFixedUpdate();
        }

        _waitingCoroutine = false;
        ApplyGravity();
    }

    IEnumerator ApplyGravityWithoutMRUK()
    {
        yield return null;
        yield return new WaitForFixedUpdate();
        Debug.LogWarning(
            "[DeferGravityUntilMRUKSceneReady] MRUK.Instance missing; enabling gravity without MRUK wait.",
            this);
        ApplyGravity();
    }

    bool HasReadyEffectMeshColliders()
    {
        Transform root = effectMeshRootOverride;
        if (root == null && !string.IsNullOrEmpty(effectMeshRootName))
        {
            var go = GameObject.Find(effectMeshRootName);
            if (go != null)
                root = go.transform;
        }

        if (root == null)
            return false;

        foreach (var col in root.GetComponentsInChildren<Collider>(true))
        {
            if (!col.enabled || col.isTrigger)
                continue;
            if (col is MeshCollider mc && mc.sharedMesh == null)
                continue;
            return true;
        }

        return false;
    }

    void ApplyGravity()
    {
        if (_gravityApplied || !_wantedGravity)
            return;

        _gravityApplied = true;
        _rb.useGravity = true;
    }
}
