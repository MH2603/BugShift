using BrewMonster.Scripts.Grass;
using BrewMonster.Scripts.Grass.Test;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrewMonster.EditorTools
{
    /// <summary>
    /// One-click setup for the grass interaction test. Adds a <c>Grass Trample Test</c> root to the OPEN scene with:
    ///   • Grass Field  — <see cref="GrassTestField"/>: GPU-instanced billboard blades, generated in code;
    ///   • Player       — a capsule with <see cref="GrassTestPlayer"/> (WASD/arrows, Shift = run, P = auto-walk) and
    ///                    the <see cref="GrassInteractionController"/> that publishes the shader globals;
    ///   • Test Camera  — <see cref="GrassTestCamera"/> following the capsule;
    ///   • Ground       — a plane so the blades do not float, plus a directional light if the scene has none.
    ///
    /// Press Play and the capsule walks through the grass by itself (auto-walk), so the trample is visible without
    /// touching anything; any movement key hands control back to the keyboard. Everything is ordinary scene objects:
    /// the operation is undoable, and running the menu twice only selects the rig that is already there.
    /// </summary>
    public static class GrassTrampleTestRigBuilder
    {
        private const string RootName = "Grass Trample Test";
        private const string UndoLabel = "Add Grass Trample Test Rig";

        [MenuItem("Tools/BrewMonster/Add Grass Trample Test Rig", priority = 20)]
        private static void AddRig()
        {
            GameObject pExisting = GameObject.Find(RootName);
            if (pExisting != null)
            {
                Selection.activeGameObject = pExisting;
                EditorUtility.DisplayDialog(
                    "Grass Trample Test",
                    $"'{RootName}' is already in this scene, so nothing was added — it is selected instead.",
                    "OK");
                return;
            }

            GameObject pRoot = CreateObject(RootName, null, Vector3.zero);

            //	Ground: the blade cards start at y = 0, so a plane gives the eye something to judge the bend against.
            GameObject pGround = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Undo.RegisterCreatedObjectUndo(pGround, UndoLabel);
            pGround.name = "Ground";
            pGround.transform.SetParent(pRoot.transform, false);
            pGround.transform.localScale = Vector3.one * 6f;         //	Plane is 10 m, so 60 m across

            //	Grass patch.
            GameObject pGrass = CreateObject("Grass Field", pRoot.transform, Vector3.zero);
            GrassTestField pField = pGrass.AddComponent<GrassTestField>();

            //	Mockup player: a capsule floating 1 m up so it stands on the ground (capsule height is 2 m).
            GameObject pPlayer = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(pPlayer, UndoLabel);
            pPlayer.name = "Player (mockup)";
            pPlayer.transform.SetParent(pRoot.transform, false);
            pPlayer.transform.localPosition = new Vector3(0f, 1f, 0f);
            GrassTestPlayer pPlayerScript = pPlayer.AddComponent<GrassTestPlayer>();

            //	The interaction controller publishes the player state the grass shader reads (doc §5). It sits on the
            //	capsule it follows, so the whole rig is self-contained.
            GrassInteractionController pInteraction = pPlayer.AddComponent<GrassInteractionController>();
            pInteraction.Player = pPlayer.transform;
            pInteraction.MovementSource = pPlayer.transform;

            //	Camera. No AudioListener (the scene very likely already has one) and a higher depth so it wins over the
            //	scene's own camera instead of fighting it for the same render order.
            GameObject pCameraObject = CreateObject("Test Camera", pRoot.transform, new Vector3(0f, 4.4f, -6f));
            Camera pCamera = pCameraObject.AddComponent<Camera>();
            pCamera.depth = 1f;
            GrassTestCamera pFollow = pCameraObject.AddComponent<GrassTestCamera>();

            pField.Interaction = pInteraction;
            pPlayerScript.ViewCamera = pCamera;
            pFollow.Target = pPlayer.transform;

            pCameraObject.transform.LookAt(pPlayer.transform.position + Vector3.up * 0.9f);

            EnsureDirectionalLight(pRoot.transform);

            EditorSceneManager.MarkSceneDirty(pRoot.scene);
            Selection.activeGameObject = pRoot;

            Debug.Log($"[{nameof(GrassTrampleTestRigBuilder)}] Added '{RootName}'. Press Play: the capsule walks the " +
                      "grass by itself (P toggles auto-walk), or take over with WASD/arrows and Shift to run.");
        }

        [MenuItem("Tools/BrewMonster/Remove Grass Trample Test Rig", priority = 21)]
        private static void RemoveRig()
        {
            GameObject pRoot = GameObject.Find(RootName);
            if (pRoot == null)
            {
                EditorUtility.DisplayDialog(
                    "Grass Trample Test",
                    $"There is no '{RootName}' in this scene.",
                    "OK");
                return;
            }

            Undo.DestroyObjectImmediate(pRoot);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        /// <summary>
        /// URP has no ambient/directional fallback the grass shader would pick up, so a scene without a light renders
        /// the blades flat dark — add a basic sun when the scene has none.
        /// </summary>
        private static void EnsureDirectionalLight(Transform pParent)
        {
            if (Object.FindObjectsByType<Light>().Length > 0)
                return;

            GameObject pLightObject = CreateObject("Directional Light (test)", pParent, new Vector3(0f, 8f, 0f));
            Light pLight = pLightObject.AddComponent<Light>();
            pLight.type = LightType.Directional;
            pLight.intensity = 1.2f;
            pLight.color = new Color(1f, 0.96f, 0.89f);
            pLightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static GameObject CreateObject(string p_strName, Transform p_parent, Vector3 p_vLocalPosition)
        {
            GameObject pObject = new GameObject(p_strName);
            Undo.RegisterCreatedObjectUndo(pObject, UndoLabel);
            pObject.transform.SetParent(p_parent, false);
            pObject.transform.localPosition = p_vLocalPosition;
            return pObject;
        }
    }
}
