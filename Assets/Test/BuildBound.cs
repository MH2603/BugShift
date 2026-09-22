using UnityEngine;
using System.Collections;

public class BuildBound : MonoBehaviour
{

    public MeshFilter _meshFilter;
    public SkinnedMeshRenderer _skinnedMeshRenderer;

     // Bound box exactly as baked in the prefab (SkinnedMeshRenderer "m_AABB"): expressed in the
    // root bone bind space.
    private Vector3 _bakedBoundCenter;
    private Vector3 _bakedBoundExtents;
    private bool _bakedBoundCenterCaptured;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if(!_meshFilter) _meshFilter = GetComponent<MeshFilter>();
        if(!_skinnedMeshRenderer) _skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();

        ReBuildSkinnedBoundCenter();
    }

    [ContextMenu("Set Bound Center")]
    public void ReBuildSkinnedBoundCenter()
    {
        // BMLogger.Log( $"[MH] start update skinned mesh bound center of {gameObject.name}");
        var skinnedMeshRenderer = _skinnedMeshRenderer != null
            ? _skinnedMeshRenderer
            : GetComponent<SkinnedMeshRenderer>();
        if (skinnedMeshRenderer == null)
        {
            return;
        }

        if (!_bakedBoundCenterCaptured)
        {
            // Keep the prefab value: it is the only source of truth for the bind space bound.
            // Capture center *and* extents: localBounds is overwritten below, so reading
            // bounds.extents on a later run would refit an already refitted box (it keeps growing).
            _bakedBoundCenter = skinnedMeshRenderer.localBounds.center;
            _bakedBoundExtents = skinnedMeshRenderer.localBounds.extents;
            _bakedBoundCenterCaptured = true;
        }

        var rootBone = skinnedMeshRenderer.rootBone;
        if (rootBone == null)
        {
            return; // No bone assigned yet - keep the baked bound until one is.
        }

        // BMLogger.Log( $"[MH] updated skinned mesh bound center of {gameObject.name}");

        // bind (root bone) space -> world -> this transform local space
        // var bindSpaceToRenderer = transform.worldToLocalMatrix * rootBone.localToWorldMatrix;
        // var bindSpaceToRenderer = rootBone.localToWorldMatrix * transform.worldToLocalMatrix ;

        // var bounds = skinnedMeshRenderer.localBounds;
        // var extents = bounds.extents;
        // bounds.center = bindSpaceToRenderer.MultiplyPoint3x4(_bakedBoundCenter);
        // // Refit the axis aligned box: a rotated/scaled root bone grows the extents.
        // bounds.extents = new Vector3(
        //     Mathf.Abs(bindSpaceToRenderer.m00) * extents.x + Mathf.Abs(bindSpaceToRenderer.m01) * extents.y + Mathf.Abs(bindSpaceToRenderer.m02) * extents.z,
        //     Mathf.Abs(bindSpaceToRenderer.m10) * extents.x + Mathf.Abs(bindSpaceToRenderer.m11) * extents.y + Mathf.Abs(bindSpaceToRenderer.m12) * extents.z,
        //     Mathf.Abs(bindSpaceToRenderer.m20) * extents.x + Mathf.Abs(bindSpaceToRenderer.m21) * extents.y + Mathf.Abs(bindSpaceToRenderer.m22) * extents.z);

        // skinnedMeshRenderer.localBounds = bounds;

        StartCoroutine( OffsetBoundCenterBaseOnRootBone(skinnedMeshRenderer));
    }

    IEnumerator OffsetBoundCenterBaseOnRootBone(SkinnedMeshRenderer skinnedMeshRenderer)
    {
        yield return new WaitForSeconds(2f); // wait to anim run and Root Bone correct pos

        if (skinnedMeshRenderer == null)
        {
            yield break;
        }

        var bounds = skinnedMeshRenderer.localBounds;
        var sharedMesh = skinnedMeshRenderer.sharedMesh;
        var hasMeshBounds = !_bakedBoundCenterCaptured && sharedMesh != null;

        // Bound source: the bound baked in the prefab (captured in bind space by
        // ReBuildSkinnedBoundCenter), or the mesh data itself when no prefab value was captured
        // (renderer/mesh built at runtime).
        var bindSpaceCenter = _bakedBoundCenterCaptured
            ? _bakedBoundCenter
            : hasMeshBounds ? sharedMesh.bounds.center : bounds.center;
        var bindSpaceExtents = _bakedBoundCenterCaptured
            ? _bakedBoundExtents
            : hasMeshBounds ? sharedMesh.bounds.extents : bounds.extents;

        var rootBone = skinnedMeshRenderer.rootBone;
        if (rootBone == null)
        {
            // No root bone assigned: keep the bound in bind space (nothing to convert to).
            bounds.center = bindSpaceCenter;
            bounds.extents = bindSpaceExtents;
            skinnedMeshRenderer.localBounds = bounds;
            yield break;
        }

        // this transform local space -> world -> root bone space.
        // Unity interprets localBounds in the *root bone* space, so what we assign must be the
        // box expressed in that space.
        var toBoneSpace = rootBone.worldToLocalMatrix * transform.localToWorldMatrix;
        Debug.Log($"{toBoneSpace}");

        bounds.center = toBoneSpace.MultiplyPoint3x4(bindSpaceCenter);

        // Refit the axis aligned box: the converted box is rotated/scaled/sheared, so the AABB
        // enclosing it has extents = |toBoneSpace| . bindSpaceExtents (abs row sums of the 3x3
        // part) -- exactly the 8 transformed corners' AABB. Without this the extents would stay in
        // bind space and no longer match the moved center.
        bounds.extents = new Vector3(
            Mathf.Abs(toBoneSpace.m00) * bindSpaceExtents.x + Mathf.Abs(toBoneSpace.m01) * bindSpaceExtents.y + Mathf.Abs(toBoneSpace.m02) * bindSpaceExtents.z,
            Mathf.Abs(toBoneSpace.m10) * bindSpaceExtents.x + Mathf.Abs(toBoneSpace.m11) * bindSpaceExtents.y + Mathf.Abs(toBoneSpace.m12) * bindSpaceExtents.z,
            Mathf.Abs(toBoneSpace.m20) * bindSpaceExtents.x + Mathf.Abs(toBoneSpace.m21) * bindSpaceExtents.y + Mathf.Abs(toBoneSpace.m22) * bindSpaceExtents.z);

        skinnedMeshRenderer.localBounds = bounds;
    }
}
