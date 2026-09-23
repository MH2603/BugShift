using UnityEngine;
using System;

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;

/// <summary>
/// Bakes the per-quad billboard data of <c>BrewMonster/UnlitWithShadows</c> into two textures per leaves
/// mesh - one texel per vertex: its quad centre and the plane normal of that quad - and gives every LOD
/// level its own material copy carrying those textures, because the levels share one material asset.
///
/// Run it from Tools &gt; BrewMonster &gt; Bake Billboard Quad Data, or call <see cref="BakeFolder"/>.
/// For every prefab under the chosen folder it walks the MeshRenderers (one per LOD level), reads the
/// leaves sub mesh, and writes:
///   &lt;meshName&gt;_BillboardPivot.asset   RGBAFloat, point sampled, no mipmaps, no compression
///   &lt;meshName&gt;_BillboardNormal.asset  the same settings
///   &lt;meshName&gt;_Billboard.mat          a copy of the leaves material with both textures assigned
/// all next to the prefab. Re-running updates those assets in place so the textures and materials keep
/// their GUIDs and the prefabs do not lose their references.
///
/// The shader addresses the textures by SV_VertexID, which the baker records in the material as
/// _BillboardDataSize (width, height, 1/width, 1/height). That keeps the runtime free of any per-renderer
/// binding, at the price of the usual SV_VertexID rules: no static or dynamic batching, no GPU instancing
/// and no CPU re-baked (BakeMesh) skinned meshes - they all renumber or merge the vertex buffer.
/// </summary>
public class BillboardDataBaker : EditorWindow
{
    private const string PivotTextureSuffix = "_BillboardPivot";
    private const string NormalTextureSuffix = "_BillboardNormal";
    private const string MaterialSuffix = "_Billboard";
    private const string FaceCameraKeyword = "_BILLBOARD_ON";
    private const string PerQuadPivotKeyword = "_BILLBOARD_PER_VERTEX_PIVOT";

    /// <summary>Name of the now-deleted runtime component, still matched to clean up old prefabs.</summary>
    private const string RuntimeBinderTypeName = "BillboardPivotBinder";

    private static readonly int PivotTextureId = Shader.PropertyToID("_BillboardPivotTex");
    private static readonly int NormalTextureId = Shader.PropertyToID("_BillboardNormalTex");
    private static readonly int DataSizeId = Shader.PropertyToID("_BillboardDataSize");
    private static readonly int FaceCameraToggleId = Shader.PropertyToID("_Billboard");
    private static readonly int PerQuadPivotToggleId = Shader.PropertyToID("_BillboardPivot");

    [SerializeField] private DefaultAsset rootFolder;
    [Tooltip("Sub mesh index drawn with the leaves material, 0 = bark, 1 = leaves in these prefabs.")]
    [SerializeField] private int leavesSubMesh = 1;
    [Tooltip("Ignore the index above for meshes whose sub mesh material is named something with 'leaf' in it.")]
    [SerializeField] private bool detectLeavesByName = true;
    [Tooltip("Corner merge tolerance, as a fraction of a card's own size. Duplicated corners sit at distance ~0.")]
    [SerializeField] private float cornerMergeRatio = 1e-3f;
    [Tooltip("Remove BillboardPivotBinder components and any missing script left behind by the deleted binder file.")]
    [SerializeField] private bool removeRuntimeBinder = true;
    [Tooltip("Switch 'Face Camera' on in the baked materials - the leaves are supposed to billboard.")]
    [SerializeField] private bool enableFaceCamera = true;
    [Tooltip("Switch 'Pivot Per Quad/Triangle' on in the baked materials, so they use the baked textures.")]
    [SerializeField] private bool enablePerQuadPivot = true;
    [SerializeField] private bool verboseLogging = true;

    private readonly List<string> report = new List<string>();
    private Vector2 scroll;

