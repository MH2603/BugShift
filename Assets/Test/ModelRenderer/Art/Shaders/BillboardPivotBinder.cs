using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Feeds the per-quad billboard pivot of <c>BrewMonster/UnlitWithShadows</c>
/// (Assets/Test/ModelRenderer/Art/Shaders/CustomUnlitShader.shader) for meshes that are a set of
/// leaf cards, such as a tree canopy: the shader's billboard otherwise rotates every vertex around
/// the mesh origin, which slides all the cards together instead of turning each one in place.
///
/// The component reads the mesh triangles once, computes one object-space pivot per vertex - the
/// centre of the quad or triangle that vertex belongs to - uploads them into a
/// <c>StructuredBuffer&lt;float3&gt;</c> and binds that buffer to every renderer below this object.
/// The shader indexes the buffer with <c>SV_VertexID</c>, so the material must have the
/// <c>_BILLBOARD_PER_VERTEX_PIVOT</c> keyword on: the "Pivot Per Quad/Triangle" toggle next to
/// "Billboard" in the material inspector.
///
/// Requirements, enforced or warned about below:
/// - the mesh must be Read/Write enabled in its importer, otherwise its vertices cannot be read;
/// - the renderer must not be static-batched or GPU-instanced: both rewrite the vertex buffer, after
///   which <c>SV_VertexID</c> is no longer the mesh vertex index. Dynamic batching does the same; add
///   <c>"DisableBatching" = "True"</c> to the shader's SubShader tags if you want the shader to
///   refuse it everywhere instead of relying on this check;
/// - every submesh is read: submeshes only partition the index buffer, while the pivot buffer is indexed
///   per vertex, so a vertex that only the leaves submesh uses would otherwise keep the origin as its
///   pivot and slide to the mesh origin;
/// - no vertex may be shared between two cards (a stitched leaf strip, or two submeshes referencing the
///   same vertex). One pivot per vertex cannot serve both cards, and that case is reported as a warning;
/// - a SkinnedMeshRenderer is fine as long as nothing re-bakes the mesh on the CPU (BakeMesh), which
///   would change the vertex order out from under the buffer.
/// </summary>
[DisallowMultipleComponent]
public class BillboardPivotBinder : MonoBehaviour
{
    /// <summary>Which group of vertices shares one pivot.</summary>
    public enum PivotSource
    {
        /// <summary>Both triangles of a card share one pivot, so the card cannot tear along its diagonal.</summary>
        Quad = 0,

        /// <summary>Every triangle gets its own pivot, for meshes whose cards really are separate triangles.</summary>
        Triangle = 1
    }

    /// <summary>float3 per vertex.</summary>
    private const int PivotStride = 3 * sizeof(float);

    /// <summary>Corners in a card. A pair of triangles counts as a card when it has exactly this many unique indices.</summary>
    private const int QuadCornerCount = 4;

    private const int TriangleStride = 3;
    private const int QuadTriangleStride = 2 * TriangleStride;

    /// <summary>Corner merge tolerance, as a fraction of the pair's own size, plus an absolute floor.</summary>
    private const float CornerMergeRatio = 1e-3f;
    private const float MinCornerSqrEpsilon = 1e-12f;

    private const string PivotBufferName = "_BillboardPivotBuffer";
    private const string NormalBufferName = "_BillboardCardNormalBuffer";
    private const string BillboardPropertyName = "_Billboard";
    private const string PerVertexPivotKeyword = "_BILLBOARD_PER_VERTEX_PIVOT";

    private static readonly int PivotBufferId = Shader.PropertyToID(PivotBufferName);
    private static readonly int NormalBufferId = Shader.PropertyToID(NormalBufferName);

    /// <summary>One pivot buffer per (mesh, pivot source), ref-counted so a canopy that instances one mesh uploads it once.</summary>
    private static readonly Dictionary<(int meshId, PivotSource source), SharedPivot> SharedPivots =
        new Dictionary<(int, PivotSource), SharedPivot>();

