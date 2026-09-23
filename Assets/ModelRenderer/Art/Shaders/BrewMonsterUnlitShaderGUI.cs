using UnityEngine;
using System;
using UnityEngine.Rendering;

#if UNITY_EDITOR
// UnityEditor only exists in the editor, and this file is not inside an Editor folder, so this using
// has to stay inside the guard - pulled above it, the whole file fails to compile in a player build.
using UnityEditor;

/// <summary>
/// Custom inspector for <c>BrewMonster/UnlitWithShadows</c>
/// (Assets/Test/ModelRenderer/Art/Shaders/CustomUnlitShader.shader).
/// Keep the property list below in sync with that shader's Properties block.
/// Hidden markers written by the Surface Type popup: <c>_Surface</c> / <c>_RenderType</c>.
/// Hidden properties driven by code: <c>_PWColorMode</c> (CustomizePreviewApplier),
/// <c>_AmbientDayColor</c> (GlobalShaderVariableSetting).
/// </summary>
public class BrewMonsterUnlitShaderGUI : ShaderGUI
{
    // Properties
    private MaterialProperty colorProp;
    private MaterialProperty baseMapProp;
    private MaterialProperty highMapProp;
    private MaterialProperty skinColorProp;
    private MaterialProperty shadowStrengthProp;
    private MaterialProperty billboardProp;
    private MaterialProperty billboardPivotProp;
    private MaterialProperty billboardPivotTexProp;
    private MaterialProperty billboardNormalTexProp;
    private MaterialProperty surfaceProp;
    private MaterialProperty renderTypeProp;
    private MaterialProperty srcBlendProp;
    private MaterialProperty dstBlendProp;
    private MaterialProperty zWriteProp;
    private MaterialProperty cullProp;

    // UI Labels
    private static readonly GUIContent baseMapLabel = new GUIContent("Base Map");
    private static readonly GUIContent highMapLabel = new GUIContent("High Map", "Second texture, sampled when PW Color Mode = Blend (eyes / brows / mouth).");
    private static readonly GUIContent shadowStrengthLabel = new GUIContent("Shadow Strength");
    private static readonly GUIContent surfaceOptionsLabel = new GUIContent("Surface Options");
    private static readonly GUIContent faceOptionsLabel = new GUIContent("Face Blend", "Only sampled when PW Color Mode = Blend (eyes / brows / mouth).");
    private static readonly GUIContent billboardOptionsLabel = new GUIContent("Billboard");
    private static readonly GUIContent billboardLabel = new GUIContent("Face Camera", "Turn this mesh around its pivot so it faces the camera, Y axis kept upright. These materials render the back face, so the mesh's BACK side is the one aimed at the camera; flip facingSign in the shader's vert to aim the front side instead. Off by default - without it every vertex keeps its authored transform. Drives the _BILLBOARD_ON keyword, so the billboard is compiled out of the vertex shader when it is off.");
    private static readonly GUIContent advancedBlendLabel = new GUIContent("Advanced Blend", "Raw blend state, normally written by the Surface Type popup. Change it only when you need a blend setup that popup does not cover.");
    private static readonly GUIContent billboardPivotLabel = new GUIContent("Pivot Per Quad/Triangle", "Rotate every vertex around the centre of its own quad/triangle instead of around the mesh origin - for meshes that are a set of leaf cards, a tree canopy for instance. The centres and plane normals come from the baked textures below; with none assigned the pivots read as zero and the billboard falls back to the mesh origin. Needs shader model 4.5 (SV_VertexID + vertex texture fetch). Drives the _BILLBOARD_PER_VERTEX_PIVOT keyword.");
    private static readonly GUIContent billboardPivotTexLabel = new GUIContent("Quad Pivot (baked)", "Object-space centre of the quad each vertex belongs to - one texel per vertex, addressed by SV_VertexID. Written next to the prefab by Tools > BrewMonster > Bake Billboard Quad Data; a hand-assigned texture will not line up.");
    private static readonly GUIContent billboardNormalTexLabel = new GUIContent("Quad Normal (baked)", "Object-space plane normal of that same quad, one texel per vertex. Written by the billboard data baker together with the pivot texture.");
    private static readonly string[] surfaceNames = Enum.GetNames(typeof(SurfaceType));

    // Editor state
    private bool showAdvancedBlend;

    // Enums
    /// <remarks>Values must match the shader's hidden <c>_RenderType</c> (0 Opaque, 1 Transparent, 2 Cutout).</remarks>
    public enum SurfaceType
    {
        Opaque = 0,
        Transparent = 1,
        Cutout = 2
    }

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        // Find properties (keep in sync with the Properties block of CustomUnlitShader.shader).
        colorProp = FindProperty("_Color", properties);
        baseMapProp = FindProperty("_BaseMap", properties);
        highMapProp = FindProperty("_HighMap", properties);
        skinColorProp = FindProperty("_SkinColor", properties);
        shadowStrengthProp = FindProperty("_ShadowStrength", properties);
        billboardProp = FindProperty("_Billboard", properties);
        billboardPivotProp = FindProperty("_BillboardPivot", properties);
        billboardPivotTexProp = FindProperty("_BillboardPivotTex", properties);
        billboardNormalTexProp = FindProperty("_BillboardNormalTex", properties);
        surfaceProp = FindProperty("_Surface", properties);
        renderTypeProp = FindProperty("_RenderType", properties);
        srcBlendProp = FindProperty("_SrcBlend", properties);
        dstBlendProp = FindProperty("_DstBlend", properties);
        zWriteProp = FindProperty("_ZWrite", properties);
        cullProp = FindProperty("_Cull", properties);