    [MenuItem("Tools/BrewMonster/Bake Billboard Quad Data")]
    private static void OpenWindow()
    {
        var window = GetWindow<BillboardDataBaker>(true, "Billboard Data Baker");
        window.minSize = new Vector2(460f, 320f);
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
        rootFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Folder", "Every prefab under this folder is processed, recursively."),
            rootFolder,
            typeof(DefaultAsset),
            false);
        leavesSubMesh = EditorGUILayout.IntField(new GUIContent("Leaves Sub Mesh", "0 = bark, 1 = leaves in these prefabs."), leavesSubMesh);
        detectLeavesByName = EditorGUILayout.Toggle(new GUIContent("Detect Leaves By Name", "Prefer the sub mesh whose material name contains 'leaf'."), detectLeavesByName);
        cornerMergeRatio = EditorGUILayout.FloatField(new GUIContent("Corner Merge Ratio", "Fraction of a card's size below which two corners count as the same corner."), cornerMergeRatio);
        enableFaceCamera = EditorGUILayout.Toggle(new GUIContent("Enable Face Camera", "Switch 'Face Camera' on in the baked materials."), enableFaceCamera);
        enablePerQuadPivot = EditorGUILayout.Toggle(new GUIContent("Enable Per Quad Toggle", "Switch 'Pivot Per Quad/Triangle' on in the baked materials."), enablePerQuadPivot);
        removeRuntimeBinder = EditorGUILayout.Toggle(new GUIContent("Clean Up Old Binder", "Remove BillboardPivotBinder components, and missing scripts, from the processed prefabs."), removeRuntimeBinder);
        verboseLogging = EditorGUILayout.Toggle("Log Every Prefab", verboseLogging);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(rootFolder == null))
        {
            if (GUILayout.Button("Bake", GUILayout.Height(28f)))
            {
                BakeFolder(AssetDatabase.GetAssetPath(rootFolder), leavesSubMesh, detectLeavesByName, cornerMergeRatio, enableFaceCamera, enablePerQuadPivot, removeRuntimeBinder, verboseLogging, report);
                scroll = Vector2.zero;
            }
        }

        if (rootFolder == null)
        {
            EditorGUILayout.HelpBox("Pick the folder that holds the prefabs (their meshes and materials).", MessageType.Info);
        }

        report.RemoveAll(string.IsNullOrEmpty);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (string line in report)
        {
            EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    /// <summary>Bakes every prefab under <paramref name="folderPath"/>; the window calls this, pipelines can too.</summary>
    public static void BakeFolder(
        string folderPath,
        int leavesSubMesh,
        bool detectLeavesByName,
        float cornerMergeRatio,
        bool enableFaceCamera,
        bool enablePerQuadPivot,
        bool removeRuntimeBinder,
        bool verboseLogging,
        List<string> report = null)
    {
        if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
        {
            Debug.LogError($"Billboard data baker: '{folderPath}' is not a project folder.");
            return;
        }

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });
        int bakedPrefabs = 0;
        int skipped = 0;

        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                EditorUtility.DisplayProgressBar("Baking billboard data", prefabPath, (float)i / Mathf.Max(1, guids.Length));

                switch (BakePrefab(prefabPath, leavesSubMesh, detectLeavesByName, cornerMergeRatio, enableFaceCamera, enablePerQuadPivot, removeRuntimeBinder, verboseLogging, report))
                {
                    case BakeResult.Baked:
                        bakedPrefabs++;
                        break;
                    case BakeResult.Skipped:
                        skipped++;
                        break;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        string summary = $"Billboard data baker: {bakedPrefabs} prefab(s) baked, {skipped} skipped, out of {guids.Length} under '{folderPath}'.";
        report?.Add(summary);
        Debug.Log(summary, AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath));
    }

    private enum BakeResult
    {
        Baked,
        Skipped
    }

    private static BakeResult BakePrefab(
        string prefabPath,
        int leavesSubMesh,
        bool detectLeavesByName,
        float cornerMergeRatio,
        bool enableFaceCamera,
        bool enablePerQuadPivot,
        bool removeRuntimeBinder,
        bool verboseLogging,
        List<string> report)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
        if (contents == null)
        {
            report?.Add($"[skip] {prefabPath}: could not load the prefab");
            return BakeResult.Skipped;
        }

        try
        {
            string folder = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                report?.Add($"[skip] {prefabPath}: its folder is not inside the project");
                return BakeResult.Skipped;
            }

            bool changed = false;
            bool anyMesh = false;

            if (removeRuntimeBinder)
            {
                changed |= CleanUpRuntimeBinder(contents, prefabPath, report, verboseLogging);
            }

            // One MeshRenderer per LOD level in these prefabs; the skinned case is handled the same way.
            foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = GetMesh(renderer);
                if (mesh == null || mesh.vertexCount == 0)
                {
                    continue;
                }

                anyMesh = true;
                changed |= BakeRenderer(renderer, mesh, folder, leavesSubMesh, detectLeavesByName, cornerMergeRatio, enableFaceCamera, enablePerQuadPivot, prefabPath, report, verboseLogging);
            }

            if (!anyMesh)
            {
                report?.Add($"[skip] {prefabPath}: no renderer with a mesh");
                return BakeResult.Skipped;
            }

            if (changed)
            {
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                report?.Add($"[ok]   {prefabPath}");
                return BakeResult.Baked;
            }

            report?.Add($"[skip] {prefabPath}: nothing to bake (no leaves material using the billboard shader?)");
            return BakeResult.Skipped;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    private static bool BakeRenderer(
        Renderer renderer,
        Mesh mesh,
        string outputFolder,
        int leavesSubMesh,
        bool detectLeavesByName,
        float cornerMergeRatio,
        bool enableFaceCamera,
        bool enablePerQuadPivot,
        string prefabPath,
        List<string> report,
        bool verboseLogging)
    {
        Material[] materials = renderer.sharedMaterials;
        int leaves = ResolveLeavesSubMesh(mesh, materials, leavesSubMesh, detectLeavesByName);
        if (leaves < 0 || leaves >= materials.Length)
        {
            report?.Add($"[skip] {prefabPath}: '{renderer.name}' has no sub mesh that looks like leaves");
            return false;
        }

        Material source = materials[leaves];
        if (source == null || !source.HasProperty(PivotTextureId))
        {
            report?.Add($"[skip] {prefabPath}: '{renderer.name}' sub mesh {leaves} does not use BrewMonster/UnlitWithShadows");
            return false;
        }

        if (!TryReadMesh(mesh, out Vector3[] vertices, out List<int[]> subMeshTriangles, out string readError))
        {
            report?.Add($"[skip] {prefabPath}: '{mesh.name}': {readError}");
            return false;
        }

        if (leaves >= subMeshTriangles.Count)
        {
            report?.Add($"[skip] {prefabPath}: '{mesh.name}' has {subMeshTriangles.Count} sub mesh(es), {leaves} requested");
            return false;
        }

        if (!BillboardQuadData.TryBuild(vertices, subMeshTriangles[leaves], cornerMergeRatio, out int width, out int height, out Color[] pivots, out Color[] normals, out string warning))
        {
            report?.Add($"[skip] {prefabPath}: '{mesh.name}' has no triangles in sub mesh {leaves}");
            return false;
        }

        if (!string.IsNullOrEmpty(warning))
        {
            Debug.LogWarning($"Billboard data baker: {warning} ({mesh.name}, {prefabPath})");
            report?.Add($"      ! {warning}");
        }

        Texture2D pivotTexture = CreateOrUpdateTexture($"{outputFolder}/{mesh.name}{PivotTextureSuffix}.asset", width, height, pivots);
        Texture2D normalTexture = CreateOrUpdateTexture($"{outputFolder}/{mesh.name}{NormalTextureSuffix}.asset", width, height, normals);

        var dataSize = new Vector4(width, height, 1f / width, 1f / height);
        Material baked = CreateOrUpdateMaterial($"{outputFolder}/{mesh.name}{MaterialSuffix}.mat", source, pivotTexture, normalTexture, dataSize, enableFaceCamera, enablePerQuadPivot);

        materials[leaves] = baked;
        renderer.sharedMaterials = materials;

        if (verboseLogging)
        {
            report?.Add($"       {renderer.name}: {mesh.name} sub mesh {leaves} -> {width}x{height} texels, material {baked.name}{(enableFaceCamera ? " (Face Camera on)" : string.Empty)}");
        }

        return true;
    }

    /// <summary>Finds the sub mesh drawn with the leaves material: by name first, then by the configured index.</summary>
    private static int ResolveLeavesSubMesh(Mesh mesh, Material[] materials, int leavesSubMesh, bool detectLeavesByName)
    {
        if (detectLeavesByName)
        {
            for (int i = 0; i < materials.Length && i < mesh.subMeshCount; i++)
            {
                string name = materials[i] != null ? materials[i].name : string.Empty;
                if (name.IndexOf("leaf", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("leaves", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }
        }

        return leavesSubMesh;
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

    /// <summary>
    /// Reads vertices and per-sub-mesh indices. Read/Write does not have to be enabled on the model: the
    /// editor can take a read-only snapshot of the mesh data instead.
    /// </summary>
    private static bool TryReadMesh(Mesh mesh, out Vector3[] vertices, out List<int[]> subMeshTriangles, out string error)
    {
        vertices = Array.Empty<Vector3>();
        subMeshTriangles = new List<int[]>();
        error = null;

        if (mesh.vertexCount == 0 || mesh.subMeshCount == 0)
        {
            error = "it has no triangles";
            return false;
        }

        if (mesh.isReadable)
        {
            vertices = mesh.vertices;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                subMeshTriangles.Add(mesh.GetIndices(subMesh));
            }

            return true;
        }

        Mesh.MeshDataArray meshDataArray = MeshUtility.AcquireReadOnlyMeshData(mesh);
        try
        {
            Mesh.MeshData meshData = meshDataArray[0];

            var nativeVertices = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp);
            try
            {
                meshData.GetVertices(nativeVertices);
                vertices = new Vector3[meshData.vertexCount];
                nativeVertices.CopyTo(vertices);

                for (int subMesh = 0; subMesh < meshData.subMeshCount; subMesh++)
                {
                    var nativeIndices = new NativeArray<int>(meshData.GetSubMesh(subMesh).indexCount, Allocator.Temp);
                    try
                    {
                        meshData.GetIndices(nativeIndices, subMesh, true);
                        var indices = new int[nativeIndices.Length];
                        nativeIndices.CopyTo(indices);
                        subMeshTriangles.Add(indices);
                    }
                    finally
                    {
                        nativeIndices.Dispose();
                    }
                }
            }
            finally
            {
                nativeVertices.Dispose();
            }

            return true;
        }
        finally
        {
            meshDataArray.Dispose();
        }
    }

    private static Texture2D CreateOrUpdateTexture(string path, int width, int height, Color[] pixels)
    {
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (texture != null && (texture.width != width || texture.height != height || texture.format != TextureFormat.RGBAFloat))
        {
            // The mesh changed shape since the last bake, so the old layout is unusable.
            AssetDatabase.DeleteAsset(path);
            texture = null;
        }

        if (texture == null)
        {
            texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                name = Path.GetFileNameWithoutExtension(path),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            AssetDatabase.CreateAsset(texture, path);
        }
        else
        {
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            EditorUtility.SetDirty(texture);
        }

        return texture;
    }

    private static Material CreateOrUpdateMaterial(string path, Material source, Texture2D pivotTexture, Texture2D normalTexture, Vector4 dataSize, bool enableFaceCamera, bool enablePerQuadPivot)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(source) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            // Refresh from the shared material this copy came from, keeping the asset's own GUID so the
            // prefab that points at it - and the textures below - stay connected.
            material.shader = source.shader;
            material.CopyPropertiesFromMaterial(source);
        }

        material.SetTexture(PivotTextureId, pivotTexture);
        material.SetTexture(NormalTextureId, normalTexture);
        material.SetVector(DataSizeId, dataSize);

        // The leaves are meant to billboard, so the baked material turns Face Camera on for itself: baked quad
        // data on a material that does not billboard would do nothing at all.
        if (enableFaceCamera)
        {
            material.SetFloat(FaceCameraToggleId, 1f);
            material.EnableKeyword(FaceCameraKeyword);
        }

        if (enablePerQuadPivot)
        {
            material.SetFloat(PerQuadPivotToggleId, 1f);
            material.EnableKeyword(PerQuadPivotKeyword);
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>Removes the old runtime binder by name and sweeps missing scripts left behind by its deleted file.</summary>
    private static bool CleanUpRuntimeBinder(GameObject root, string prefabPath, List<string> report, bool verboseLogging)
    {
        bool changed = false;

        foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour != null && behaviour.GetType().Name == RuntimeBinderTypeName)
            {
                DestroyImmediate(behaviour, true);
                changed = true;
            }
        }

        int missing = 0;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            missing += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
        }

        if (missing > 0)
        {
            changed = true;
            report?.Add($"       {prefabPath}: removed {missing} missing script component(s)");
        }

        if (changed && verboseLogging)
        {
            report?.Add($"       {prefabPath}: removed the old billboard pivot binder");
        }

        return changed;
    }
}