    [Tooltip("Quad: hai tam giác của cùng một card dùng chung một tâm, card không bị nứt dọc đường chéo. Triangle: mỗi tam giác một tâm.")]
    [SerializeField] private PivotSource pivotSource = PivotSource.Quad;

    [Tooltip("Gán buffer cho cả các Renderer đang tắt trong hierarchy.")]
    [SerializeField] private bool includeInactive = false;

    [Tooltip("Báo warning khi material thiếu keyword _BILLBOARD_PER_VERTEX_PIVOT, khi renderer bị static batch, hoặc khi bật GPU Instancing.")]
    [SerializeField] private bool logWarnings = true;

    private sealed class SharedPivot
    {
        /// <summary>Object-space centre of the quad/triangle each vertex belongs to.</summary>
        public GraphicsBuffer Buffer;

        /// <summary>Object-space plane normal of that same quad/triangle, taken from its winding.</summary>
        public GraphicsBuffer NormalBuffer;

        public int Users;
    }

    private sealed class Binding
    {
        public Renderer Renderer;
        public SharedPivot Pivot;

        /// <summary>The renderer's property block before we added the buffer, restored on unbind.</summary>
        public MaterialPropertyBlock PreviousBlock;
    }

    private readonly List<Binding> bindings = new List<Binding>();

    /// <summary>Corner positions of the pair of triangles under test, merged by position. Build-time scratch.</summary>
    private readonly List<Vector3> cardCorners = new List<Vector3>(QuadTriangleStride);

    /// <summary>Card id that claimed each vertex, -1 for none. Build-time state, used to spot shared corners.</summary>
    private int[] claimedByCard = System.Array.Empty<int>();
    private int nextCardId;
    private bool sharedCornerDetected;

    /// <summary>Number of renderers currently holding a pivot buffer, for diagnostics.</summary>
    public int BoundRendererCount => bindings.Count;

    private void OnEnable()
    {
        Bind();
    }

    private void OnDisable()
    {
        Unbind();
    }

    private void OnValidate()
    {
        // Switching between quad and triangle pivots needs a different buffer, so redo the binding.
        if (Application.isPlaying && isActiveAndEnabled)
        {
            Bind();
        }
    }

    /// <summary>
    /// (Re)binds every renderer below this object. Call it after swapping a mesh at runtime, since the
    /// buffer is built from the mesh that was current when the component was enabled.
    /// </summary>
    public void Bind()
    {
        Unbind();

        foreach (Renderer target in GetComponentsInChildren<Renderer>(includeInactive))
        {
            Mesh mesh = GetMesh(target);
            if (mesh == null || mesh.vertexCount == 0)
            {
                continue;
            }

            SharedPivot pivot = Acquire(mesh);
            if (pivot == null)
            {
                continue;
            }

            var previous = new MaterialPropertyBlock();
            target.GetPropertyBlock(previous);

            var block = new MaterialPropertyBlock();
            target.GetPropertyBlock(block);
            block.SetBuffer(PivotBufferId, pivot.Buffer);
            block.SetBuffer(NormalBufferId, pivot.NormalBuffer);
            target.SetPropertyBlock(block);

            bindings.Add(new Binding { Renderer = target, Pivot = pivot, PreviousBlock = previous });

            if (logWarnings)
            {
                WarnAboutSetup(target);
            }
        }
    }

    private void Unbind()
    {
        foreach (Binding binding in bindings)
        {
            if (binding.Renderer != null)
            {
                binding.Renderer.SetPropertyBlock(binding.PreviousBlock);
            }

            Release(binding.Pivot);
        }

        bindings.Clear();
    }

    /// <summary>Drops the buffers left over from a previous play session when the domain is not reloaded.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSharedPivots()
    {
        foreach (SharedPivot pivot in SharedPivots.Values)
        {
            pivot.Buffer?.Dispose();
            pivot.NormalBuffer?.Dispose();
        }

        SharedPivots.Clear();
    }

