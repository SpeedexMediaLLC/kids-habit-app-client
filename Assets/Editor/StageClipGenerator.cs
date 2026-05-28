// StageClipGenerator: Step 7 のプレースホルダー演出用 AnimationClip 2 本を
// AnimationUtility 経由で生成 (Codex レビュー判定の案 C = コード生成、
// YAML 手書きの破損リスクを回避)。
//
// 出力先 (既存 .anim を上書き、GUID 保持):
//   Assets/Animations/Stage_Up.anim
//     scale x/y/z: t=0.00→1.0, t=0.25→1.2, t=0.50→1.0 (length=0.5s, loop=off)
//   Assets/Animations/Stage_Up_To_Grown.anim
//     scale x/y/z: t=0.00→1.0, t=0.20→1.4, t=0.40→1.0 (length=0.4s, loop=off)
//
// binding: path="" (Prefab ルート自身の Transform), m_LocalScale.x / .y / .z
// tangent: ClampedAuto (Auto/Smooth 相当、行き過ぎ無しの自然な膨らみ)
//
// 既存 .anim 不在で新規 CreateAsset した場合は GUID が変わるため
// Animator Controller の State Motion 割当が外れる旨を Warning 出力。

using System.IO;
using UnityEditor;
using UnityEngine;

public static class StageClipGenerator
{
    private const string StageUpPath = "Assets/Animations/Stage_Up.anim";
    private const string StageUpToGrownPath = "Assets/Animations/Stage_Up_To_Grown.anim";

    [MenuItem("Tools/Generate Stage Clips")]
    public static void GenerateStageClips()
    {
        Debug.Log("[StageClipGenerator] Generation started");

        GenerateClip(
            path: StageUpPath,
            keyframes: new[]
            {
                new Keyframe(0.00f, 1.0f),
                new Keyframe(0.25f, 1.2f),
                new Keyframe(0.50f, 1.0f),
            });

        GenerateClip(
            path: StageUpToGrownPath,
            keyframes: new[]
            {
                new Keyframe(0.00f, 1.0f),
                new Keyframe(0.20f, 1.4f),
                new Keyframe(0.40f, 1.0f),
            });

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[StageClipGenerator] Generation complete (Stage_Up.anim + Stage_Up_To_Grown.anim)");
    }

    private static void GenerateClip(string path, Keyframe[] keyframes)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        bool preserveGuid = clip != null;

        if (!preserveGuid)
        {
            clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path) };
        }

        // 既存の curve / event をクリアして再構築 (前回生成の残骸が混ざらないように)
        clip.ClearCurves();

        var curve = new AnimationCurve(keyframes);

        var bindings = new[]
        {
            new EditorCurveBinding
            {
                path = string.Empty, type = typeof(Transform), propertyName = "m_LocalScale.x",
            },
            new EditorCurveBinding
            {
                path = string.Empty, type = typeof(Transform), propertyName = "m_LocalScale.y",
            },
            new EditorCurveBinding
            {
                path = string.Empty, type = typeof(Transform), propertyName = "m_LocalScale.z",
            },
        };

        foreach (var binding in bindings)
        {
            // まず curve を bind
            AnimationUtility.SetEditorCurve(clip, binding, curve);

            // SetEditorCurve 後の curve を取り直して各 key の tangent mode を
            // ClampedAuto (Smooth/行き過ぎ無し) に設定。再度 SetEditorCurve で書き戻す。
            var bound = AnimationUtility.GetEditorCurve(clip, binding);
            for (int i = 0; i < bound.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(bound, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(bound, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            AnimationUtility.SetEditorCurve(clip, binding, bound);
        }

        // Loop OFF (1 回再生で停止)
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        if (preserveGuid)
        {
            EditorUtility.SetDirty(clip);
            Debug.Log(
                $"[StageClipGenerator] Updated {path} " +
                $"length={clip.length:F2}s loopTime={settings.loopTime} (GUID preserved)");
        }
        else
        {
            AssetDatabase.CreateAsset(clip, path);
            Debug.LogWarning(
                $"[StageClipGenerator] {path} did not exist; created with NEW GUID. " +
                "Animator Controller の State Motion 割当が外れた可能性があるため Editor で再確認要");
        }
    }
}
