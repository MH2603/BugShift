using System.Collections;
using UnityEngine;

namespace BrewMonster.Scripts
{
    public class SkinnedMeshRenderFromData : MonoBehaviour
    {
        public static string baseTexturePath = "ModelRenderer/Art/Textures/";
        public static string baseMeshPath = "ModelRenderer/Art/Models";
        
        private int BaseMapHash = Shader.PropertyToID("_BaseMap");
        
        public SkeletonBuilder _skeletonBuilder;
        public MeshFilter _meshFilter;
        public SkinnedMeshRenderer _skinnedMeshRenderer;
        
        private Vector3[] vertices = new Vector3[]
        {
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(0, 0, 1)
        };

        private int[] indices = new int[]
        {
            0, 1, 2,
            0, 1, 3,
            1, 2, 3,
            2, 0, 3
        };
    
        private Vector2[] uv1 = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(0, 1),
            new Vector2(1, 1)
        };
    
        // normals
        private Vector3[] normals = new Vector3[]
        {
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, -1)
        };

        public string[] BoneNames;

        // Bound center exactly as baked in the prefab (SkinnedMeshRenderer "m_AABB"): expressed in the
        // root bone bind space.
        private Vector3 _bakedBoundCenter;
        private Vector3 _bakedBoundExtents;
        private bool _bakedBoundCenterCaptured;

       
        void Start()
        {
            DelayBuildSkinnedBound();
        }

        [ContextMenu("Set Bound Center")]
        public void DelayBuildSkinnedBound()
        {

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
                _bakedBoundCenter = skinnedMeshRenderer.sharedMesh.bounds.center;
                _bakedBoundExtents = skinnedMeshRenderer.sharedMesh.bounds.extents;
                _bakedBoundCenterCaptured = true;
            }

            var rootBone = skinnedMeshRenderer.rootBone;
            if (rootBone == null)
            {
                return; // No bone assigned yet - keep the baked bound until one is.
            }

            // BMLogger.Log( $"[MH] updated skinned mesh bound center of {gameObject.name}");

           
            StartCoroutine( BuildSkinnedBoundCorountine(skinnedMeshRenderer));
        }

        IEnumerator BuildSkinnedBoundCorountine(SkinnedMeshRenderer skinnedMeshRenderer)
        {
            yield return new WaitForSeconds(5f); // wait to anim run and Root Bone correct pos
            BuildSkinnedBound(skinnedMeshRenderer);
        }

        void BuildSkinnedBound(SkinnedMeshRenderer skinnedMeshRenderer)
        {
            
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
                return;
            }

            Debug.Log($" {gameObject.name} bone root local = {rootBone.localPosition} bond center = {bounds.center} extens = {bounds.extents}");

            // this transform local space -> world -> root bone space.
            // Unity interprets localBounds in the *root bone* space, so what we assign must be the
            // box expressed in that space.
            var toBoneSpace = rootBone.worldToLocalMatrix * transform.localToWorldMatrix;
            // Debug.Log($"{toBoneSpace}");

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
        

    #if MODEL_RENDERER_PROJECT
        public async Task Render(A3DSkin skin, int index, Material material, SkeletonBuilder skeletonBuilder = null)
        {
            A3DSkinMesh skinMesh = skin._skinMeshes[index];
            BoneNames = skin._boneNames;

            // Set up all paths
            var relativeFilePath = skin.m_strAbsoluteFilePath.Substring(Application.dataPath.Length + 1);
            relativeFilePath = relativeFilePath.Substring(0, relativeFilePath.LastIndexOf('.'));
            var relativeMeshPath = relativeFilePath;
            var absoluteMeshFolderPath = Path.Combine(Application.dataPath, baseMeshPath, relativeMeshPath);
            var skinName = skin.m_strSkinName;
            relativeMeshPath = Path.Combine(relativeMeshPath, $"{skinMesh.m_strMeshName}.mesh");
            var absoluteMeshPath = Path.Combine(Application.dataPath, baseMeshPath, relativeMeshPath);
            var savedMeshPath = Path.Combine("Assets", baseMeshPath, relativeMeshPath);

            var relativeMaterialPath = Path.Combine(relativeFilePath, $"{skinMesh.m_strMeshName}.mat");
            var absoluteMaterialPath = Path.Combine(Application.dataPath, baseMeshPath, relativeMaterialPath);
            var savedMaterialPath = Path.Combine("Assets", baseMeshPath, relativeMaterialPath);

            // Prefab paths
            string prefabRelativePath = Path.Combine(relativeFilePath, $"{skinMesh.m_strMeshName}.prefab");
            string prefabAssetPath = Path.Combine("Assets", baseMeshPath, prefabRelativePath);
            string prefabAbsolutePath = Path.Combine(Application.dataPath, baseMeshPath, prefabRelativePath);

            gameObject.name = skinMesh.m_strMeshName;

#if UNITY_EDITOR
            // Check if prefab already exists at the beginning
            GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            
            if (existingPrefab != null)
            {
                // Prefab exists - load it and assign bones for animation
                PrefabUtility.ConvertToPrefabInstance(gameObject, existingPrefab, new ConvertToPrefabInstanceSettings(), InteractionMode.AutomatedAction);
                
                // Get the SkinnedMeshRenderer from the prefab instance
                _skinnedMeshRenderer = gameObject.GetComponent<SkinnedMeshRenderer>();
                
                // Assign bones and root bone so it works with animation
                if (_skinnedMeshRenderer != null)
                {
                    
                    _skeletonBuilder = skeletonBuilder ?? _skeletonBuilder;
                    _skinnedMeshRenderer.bones = _skeletonBuilder.GetBones(skin._boneNames);
                    _skinnedMeshRenderer.rootBone = _skinnedMeshRenderer.bones[^1];
                }
                
                return; // Early exit - no need to create mesh/material
            }
#endif

            // Prefab doesn't exist - proceed with normal rendering
            if (!Directory.Exists(absoluteMeshFolderPath)) Directory.CreateDirectory(absoluteMeshFolderPath);
            
            if (_meshFilter == null)
            {
                _meshFilter = gameObject.GetComponent<MeshFilter>();
                if (_meshFilter == null)
                    _meshFilter = gameObject.AddComponent<MeshFilter>();
            }
            Mesh mesh;
            if (File.Exists(absoluteMeshPath))
            {
                // If mesh already exists, we just load it from the asset database
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(savedMeshPath);
                _meshFilter.mesh                = mesh;
                _skeletonBuilder = skeletonBuilder ?? _skeletonBuilder;
                _skinnedMeshRenderer.bones      = _skeletonBuilder.GetBones(skin._boneNames);
                _skinnedMeshRenderer.rootBone   = _skinnedMeshRenderer.bones[^1];
                _skinnedMeshRenderer.sharedMesh = mesh;
            }
            else
            {
                // If mesh does not exist, we create a new one. Then save it to the asset database.
                vertices = new Vector3[skinMesh.iNumVert];
                for (int i = 0; i < skinMesh.iNumVert; i++)
                {
                    vertices[i] = new Vector3(skinMesh._vertices[i].vPos[0], skinMesh._vertices[i].vPos[1], skinMesh._vertices[i].vPos[2]);
                }
            
                indices = new int[skinMesh.iNumIdx];
                for (int i = 0; i < skinMesh.iNumIdx; i++)
                {
                    indices[i] = skinMesh._indices[i];
                }
            
                uv1 = new Vector2[skinMesh.iNumVert];
                for (int i = 0; i < skinMesh.iNumVert; i++)
                {
                    uv1[i] = new Vector2(skinMesh._vertices[i].tu, skinMesh._vertices[i].tv);
                }
            
                normals = new Vector3[skinMesh.iNumVert];
                for (int i = 0; i < skinMesh.iNumVert; i++)
                {
                    normals[i] = new Vector3(skinMesh._vertices[i].vNormal[0], skinMesh._vertices[i].vNormal[1], skinMesh._vertices[i].vNormal[2]);
                }
                
                // Create a new Mesh
                mesh = new Mesh();

                // Set the vertices and triangles (indices)
                mesh.vertices  = vertices;
                mesh.uv        = uv1;
                mesh.triangles = indices;
                
                
                var boneWeights = new BoneWeight[skinMesh.iNumVert];
                for (int i = 0; i < skinMesh.iNumVert; i++)
                {
                    var src = skinMesh._vertices[i];
                    BoneWeight boneWeight = new BoneWeight
                    {
                        boneIndex0 = (int)(src.dwMatIndices & 0x000000ff),
                        boneIndex1 = (int)((src.dwMatIndices >> 8) & 0x000000ff),
                        boneIndex2 = (int)((src.dwMatIndices >> 16) & 0x000000ff),
                        boneIndex3 = (int)((src.dwMatIndices >> 24) & 0x000000ff),
                        weight0    = src.aWeights.Length > 0 ? src.aWeights[0] : 0.0f,
                        weight1    = src.aWeights.Length > 1 ? src.aWeights[1] : 0.0f,
                        weight2    = src.aWeights.Length > 2 ? src.aWeights[2] : 0.0f,
                        weight3    = src.aWeights.Length > 3 ? src.aWeights[3] : 0.0f
                    };
                    boneWeights[i] = boneWeight;
                }
                mesh.boneWeights = boneWeights;
                mesh.bindposes   = skeletonBuilder != null ? skeletonBuilder.GetBindPoses(skin._boneNames) : _skeletonBuilder.GetBindPoses(skin._boneNames);
                
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                if (_skinnedMeshRenderer == null)
                {
                    _skinnedMeshRenderer = gameObject.AddComponent<SkinnedMeshRenderer>();
                }
                _skinnedMeshRenderer.bones      = skeletonBuilder != null ? skeletonBuilder.GetBones(skin._boneNames) : _skeletonBuilder.GetBones(skin._boneNames);
                _skinnedMeshRenderer.rootBone   = _skinnedMeshRenderer.bones[^1];

                // now we save the mesh to asset
                AssetDatabase.CreateAsset(mesh, savedMeshPath);
                AssetDatabase.Refresh();

                // await Task.Delay(100);
                
                // then set the mesh to the skinned mesh renderer
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(savedMeshPath);
                _skinnedMeshRenderer.sharedMesh = mesh;
                _meshFilter.mesh = mesh;
            }
            
            Material newMaterial = null;
            if (File.Exists(absoluteMaterialPath))
            {
                newMaterial = AssetDatabase.LoadAssetAtPath<Material>(savedMaterialPath);
            }
            else
            {
                newMaterial = new Material(material);
            }
            var absoluteTexturePath = Path.Combine(skin.m_strTextureDirPath, skin.textureNames[skinMesh.iTexture]);
            var relativeTexturePath = absoluteTexturePath.Substring(Application.dataPath.Length + 1);
            string texturePathWithoutExtension = relativeTexturePath.Substring(0, relativeTexturePath.Length - 4);

            // this is absolute path of the png file
            string pngTexturePath = Path.Combine(Application.dataPath, baseTexturePath, texturePathWithoutExtension + ".png");

            // check if png file exists
            if (File.Exists(pngTexturePath))
            {
                if (pngTexturePath.Contains(Application.dataPath))
                {
                    pngTexturePath = pngTexturePath.Substring(Application.dataPath.Length);
                    pngTexturePath = "Assets" + pngTexturePath;
                }

                newMaterial.SetTexture(BaseMapHash, AssetDatabase.LoadAssetAtPath<Texture2D>(pngTexturePath));
            }
            else if (File.Exists(absoluteTexturePath))
            {
                bool isTGATexture = absoluteTexturePath.Contains(".tga");
                
                Texture2D texture;
                if (isTGATexture)
                {
                    texture = AAssit.LoadTextureTGA(absoluteTexturePath);
                }
                else
                {
                    texture = AAssit.LoadTextureDXT(File.ReadAllBytes(absoluteTexturePath));
                }

                // save the texture to png
                FolderAndFileUltilities.CreateFolderStructure(pngTexturePath.Substring(0, pngTexturePath.LastIndexOf('/')));
                AAssit.SaveTexture2DToPNG(texture, pngTexturePath, true);
                
                AssetDatabase.Refresh();
                if (pngTexturePath.Contains(Application.dataPath))
                {
                    pngTexturePath = pngTexturePath.Substring(Application.dataPath.Length);
                    pngTexturePath = "Assets" + pngTexturePath;
                }
                newMaterial.SetTexture(BaseMapHash, AssetDatabase.LoadAssetAtPath<Texture2D>(pngTexturePath));
            }
            else
            {
                newMaterial.SetTexture(BaseMapHash, Texture2D.whiteTexture);
            }

            if (!File.Exists(absoluteMaterialPath))
            {
                AssetDatabase.CreateAsset(newMaterial, savedMaterialPath);
                AssetDatabase.Refresh();
                newMaterial = AssetDatabase.LoadAssetAtPath<Material>(savedMaterialPath);
            }
                
            _skinnedMeshRenderer.material = newMaterial;

#if UNITY_EDITOR
            // Save the GameObject as a new prefab (we already checked it doesn't exist at the beginning)
            // Ensure target folder exists
            string prefabFolderAbsolute = Path.GetDirectoryName(prefabAbsolutePath);
            if (!Directory.Exists(prefabFolderAbsolute))
            {
                Directory.CreateDirectory(prefabFolderAbsolute);
            }

            // Create prefab and connect the current object to it
            PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, prefabAssetPath, InteractionMode.AutomatedAction);
#endif
        }

        // each skinned mesh should have a skeleton builder
        // no skeleton => no animation
        public void BuildSkeleton(A3DSkeleton skeleton)
        {
            _skeletonBuilder.BuildSkeleton(skeleton);
        }
#endif
    }
}