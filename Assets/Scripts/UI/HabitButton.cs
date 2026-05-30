using System;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class HabitButton : MonoBehaviour
{
    [SerializeField] private PressEffectPlayer pressEffectPlayer;
    [SerializeField] private CreatureDisplay creatureDisplay;
    [SerializeField] private string targetMemberId;
    [SerializeField] private string habitId;

    // M3 Step 6: ホームで選択した member/habit を子供モードに渡す前に注入する.
    // これにより MainScene Inspector の固定値依存を除去 (実行時に上書き).
    public void SetTarget(string newTargetMemberId, string newHabitId)
    {
        targetMemberId = newTargetMemberId;
        habitId = newHabitId;
        Debug.Log($"[HabitButton] SetTarget member={newTargetMemberId} habit={newHabitId}");
    }

    public void OnClick()
    {
        Debug.Log("habit button pressed");
        // 即演出 (光る/サイズアップ/音) ← 体験のため通信を待たない (§5.1-5)。
        if (pressEffectPlayer != null)
        {
            pressEffectPlayer.Play();
        }
        RecordPress();
    }

    // M5: record_habit はオフライン先行コーディネータ (HabitSyncService) 経由にする
    // (ローカル窓チェック → ローカル保存 → 通信時送信, §5.1)。HabitSyncService が無い検証用
    // シーン (TestM1Scene 等・家族/キュー不在) では従来どおり直接 RPC にフォールバックする。
    private void RecordPress()
    {
        if (string.IsNullOrWhiteSpace(targetMemberId) || string.IsNullOrWhiteSpace(habitId))
        {
            Debug.LogError("[HabitButton] targetMemberId / habitId is not set");
            return;
        }
        if (!Guid.TryParse(targetMemberId, out var memberGuid))
        {
            Debug.LogError($"[HabitButton] targetMemberId is not a valid GUID: {targetMemberId}");
            return;
        }
        if (!Guid.TryParse(habitId, out var habitGuid))
        {
            Debug.LogError($"[HabitButton] habitId is not a valid GUID: {habitId}");
            return;
        }

        var sync = HabitSyncService.Instance;
        if (sync != null)
        {
            sync.RequestRecord(memberGuid, habitGuid);
            return;
        }
        LegacyDirectAsync(memberGuid, habitGuid).Forget();
    }

    // 検証用シーン向けの従来経路 (オフラインキューを通さない直接 RPC)。
    private async UniTask LegacyDirectAsync(Guid memberGuid, Guid habitGuid)
    {
        var clientEventId = Guid.NewGuid();

        try
        {
            var result = await ApiService.RecordHabitAsync(memberGuid, habitGuid, clientEventId);
            var growthDelta = FormatToken(result.Raw["growth_points_delta"]);
            var totalGrowth = FormatToken(result.Raw["total_growth_points"]);
            var newStage = FormatToken(result.Raw["new_stage"]);
            var stageChanged = FormatBoolToken(result.Raw["stage_changed"]);
            Debug.Log(
                $"[HabitButton] result_code='{result.ResultCode}' " +
                $"growth_points_delta={growthDelta} total_growth_points={totalGrowth} " +
                $"new_stage='{newStage}' stage_changed={stageChanged} " +
                $"client_event_id={clientEventId}");

            // Step 7: stage_changed=true なら CreatureDisplay に切替を依頼。
            // Trigger 選択 (Stage_Up vs Stage_Up_To_Grown) は CreatureDisplay.SetStage 内で隠蔽。
            if (stageChanged == "true"
                && !string.IsNullOrEmpty(newStage) && newStage != "null")
            {
                if (creatureDisplay != null)
                {
                    creatureDisplay.SetStage(newStage);
                }
                else
                {
                    Debug.LogWarning(
                        "[HabitButton] stage_changed=true but creatureDisplay reference is null; " +
                        "visual switch skipped. MainScene の Inspector で CreatureRoot を割当て要");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HabitButton] record_habit RPC failed: {ex.ToString()}");
        }
    }

    private static string FormatToken(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return "null";
        }
        return token.ToString();
    }

    private static string FormatBoolToken(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return "null";
        }
        return token.ToString().ToLowerInvariant();
    }
}