/// <summary>
/// Turns a sub mesh's triangles into one object-space pivot and plane normal per vertex - the centre and
/// plane of the card that vertex belongs to, or of its triangle when the pair of triangles is not a card.
/// </summary>
internal static class BillboardQuadData
{
    private const int TriangleStride = 3;
    private const int QuadTriangleStride = 2 * TriangleStride;
    private const int QuadCornerCount = 4;
    private const int MaxTextureWidth = 2048;
    private const float MinCornerSqrEpsilon = 1e-12f;

    public static bool TryBuild(
        Vector3[] vertices,
        int[] indices,
        float cornerMergeRatio,
        out int width,
        out int height,
        out Color[] pivots,
        out Color[] normals,
        out string warning)
    {
        warning = null;
        width = 1;
        height = 1;
        pivots = Array.Empty<Color>();
        normals = Array.Empty<Color>();

        if (vertices.Length == 0 || indices == null || indices.Length < TriangleStride)
        {
            return false;
        }

        var centres = new Vector3[vertices.Length];
        var planeNormals = new Vector3[vertices.Length];
        var claimedByCard = new int[vertices.Length];
        for (int i = 0; i < claimedByCard.Length; i++)
        {
            claimedByCard[i] = -1;
        }

        var cardCorners = new List<Vector3>(QuadTriangleStride);
        bool sharedCorner = false;
        int nextCardId = 0;

        int triangle = 0;
        for (; triangle + QuadTriangleStride - 1 < indices.Length; triangle += QuadTriangleStride)
        {
            if (TryGetCardCentre(vertices, indices, triangle, cornerMergeRatio, cardCorners, out Vector3 centre))
            {
                Vector3 normal = TriangleNormal(vertices, indices, triangle);
                int card = nextCardId++;

                // Both halves of one card carry the same centre and normal, so the card cannot tear along its
                // diagonal and both halves keep their size.
                SetCardCorners(centres, planeNormals, claimedByCard, indices, triangle, centre, normal, card, ref sharedCorner);
                SetCardCorners(centres, planeNormals, claimedByCard, indices, triangle + TriangleStride, centre, normal, card, ref sharedCorner);
            }
            else
            {
                // Not a card - two unrelated triangles - so each keeps its own centre.
                SetTriangleCorners(centres, planeNormals, vertices, indices, triangle);
                SetTriangleCorners(centres, planeNormals, vertices, indices, triangle + TriangleStride);
            }
        }

        for (; triangle + TriangleStride - 1 < indices.Length; triangle += TriangleStride)
        {
            SetTriangleCorners(centres, planeNormals, vertices, indices, triangle);
        }

        if (sharedCorner)
        {
            warning = "some vertices are shared between different quads, so one pivot per vertex cannot be right for all of them; " +
                      "give every card its own four corners (split the vertices) before baking";
        }

        width = Mathf.Clamp(vertices.Length, 1, MaxTextureWidth);
        height = Mathf.Max(1, Mathf.CeilToInt(vertices.Length / (float)width));

        // One texel per vertex, row-major, so the shader can find vertex N at (N % width, N / width). Padding
        // texels stay zero: the shader reads those pivots as the mesh origin and skips their normals.
        pivots = new Color[width * height];
        normals = new Color[width * height];
        for (int v = 0; v < vertices.Length; v++)
        {
            Vector3 centre = centres[v];
            Vector3 normal = planeNormals[v];
            pivots[v] = new Color(centre.x, centre.y, centre.z, 1f);
            normals[v] = new Color(normal.x, normal.y, normal.z, 0f);
        }

        return true;
    }

