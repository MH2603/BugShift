using System.Collections.Generic;
using UnityEngine;

namespace BrewMonster.Scripts
{
    public class SkeletonBuilder : MonoBehaviour
    {
        public List<Transform> bones = new List<Transform>();
        public List<Matrix4x4> bindPoses = new List<Matrix4x4>();
        public Transform rootBone;
        public Dictionary<string, Transform> hooks = new Dictionary<string, Transform>();
        private readonly Dictionary<string, Matrix4x4> _angelicaRelativeByName =
            new Dictionary<string, Matrix4x4>();

        /// <summary>
        /// Returns C++ <c>A3DBone::GetOriginalMatrix()</c> (<c>BONEDATA::matRelative</c>)
        /// converted to Unity matrix layout.
        /// </summary>
        public bool TryGetAngelicaRelative(string boneName, out Matrix4x4 matrix)
        {
            if (!string.IsNullOrEmpty(boneName) &&
                _angelicaRelativeByName.TryGetValue(boneName, out matrix))
            {
                return true;
            }

            matrix = Matrix4x4.identity;
            return false;
        }

        /// <summary>
        /// Reconstructs a bone-to-parent rest matrix from serialized bind poses.
        /// This remains available on runtime prefabs even when source matRelative
        /// metadata was not baked and the animated Transform is no longer at rest.
        /// </summary>
        public bool TryGetBindRelative(Transform bone, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (bone == null || bones == null || bindPoses == null)
                return false;

            int boneIndex = bones.IndexOf(bone);
            if (boneIndex < 0 || boneIndex >= bindPoses.Count)
                return false;

            Matrix4x4 boneToRoot = bindPoses[boneIndex].inverse;
            Transform parent = bone.parent;
            int parentIndex = parent != null ? bones.IndexOf(parent) : -1;
            if (parentIndex >= 0 && parentIndex < bindPoses.Count)
            {
                Matrix4x4 parentToRoot = bindPoses[parentIndex].inverse;
                matrix = parentToRoot.inverse * boneToRoot;
            }
            else
            {
                matrix = boneToRoot;
            }

            return true;
        }


        /// <summary>
        /// This function retrieves an array of transforms for the specified bone names.
        /// It iterates through the input names, finds the matching transform in the global bones list,
        /// and then appends the root bone transform as the final element before returning the assembled array.
        /// </summary>
        /// <param name="boneNames"></param>
        /// <returns></returns>
        public Transform[] GetBones(string[] boneNames)
        {
            if (boneNames == null || boneNames.Length == 0)
            {
                // Return all bones if boneNames is empty or null
                Transform[] allBoneTransforms = new Transform[bones.Count + 1];
                for (int i = 0; i < bones.Count; i++)
                {
                    allBoneTransforms[i] = bones[i];
                }
                allBoneTransforms[bones.Count] = rootBone;
                return allBoneTransforms;
            }
            else
            {
                // Get only the bones with names in the boneNames array
                Transform[] boneTransforms = new Transform[boneNames.Length + 1];
                for (int i = 0; i < boneNames.Length; i++)
                {
                    boneTransforms[i] = bones.Find(bone => bone.name == boneNames[i]);
                }
                boneTransforms[boneNames.Length] = rootBone;
                return boneTransforms;
            }
        }

        public void GetBoneNoGC(string boneName, out Transform boneTransform)
        {
            boneTransform = bones.Find(bone => bone.name == boneName);
        }

