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
        UnityEngine.Debug.Log(
            $"[ApiService] CallAsync begin function={procedureName} initialized={SupabaseService.IsInitialized}");
        // RuntimeInitializeOnLoadMethod による自動初期化が走っていない / まだ完了
        // していない経路 (シーン単独起動・初回 RPC が早すぎる場合等) でも確実に
        // 認証付き Client が用意されるよう、毎回 InitializeAsync を await する。
        // IsInitialized なら即返、進行中なら同じ UniTask が共有される (single-flight)。
        if (!SupabaseService.IsInitialized)
        {
            await SupabaseService.InitializeAsync();
        }
        UnityEngine.Debug.Log(
            $"[ApiService] await InitializeAsync done initialized={SupabaseService.IsInitialized}");

        UnityEngine.Debug.Log($"[ApiService] Client.Rpc begin function={procedureName}");
        var response = await SupabaseService.Client.Rpc(procedureName, parameters);
        // M5: サーバー時刻 anchor をレスポンスの Date ヘッダから供給する (ServerClock §5.4.1)。
        // 専用 RPC を足さず既存応答を使う = server 無改修。
        ServerClock.FeedFromHttp(response?.ResponseMessage);
        // BaseResponse には StatusCode プロパティが直接無いため ResponseMessage 経由で取得。
        // HttpResponseMessage が null の場合 (応答自体無し) も視覚化できるよう "null" 表示にする。
        UnityEngine.Debug.Log(
            $"[ApiService] Client.Rpc returned status={response?.ResponseMessage?.StatusCode.ToString() ?? "null"} " +
            $"content_len={response?.Content?.Length ?? -1}");

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
        UnityEngine.Debug.Log($"[ApiService] CallAsync end function={procedureName} result_code='{resultCode}'");
        return new RpcResult(resultCode, json);
    }
}
