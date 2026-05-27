using System;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class HabitButton : MonoBehaviour
{
    [SerializeField] private PressEffectPlayer pressEffectPlayer;
    [SerializeField] private string targetMemberId;
    [SerializeField] private string habitId;

    public void OnClick()
    {
        Debug.Log("habit button pressed");
        if (pressEffectPlayer != null)
        {
            pressEffectPlayer.Play();
        }
        RecordHabitAsync().Forget();
    }

    private async UniTask RecordHabitAsync()
    {
        if (string.IsNullOrWhiteSpace(targetMemberId) || string.IsNullOrWhiteSpace(habitId))
        {
            Debug.LogError("[HabitButton] targetMemberId / habitId is not set in Inspector");
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
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HabitButton] record_habit RPC failed: {ex.Message}");
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
