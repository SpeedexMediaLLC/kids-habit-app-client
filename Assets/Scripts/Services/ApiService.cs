// ApiService: Supabase RPC 9 個の薄いラッパ (static class)
// 戻り値は (string ResultCode, JObject Raw). 型付き DTO は M1 では作らない.

using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

public static class ApiService
{
    public readonly struct RpcResult
    {
        public RpcResult(string resultCode, JObject raw)
        {
            ResultCode = resultCode;
            Raw = raw;
        }
        public string ResultCode { get; }
        public JObject Raw { get; }
    }

    public static UniTask<RpcResult> CreateFamilyWithParentAsync(
        string familyName,
        string parentNickname,
        string childNickname,
        string passcode,
        string consentVersion)
    {
        return CallAsync("create_family_with_parent", new Dictionary<string, object>
        {
            { "p_family_name", familyName },
            { "p_parent_nickname", parentNickname },
            { "p_child_nickname", childNickname },
            { "p_passcode", passcode },
            { "p_consent_version", consentVersion },
        });
    }

    public static UniTask<RpcResult> RecordHabitAsync(
        Guid targetMemberId,
        Guid habitId,
        Guid clientEventId)
    {
        return CallAsync("record_habit", new Dictionary<string, object>
        {
            { "p_target_member_id", targetMemberId },
            { "p_habit_id", habitId },
            { "p_client_event_id", clientEventId },
        });
    }

    public static UniTask<RpcResult> VerifyPasscodeAsync(string passcode)
    {
        return CallAsync("verify_passcode", new Dictionary<string, object>
        {
            { "p_passcode", passcode },
        });
    }

    public static UniTask<RpcResult> ChangePasscodeAsync(string oldPasscode, string newPasscode)
    {
        return CallAsync("change_passcode", new Dictionary<string, object>
        {
            { "p_old_passcode", oldPasscode },
            { "p_new_passcode", newPasscode },
        });
    }

    public static UniTask<RpcResult> AddHabitAsync(
        Guid targetMemberId,
        Guid? templateId,
        string customTitle,
        string intensity)
    {
        return CallAsync("add_habit", new Dictionary<string, object>
        {
            { "p_target_member_id", targetMemberId },
            { "p_template_id", templateId.HasValue ? (object)templateId.Value : null },
            { "p_custom_title", customTitle },
            { "p_intensity", intensity },
        });
    }

    public static UniTask<RpcResult> UpdateHabitAsync(
        Guid habitId,
        string title,
        string intensity,
        bool isActive)
    {
        return CallAsync("update_habit", new Dictionary<string, object>
        {
            { "p_habit_id", habitId },
            { "p_title", title },
            { "p_intensity", intensity },
            { "p_is_active", isActive },
        });
    }

    public static UniTask<RpcResult> DeleteHabitAsync(Guid habitId)
    {
        return CallAsync("delete_habit", new Dictionary<string, object>
        {
            { "p_habit_id", habitId },
        });
    }

    public static UniTask<RpcResult> RequestAccountDeletionAsync()
    {
        return CallAsync("request_account_deletion", null);
    }

    public static UniTask<RpcResult> CancelAccountDeletionAsync(Guid deletionRequestId)
    {
        return CallAsync("cancel_account_deletion", new Dictionary<string, object>
        {
            { "p_deletion_request_id", deletionRequestId },
        });
    }

    private static async UniTask<RpcResult> CallAsync(string procedureName, object parameters)
    {
        var response = await SupabaseService.Client.Rpc(procedureName, parameters);
        var content = response?.Content ?? "";
        JObject json;
        if (string.IsNullOrEmpty(content))
        {
            json = new JObject();
        }
        else if (content.TrimStart().StartsWith("{"))
        {
            json = JObject.Parse(content);
        }
        else
        {
            json = new JObject { ["raw"] = content };
        }
        var resultCode = json["result_code"]?.ToString() ?? "";
        return new RpcResult(resultCode, json);
    }
}