    private static void SetCardCorners(
        Vector3[] centres,
        Vector3[] planeNormals,
        int[] claimedByCard,
        int[] indices,
        int triangle,
        Vector3 centre,
        Vector3 normal,
        int card,
        ref bool sharedCorner)
    {
        for (int k = 0; k < TriangleStride; k++)
        {
            int vertexIndex = indices[triangle + k];

            if (claimedByCard[vertexIndex] >= 0 && claimedByCard[vertexIndex] != card)
            {
                sharedCorner = true;
            }

            claimedByCard[vertexIndex] = card;
            centres[vertexIndex] = centre;
            planeNormals[vertexIndex] = normal;
        }
    }

    private static void SetTriangleCorners(Vector3[] centres, Vector3[] planeNormals, Vector3[] vertices, int[] indices, int triangle)
    {
        Vector3 centre =
            (vertices[indices[triangle]] + vertices[indices[triangle + 1]] + vertices[indices[triangle + 2]]) / 3f;
        Vector3 normal = TriangleNormal(vertices, indices, triangle);

        for (int k = 0; k < TriangleStride; k++)
        {
            int vertexIndex = indices[triangle + k];
            centres[vertexIndex] = centre;
            planeNormals[vertexIndex] = normal;
        }
    }

    /// <summary>
    /// True when the two triangles starting at <paramref name="triangle"/> form a card: once corners sitting at
    /// the same place are merged, exactly four of them remain. Merging by position instead of by vertex index
    /// is what makes a six-vertex card work - a quad arrives split per UV/normal, so its four corners are six
    /// vertices, and an index-based test would read that as two unrelated triangles, each half then turning
    /// around its own centre.
    /// </summary>
    private static bool TryGetCardCentre(Vector3[] vertices, int[] indices, int triangle, float cornerMergeRatio, List<Vector3> cardCorners, out Vector3 centre)
    {
        // Tolerance relative to this pair's own size: tied to the mesh instead, a large mesh would swallow
        // the real corners of its small cards. Duplicated corners sit at distance ~0.
        float sqrExtent = 0f;
        for (int k = 1; k < QuadTriangleStride; k++)
        {
            sqrExtent = Mathf.Max(sqrExtent, (vertices[indices[triangle + k]] - vertices[indices[triangle]]).sqrMagnitude);
        }

        float sqrEpsilon = Mathf.Max(sqrExtent * cornerMergeRatio * cornerMergeRatio, MinCornerSqrEpsilon);

        cardCorners.Clear();
        for (int k = 0; k < QuadTriangleStride; k++)
        {
            Vector3 corner = vertices[indices[triangle + k]];

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
    /// Plane normal of a triangle from its winding - the rule RecalculateNormals uses, so a card faces the way
    /// the rest of the mesh does. Degenerate triangles fall back to up, because a zero normal would poison the
    /// billboard basis in the shader.
    /// </summary>
    private static Vector3 TriangleNormal(Vector3[] vertices, int[] indices, int triangle)
    {
        Vector3 edge1 = vertices[indices[triangle + 1]] - vertices[indices[triangle]];
        Vector3 edge2 = vertices[indices[triangle + 2]] - vertices[indices[triangle]];
        Vector3 normal = Vector3.Cross(edge1, edge2);

        return normal.sqrMagnitude > 0f ? normal.normalized : Vector3.up;
    }
}
#endif