        Material[] materials = Array.ConvertAll(materialEditor.targets, target => (Material)target);

        // Surface
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(surfaceOptionsLabel, EditorStyles.boldLabel);
        DoSurfaceTypePopup(materialEditor, materials);
        materialEditor.ShaderProperty(cullProp, "Cull Mode");
        DoAdvancedBlendFoldout(materialEditor);

        // Base map / shadow
        EditorGUILayout.Space();
        materialEditor.TexturePropertySingleLine(baseMapLabel, baseMapProp, colorProp);
        materialEditor.ShaderProperty(shadowStrengthProp, shadowStrengthLabel);

        // Face blend (High Map)
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(faceOptionsLabel, EditorStyles.boldLabel);
        materialEditor.TexturePropertySingleLine(highMapLabel, highMapProp, skinColorProp);

        // Billboard
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(billboardOptionsLabel, EditorStyles.boldLabel);
        materialEditor.ShaderProperty(billboardProp, billboardLabel);
        materialEditor.ShaderProperty(billboardPivotProp, billboardPivotLabel);
        materialEditor.TexturePropertySingleLine(billboardPivotTexLabel, billboardPivotTexProp);
        materialEditor.TexturePropertySingleLine(billboardNormalTexLabel, billboardNormalTexProp);

        EditorGUILayout.Space();
        materialEditor.RenderQueueField();
        materialEditor.DoubleSidedGIField();
    }

    private void DoSurfaceTypePopup(MaterialEditor materialEditor, Material[] materials)
    {
        SurfaceType currentType = GetSurfaceType(materials[0]);
        bool mixedValue = false;
        for (int i = 1; i < materials.Length; i++)
        {
            if (GetSurfaceType(materials[i]) != currentType)
            {
                mixedValue = true;
                break;
            }
        }

        EditorGUI.showMixedValue = mixedValue;
        EditorGUI.BeginChangeCheck();
        var selectedSurfaceType = (SurfaceType)EditorGUILayout.Popup("Surface Type", (int)currentType, surfaceNames);
        EditorGUI.showMixedValue = false;

        if (!EditorGUI.EndChangeCheck())
        {
            return;
        }

        materialEditor.RegisterPropertyChangeUndo("Surface Type");

        // Hidden markers (the undo system tracks them through their MaterialProperty).
        surfaceProp.floatValue = selectedSurfaceType == SurfaceType.Transparent ? 1f : 0f;
        renderTypeProp.floatValue = (float)selectedSurfaceType;

        foreach (Material material in materials)
        {
            SetupMaterialBlendMode(material, selectedSurfaceType);
        }
    }

    private void DoAdvancedBlendFoldout(MaterialEditor materialEditor)
    {
        showAdvancedBlend = EditorGUILayout.Foldout(showAdvancedBlend, advancedBlendLabel, true);
        if (!showAdvancedBlend)
        {
            return;
        }

        EditorGUI.indentLevel++;
        materialEditor.ShaderProperty(srcBlendProp, "Src Blend");
        materialEditor.ShaderProperty(dstBlendProp, "Dst Blend");
        materialEditor.ShaderProperty(zWriteProp, "Z Write");
        EditorGUI.indentLevel--;
    }

    /// <summary>Writes the shader's hidden surface markers (<c>_Surface</c> / <c>_RenderType</c>).</summary>
    public static void SetupMaterialSurfaceType(Material material, SurfaceType surfaceType)
    {
        if (material == null)
        {
            return;
        }

        material.SetFloat("_Surface", surfaceType == SurfaceType.Transparent ? 1f : 0f);
        material.SetFloat("_RenderType", (float)surfaceType);
    }

    /// <summary>Applies blend state, render queue and the render-type tag for <paramref name="surfaceType"/>.</summary>
    public static void SetupMaterialBlendMode(Material material, SurfaceType surfaceType)
    {
        if (material == null)
        {
            return;
        }

        switch (surfaceType)
        {
            case SurfaceType.Transparent:
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.renderQueue = (int)RenderQueue.Transparent;
                break;

            case SurfaceType.Cutout:
                material.SetOverrideTag("RenderType", "TransparentCutout");
                material.SetInt("_SrcBlend", (int)BlendMode.One);
                material.SetInt("_DstBlend", (int)BlendMode.Zero);
                material.SetInt("_ZWrite", 1);
                material.renderQueue = (int)RenderQueue.AlphaTest;
                break;

            case SurfaceType.Opaque:
            default:
                material.SetOverrideTag("RenderType", "Opaque");
                material.SetInt("_SrcBlend", (int)BlendMode.One);
                material.SetInt("_DstBlend", (int)BlendMode.Zero);
                material.SetInt("_ZWrite", 1);
                material.renderQueue = (int)RenderQueue.Geometry;
                break;
        }
    }

    /// <summary>
    /// Reads the surface type from <c>_RenderType</c>, falling back to the legacy <c>_Surface</c>
    /// flag (0 = Opaque, 1 = Transparent) for materials saved before <c>_RenderType</c> existed.
    /// </summary>
    private static SurfaceType GetSurfaceType(Material material)
    {
        if (material.HasProperty("_RenderType"))
        {
            int renderType = Mathf.RoundToInt(material.GetFloat("_RenderType"));
            if (renderType == (int)SurfaceType.Transparent || renderType == (int)SurfaceType.Cutout)
            {
                return (SurfaceType)renderType;
            }
        }

        if (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f)
        {
            return SurfaceType.Transparent;
        }

        return SurfaceType.Opaque;
    }
}
#endif