    private SharedPivot Acquire(Mesh mesh)
    {
        var key = (mesh.GetInstanceID(), pivotSource);
        if (SharedPivots.TryGetValue(key, out SharedPivot pivot))
        {
            pivot.Users++;
            return pivot;
        }

        pivot = BuildPivot(mesh);
        if (pivot == null)
        {
            return null;
        }

        SharedPivots.Add(key, pivot);
        return pivot;
    }

    private void Release(SharedPivot pivot)
    {
        if (pivot == null)
        {
            return;
        }

        pivot.Users--;
        if (pivot.Users > 0)
        {
            return;
        }

        pivot.Buffer?.Dispose();
        pivot.NormalBuffer?.Dispose();

        // Drop the cache entry: the mesh may be replaced by the next generation, and a stale buffer
        // keyed by a dead instance id would never be reused anyway.
        var stale = new List<(int, PivotSource)>();
        foreach (KeyValuePair<(int meshId, PivotSource source), SharedPivot> pair in SharedPivots)
        {
            if (pair.Value == pivot)
            {
                stale.Add(pair.Key);
            }
        }

        foreach ((int, PivotSource) key in stale)
        {
            SharedPivots.Remove(key);
        }
    }

    private SharedPivot BuildPivot(Mesh mesh)
    {
        if (!mesh.isReadable)
        {
            Debug.LogError(
                $"Mesh '{mesh.name}' is not Read/Write enabled, so its vertices cannot be read to build a billboard pivot. " +
                "Enable Read/Write in the model importer, or remove BillboardPivotBinder from this object.",
                this);
            return null;
        }

        Vector3[] vertices = mesh.vertices;
        if (vertices.Length == 0 || mesh.subMeshCount == 0)
        {
            Debug.LogError($"Mesh '{mesh.name}' has no triangles, so there is no billboard pivot to build.", this);
            return null;
        }

        var pivots = new Vector3[vertices.Length];
        var normals = new Vector3[vertices.Length];

        claimedByCard = new int[vertices.Length];
        for (int i = 0; i < claimedByCard.Length; i++)
        {
            claimedByCard[i] = -1;
        }

        nextCardId = 0;
        sharedCornerDetected = false;

        // Every submesh, because the pivot buffer is indexed per vertex: a vertex used only by submesh 1
        // would otherwise keep the origin as its pivot and billboard itself onto the mesh origin.
        bool anyTriangle = false;
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            int[] indices = mesh.GetIndices(submesh);
            if (indices == null || indices.Length < TriangleStride)
            {
                continue;
            }

            anyTriangle = true;

            if (pivotSource == PivotSource.Triangle)
            {
                for (int i = 0; i + TriangleStride - 1 < indices.Length; i += TriangleStride)
                {
                    SetTrianglePivot(pivots, normals, vertices, indices, i);
                }
            }
            else
            {
                int consumed = SetQuadPivots(pivots, normals, vertices, indices);

                // Trailing triangle when the triangle count is odd, and every pair that is not card-shaped.
                for (int i = consumed; i + TriangleStride - 1 < indices.Length; i += TriangleStride)
                {
                    SetTrianglePivot(pivots, normals, vertices, indices, i);
                }
            }
        }

        if (!anyTriangle)
        {
            Debug.LogError($"Mesh '{mesh.name}' has no triangles, so there is no billboard pivot to build.", this);
            return null;
        }

        if (sharedCornerDetected)
        {
            Debug.LogWarning(
                $"Mesh '{mesh.name}' shares vertices between different quads, so one pivot per vertex cannot be right for all of them - " +
                "that happens when cards are stitched into a strip, or when two submeshes reference the same vertex. Give every card " +
                "its own four vertices, or switch to the vertex-attribute pivot.", this);
        }