        /// <summary>
        /// Get hook Transform by name
        /// 根据名称获取挂点变换
        /// </summary>
        /// <param name="hookName">Hook name / 挂点名称</param>
        /// <param name="recursive">Search in child models / 在子模型中搜索</param>
        /// <returns>Hook Transform or null if not found / 挂点变换，未找到返回null</returns>
        public Transform GetHook(string hookName, bool recursive = true)
        {
            if (string.IsNullOrEmpty(hookName))
            {
                return null;
            }

            if (hooks == null)
                hooks = new Dictionary<string, Transform>();
            
            // Direct lookup
            // 直接查找
            if (hooks.TryGetValue(hookName, out Transform hook) && hook != null)
            {
                return hook;
            }

            // Prefab GameObject names are often HH_ride while C++ constants use HH_Ride.
            // Dictionary is not serialized, so runtime lookup must search the hierarchy.
            // 预制体节点名常为 HH_ride，而 C++ 常量为 HH_Ride。Dictionary 不序列化，运行时必须扫层级。
            Transform childHook = FindChildRecursive(transform, hookName);
            if (childHook != null)
            {
                hooks[hookName] = childHook;
                return childHook;
            }
            
            // Recursive search in child models (if recursive flag is true)
            // 在子模型中递归搜索（如果递归标志为true）
            if (recursive)
            {
                // Search in child SkeletonBuilders (weapons, pets, etc.)
                // 在子SkeletonBuilder中搜索（武器、宠物等）
                SkeletonBuilder[] childBuilders = GetComponentsInChildren<SkeletonBuilder>(true);
                foreach (SkeletonBuilder childBuilder in childBuilders)
                {
                    if (childBuilder == this) continue;  // Skip self
                    
                    childHook = childBuilder.GetHook(hookName, false);  // Don't recurse again
                    if (childHook != null)
                        return childHook;
                }
            }
            return null;
        }
        Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name))
                return null;

            foreach (Transform child in parent)
            {
                if (child.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    return child;

                Transform found = FindChildRecursive(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
#if MODEL_RENDERER_PROJECT
        private A3DSkeleton _skeleton;
        private int _rootBoneIndex;

        public void BuildSkeleton(A3DSkeleton skeleton)
        {
            if (skeleton == null)
            {
                Debug.LogError("No skeleton data found.");
                return;
            }

            _skeleton = skeleton;
            _angelicaRelativeByName.Clear();


            Dictionary<int, GameObject> boneGameObjects = new Dictionary<int, GameObject>();
            GameObject boneGO = null;
            // Create GameObject for each bone
            for (int i = 0; i < skeleton.m_aBones.Count; i++)
            {
                boneGO = new GameObject(skeleton.m_aBones[i].m_strName);
                boneGameObjects[i] = boneGO;
            }

            A3DBone bone;


            // first find the root bone and set up the relative TM for each bone
            for (int i = 0; i < skeleton.m_aBones.Count; i++)
            {
                bone = skeleton.m_aBones[i];
                bone.m_relativeTM = CreateMatrixFromA3DMatrix4X4(bone.boneData.matRelative);
                _angelicaRelativeByName[bone.m_strName] = bone.m_relativeTM;
                // Persist A3DBone::GetOriginalMatrix() on the generated prefab.
                // The dictionary above is runtime-only and is empty after prefab reload.
                BrewMonster.AngelicaBoneRelative.Bind(
                    boneGameObjects[i].transform, bone.m_relativeTM);
                Console.WriteLine($"Bone {bone.m_strName} relative\n{bone.m_relativeTM}");
                if (bone.boneData.iParent < 0)
                {
                    _rootBoneIndex = i;
                    rootBone = boneGameObjects[i].transform;
                }
            }

            // Build upToRootTM matrix for each bone. Start from the root bone.
            // for now it's not required
            BuildUpToRootMatrix(skeleton.m_aBones[_rootBoneIndex]);

            // setup parent-child relationship
            foreach (var kvp in boneGameObjects)
            {
                int boneIndex = kvp.Key;
                boneGO = kvp.Value;
                int parentIndex = skeleton.m_aBones[boneIndex].boneData.iParent;

                if (parentIndex >= 0)
                {
                    boneGO.transform.parent = boneGameObjects[parentIndex].transform;
                }
                else
                {
                    boneGO.transform.parent = this.transform; // Set root bones to be children of the skeleton GameObject
                }
            }
            
            // Set up position of each bone
            foreach (var kvp in boneGameObjects)
            {
                int boneIndex = kvp.Key;
                boneGO = kvp.Value;
                bone = skeleton.m_aBones[boneIndex];
                
                Matrix4x4 inverseInitMatrix = CreateMatrixFromA3DMatrix4X4(bone.boneData.matBoneInit).inverse;
                Vector3 boneWorldPosition = inverseInitMatrix.MultiplyPoint3x4(Vector3.zero);

                boneGO.transform.position = boneWorldPosition;
                boneGO.transform.rotation = Quaternion.LookRotation(inverseInitMatrix.GetColumn(2), inverseInitMatrix.GetColumn(1));
                boneGO.transform.localScale = inverseInitMatrix.lossyScale;
                
                // print out data as human readable
                PrintOutData(boneGO, bone.m_upToRootTM, inverseInitMatrix, inverseInitMatrix.rotation.eulerAngles, bone);
            }

            // Add bones to the list and build bind poses for each bone.
            for (int i = 0; i < skeleton.m_aBones.Count; i++)
            {
                boneGO = boneGameObjects[i];
                bones.Add(boneGO.transform);

                if (i != _rootBoneIndex)
                {
                    // Calculate bind pose using worldToLocalMatrix
                    bindPoses.Add(boneGO.transform.worldToLocalMatrix * rootBone.localToWorldMatrix);
                }
                else // Root bone
                {
                    bindPoses.Add(boneGO.transform.worldToLocalMatrix);
                }
            }

            // now we have to create the hooks for the skeleton
            for (int i = 0; i < skeleton.m_aHooks.Count; i++)
            {
                A3DSkeletonHook hook = skeleton.m_aHooks[i];
                GameObject hookGO = new GameObject(hook.m_strName);
                hookGO.transform.parent = hook.Data.iBone >= 0 ? boneGameObjects[hook.Data.iBone].transform : rootBone;
                hookGO.transform.localPosition = CreateMatrixFromA3DMatrix4X4(hook.Data.matHookTM).GetColumn(3);
                hookGO.transform.localRotation = Quaternion.LookRotation(CreateMatrixFromA3DMatrix4X4(hook.Data.matHookTM).GetColumn(2), CreateMatrixFromA3DMatrix4X4(hook.Data.matHookTM).GetColumn(1));
                hookGO.transform.localScale = CreateMatrixFromA3DMatrix4X4(hook.Data.matHookTM).lossyScale;
                
                // Store hook in dictionary for lookup
                // 将挂点存储在字典中以便查找
                hooks[hook.m_strName] = hookGO.transform;
            }
        }

        public Matrix4x4[] GetBindPoses(string[] boneNames)
        {
            Matrix4x4[] boneBindPoses = null;
            if (boneNames?.Length > 0)
            {
                boneBindPoses = new Matrix4x4[boneNames.Length + 1];
                for (int i = 0; i < boneNames.Length; i++)
                {
                    boneBindPoses[i] = bindPoses[bones.FindIndex(bone => bone.name == boneNames[i])];
                }
                boneBindPoses[boneNames.Length] = bindPoses[_rootBoneIndex];
            }
            else
            {
                boneBindPoses = new Matrix4x4[bones.Count + 1];
                for (int i = 0; i < bones.Count; i++)
                {
                    boneBindPoses[i] = bindPoses[i];
                }
                boneBindPoses[bones.Count] = bindPoses[_rootBoneIndex];
            }

            return boneBindPoses;
        }

        private Matrix4x4 CreateMatrixFromA3DMatrix4X4(A3DMATRIX4 mat)
        {
            return new Matrix4x4(
                new Vector4(mat.m[0], mat.m[1], mat.m[2], mat.m[3]),
                new Vector4(mat.m[4], mat.m[5], mat.m[6], mat.m[7]),
                new Vector4(mat.m[8], mat.m[9], mat.m[10], mat.m[11]),
                new Vector4(mat.m[12], mat.m[13], mat.m[14], mat.m[15])
            );
        }

        /// <summary>
        /// Build the upToRootTM matrix for each bone in the skeleton. <br/>
        /// Just send in the root bone and this function will run recursively to build the upToRootTM matrix for each bone.
        /// </summary>
        /// <param name="rootBone"></param>
        private void BuildUpToRootMatrix(A3DBone rootBone)
        {
            if (_skeleton == null)
            {
                Debug.LogError("No skeleton data found.");
                return;
            }

            // Get the bone's parent index
            int parentIndex = rootBone.boneData.iParent;
            if (parentIndex < 0)
            {
                rootBone.m_upToRootTM = rootBone.m_relativeTM;
            }
            else
            {
                // Get the parent bone
                A3DBone parentBone = _skeleton.m_aBones[parentIndex];
                // Calculate the upToRootTM matrix
                rootBone.m_upToRootTM = parentBone.m_upToRootTM * rootBone.m_relativeTM;
            }

            for (int i = 0; i < rootBone.m_aChildren.Length; i++)
            {
                BuildUpToRootMatrix(_skeleton.m_aBones[rootBone.m_aChildren[i]]);
            }
        }


        private void PrintOutData(GameObject boneGO, Matrix4x4 relativeMatrix, Matrix4x4 initMatrix,
            Vector3 initRotation, A3DBone bone)
        {
            // debug log the relative matrix in a human readable format
            Console.WriteLine($"Bone {boneGO.name} relative\n{relativeMatrix}\ninit\n{initMatrix}");

            Console.WriteLine($"{boneGO.name} Flipped: {bone.boneData.byFlags}");

            // print out matBoneInit position
            Console.WriteLine($"Bone {boneGO.name} matBoneInit position: {initMatrix.GetColumn(3)}");
            // print out matBoneInit rotation
            Console.WriteLine($"Bone {boneGO.name} matBoneInit rotation: {initRotation}");
        }

#endif
    }
}