        var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pivots.Length, PivotStride);
        buffer.SetData(pivots);

        var normalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, normals.Length, PivotStride);
        normalBuffer.SetData(normals);

        return new SharedPivot { Buffer = buffer, NormalBuffer = normalBuffer, Users = 1 };
    }

    /// <summary>
    /// Walks the triangles in pairs and gives both halves of a card one shared centre. Returns the index
    /// of the first triangle it did not consume, so the caller can finish a leftover triangle.
    /// </summary>
    private int SetQuadPivots(Vector3[] pivots, Vector3[] normals, Vector3[] vertices, int[] indices)
    {
        int i = 0;
        for (; i + QuadTriangleStride - 1 < indices.Length; i += QuadTriangleStride)
        {
            if (TryGetCardCentre(vertices, indices, i, out Vector3 centre))
            {
                int card = nextCardId++;

                // The shader gets the card's own plane normal, from its winding. A smoothed vertex normal
                // would tilt the billboard basis out of the card's plane, and the projection onto that
                // basis is what makes a card shrink.
                Vector3 normal = TriangleNormal(vertices, indices, i);

                // Both halves of one card carry the same centre and normal, so the card cannot tear along
                // its diagonal and both halves keep their size.
                SetCardPivot(pivots, normals, indices, i, centre, normal, card);
                SetCardPivot(pivots, normals, indices, i + TriangleStride, centre, normal, card);
            }
            else
            {
                // Not a card - two unrelated triangles - so each keeps its own centre.
                SetTrianglePivot(pivots, normals, vertices, indices, i);
                SetTrianglePivot(pivots, normals, vertices, indices, i + TriangleStride);
            }
        }

        return i;
    }

    /// <summary>
    /// True when the two triangles starting at <paramref name="first"/> form a card: once corners sitting at
    /// the same place are merged, exactly four of them remain.
    ///
    /// Merging by position instead of by vertex index is what makes a six-vertex card work. A quad arrives
    /// from a modelling package split per UV/normal, so its four corners are six vertices, and an index-based
    /// test reads that as two unrelated triangles - each half then billboards around its own centre, which
    /// tears the card apart and makes it look smaller than it is.
    /// </summary>
    private bool TryGetCardCentre(Vector3[] vertices, int[] indices, int first, out Vector3 centre)
    {
        // Tolerance relative to this pair's own size. Tied to the mesh instead, the tolerance of a large mesh
        // would swallow the real corners of its small cards - needles on a tree, say - and every card would
        // quietly fall back to being two triangles. Duplicated corners sit at distance ~0.
        float sqrExtent = 0f;
        for (int k = 1; k < QuadTriangleStride; k++)
        {
            sqrExtent = Mathf.Max(sqrExtent, (vertices[indices[first + k]] - vertices[indices[first]]).sqrMagnitude);
        }

        float sqrEpsilon = Mathf.Max(sqrExtent * CornerMergeRatio * CornerMergeRatio, MinCornerSqrEpsilon);

        cardCorners.Clear();
        for (int k = 0; k < QuadTriangleStride; k++)
        {
            Vector3 corner = vertices[indices[first + k]];

            bool alreadyThere = false;
            for (int c = 0; c < cardCorners.Count; c++)
            {
                if ((cardCorners[c] - corner).sqrMagnitude <= sqrEpsilon)
                {
                    alreadyThere = true;
                    break;
                }
            }

            if (!alreadyThere)
            {
                cardCorners.Add(corner);
            }
        }

        centre = Vector3.zero;
        if (cardCorners.Count != QuadCornerCount)
        {
            return false;
        }

        for (int c = 0; c < cardCorners.Count; c++)
        {
            centre += cardCorners[c];
        }

        centre /= QuadCornerCount;
        return true;
    }

    /// <summary>
    /// Plane normal of the triangle starting at <paramref name="first"/> from its winding - the same rule
    /// RecalculateNormals uses, so a card faces the way the rest of the mesh does. Degenerate (zero area)
    /// triangles fall back to up, because a zero normal would poison the billboard basis in the shader.
    /// </summary>
    private static Vector3 TriangleNormal(Vector3[] vertices, int[] indices, int first)
    {
        Vector3 edge1 = vertices[indices[first + 1]] - vertices[indices[first]];
        Vector3 edge2 = vertices[indices[first + 2]] - vertices[indices[first]];
        Vector3 normal = Vector3.Cross(edge1, edge2);

        return normal.sqrMagnitude > 0f ? normal.normalized : Vector3.up;
    }

    /// <summary>Writes the centroid and plane normal of one triangle to its three corners.</summary>
    private void SetTrianglePivot(Vector3[] pivots, Vector3[] normals, Vector3[] vertices, int[] indices, int first)
    {
        Vector3 centre =
            (vertices[indices[first]] + vertices[indices[first + 1]] + vertices[indices[first + 2]]) / 3f;
        Vector3 normal = TriangleNormal(vertices, indices, first);

        for (int k = 0; k < TriangleStride; k++)
        {
            int vertexIndex = indices[first + k];
            pivots[vertexIndex] = centre;
            normals[vertexIndex] = normal;
        }
    }

    /// <summary>Writes a card's centre and normal to the three corners of the triangle starting at <paramref name="first"/>.</summary>
    private void SetCardPivot(Vector3[] pivots, Vector3[] normals, int[] indices, int first, Vector3 centre, Vector3 normal, int card)
    {
        SetCardCorner(pivots, normals, indices[first], centre, normal, card);
        SetCardCorner(pivots, normals, indices[first + 1], centre, normal, card);
        SetCardCorner(pivots, normals, indices[first + 2], centre, normal, card);
    }

    /// <summary>
    /// Records which card claimed a vertex. A vertex index already claimed by a different card means the mesh
    /// shares a vertex between cards - a stitched strip, or two submeshes referencing the same vertex - and one
    /// pivot per vertex cannot serve both, so that is reported once per mesh instead of failing quietly.
    /// </summary>
    private void SetCardCorner(Vector3[] pivots, Vector3[] normals, int vertexIndex, Vector3 centre, Vector3 normal, int card)
    {
        if (claimedByCard[vertexIndex] >= 0 && claimedByCard[vertexIndex] != card)
        {
            sharedCornerDetected = true;
        }

        claimedByCard[vertexIndex] = card;
        pivots[vertexIndex] = centre;
        normals[vertexIndex] = normal;
    }

    private static Mesh GetMesh(Renderer renderer)
    {
        if (renderer is SkinnedMeshRenderer skinned)
        {
            return skinned.sharedMesh;
        }

        var filter = renderer.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }

    private void WarnAboutSetup(Renderer renderer)
    {
        if (renderer.gameObject.isStatic)
        {
            Debug.LogWarning(
                $"'{renderer.name}' is marked Static: static batching rewrites the vertex buffer, so SV_VertexID " +
                "no longer matches the mesh vertex index and the billboard pivot will be wrong. Unmark Static for it.",
                renderer);
        }

        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null || !material.HasProperty(BillboardPropertyName))
            {
                continue;
            }

            if (!material.IsKeywordEnabled(PerVertexPivotKeyword))
            {
                Debug.LogWarning(
                    $"Material '{material.name}' does not have the {PerVertexPivotKeyword} keyword, so it ignores the pivot " +
                    "buffer and keeps billboarding around the mesh origin. Turn on 'Pivot Per Quad/Triangle' in its inspector.",
                    material);
            }

            if (material.enableInstancing)
            {
                Debug.LogWarning(
                    $"Material '{material.name}' has GPU Instancing on, which draws one shared vertex buffer for every " +
                    "instance, so SV_VertexID no longer matches the mesh vertex index. Turn it off for billboard pivots.",
                    material);
            }
        }
    }
}
