// M1Tester: M1 動作検証用 UI (procedural). Empty GameObject にアタッチして Play する.
// UI と EventSystem を Awake() でコード生成 (シーンファイル手書きなし方式).

using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// --- Postgrest POCO (M1 検証用最小定義. ここ以外で再利用しない) ---

[Table("habits")]
public class HabitModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")] public Guid FamilyId { get; set; }
    [Column("member_id")] public Guid MemberId { get; set; }
    [Column("title")]     public string Title { get; set; }
    [Column("intensity")] public string Intensity { get; set; }
    [Column("is_active")] public bool IsActive { get; set; }
}

[Table("creatures")]
public class CreatureModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")] public Guid FamilyId { get; set; }
    [Column("member_id")] public Guid MemberId { get; set; }
    [Column("name")]      public string Name { get; set; }
    [Column("stage")]     public string Stage { get; set; }
    [Column("total_growth_points")] public int TotalGrowthPoints { get; set; }
}

public class M1Tester : MonoBehaviour
{
    private const string DefaultEmail    = "m1tester+a@speedexmedia.com";
    private const string DefaultPassword = "testpass123";
    private const string DefaultFamilyName     = "テスト家A";
    private const string DefaultParentNickname = "とうさんA";
    private const string DefaultChildNickname  = "たろうA";
    private const string DefaultPasscode       = "123456";
    private const string DefaultNewPasscode    = "654321";
    private const string ConsentVersion        = "v1";

    private Text _statusText;
    private InputField _emailInput;
    private InputField _passwordInput;
    private InputField _passcodeInput;
    private InputField _newPasscodeInput;
    private Font _font;

    // CreateFamily の結果からキャッシュ. 以降のボタンで使い回す.
    private Guid? _familyId;
    private Guid? _parentMemberId;
    private Guid? _childMemberId;
    private Guid? _firstHabitId;
    private Guid? _deletionRequestId;  // Request Account Deletion の戻り値からキャッシュ. Cancel で使用.

    private async void Awake()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        BuildEventSystem();
        BuildUI();
        SetStatus("Initializing Supabase...");

        try
        {
            await SupabaseService.InitializeAsync();
            SetStatus("Ready. Sign In or Sign Up to start.");
        }
        catch (Exception ex)
        {
            SetStatus("Init failed: " + ex.Message);
        }
    }

    private void BuildEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }

    private void BuildUI()
    {
        var canvasGO = new GameObject("M1TesterCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        canvasGO.AddComponent<GraphicRaycaster>();

        var panel = new GameObject("Panel");
        panel.transform.SetParent(canvasGO.transform, false);
        var panelRect = panel.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = new Vector2(20, 20);
        panelRect.offsetMax = new Vector2(-20, -20);
        var layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 8;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        _emailInput    = CreateInputField(panel, "email", DefaultEmail);
        _passwordInput = CreateInputField(panel, "password", DefaultPassword);

        CreateButton(panel, "Sign Up",                () => OnSignUp().Forget());
        CreateButton(panel, "Sign In",                () => OnSignIn().Forget());
        CreateButton(panel, "Create Family",          () => OnCreateFamily().Forget());
        CreateButton(panel, "Add Habit",              () => OnAddHabit().Forget());
        CreateButton(panel, "List Habits",            () => OnListHabits().Forget());
        CreateButton(panel, "Record Habit",           () => OnRecordHabit().Forget());
        CreateButton(panel, "Try Direct Stage UPDATE", () => OnTryDirectStageUpdate().Forget());
        CreateButton(panel, "Sign Out",               () => OnSignOut().Forget());

        // ---- passcode 系 + 削除予約フロー (M1 完了条件の検証用ボタン群) ----
        _passcodeInput    = CreateInputField(panel, "passcode_current", DefaultPasscode);
        _newPasscodeInput = CreateInputField(panel, "passcode_new",     DefaultNewPasscode);

        CreateButton(panel, "Verify Passcode",          () => OnVerifyPasscode().Forget());
        CreateButton(panel, "Change Passcode",          () => OnChangePasscode().Forget());
        CreateButton(panel, "Request Account Deletion", () => OnRequestAccountDeletion().Forget());
        CreateButton(panel, "Cancel Account Deletion",  () => OnCancelAccountDeletion().Forget());

        _statusText = CreateStatusText(panel);
    }

    private InputField CreateInputField(GameObject parent, string label, string defaultValue)
    {
        var go = new GameObject("Input_" + label);
        go.transform.SetParent(parent.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0, 50);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.95f, 0.95f, 0.95f);
        var input = go.AddComponent<InputField>();

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 0);
        textRect.offsetMax = new Vector2(-10, 0);
        var text = textGO.AddComponent<Text>();
        text.font = _font;
        text.fontSize = 24;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        input.textComponent = text;
        input.text = defaultValue;
        return input;
    }

    private void CreateButton(GameObject parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button_" + label);
        go.transform.SetParent(parent.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0, 60);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.85f, 0.9f, 0.95f);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var text = textGO.AddComponent<Text>();
        text.text = label;
        text.font = _font;
        text.fontSize = 26;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleCenter;
    }

    private Text CreateStatusText(GameObject parent)
    {
        var go = new GameObject("Status");
        go.transform.SetParent(parent.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0, 400);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.98f, 0.98f, 0.98f);
        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 10);
        textRect.offsetMax = new Vector2(-10, -10);
        var text = textGO.AddComponent<Text>();
        text.font = _font;
        text.fontSize = 22;
        text.color = Color.black;
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private void SetStatus(string s)
    {
        if (_statusText != null) _statusText.text = s;
        Debug.Log("[M1Tester] " + s);
    }

    private void Report(string buttonLabel, ApiService.RpcResult result)
    {
        var raw = result.Raw?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
        if (raw.Length > 200) raw = raw.Substring(0, 200) + "...";
        SetStatus($"{buttonLabel}: {result.ResultCode} / raw={raw}");
    }

    // ---------- Button handlers ----------

    private async UniTask OnSignUp()
    {
        try
        {
            var session = await AuthService.SignUpAsync(_emailInput.text, _passwordInput.text);
            SetStatus($"Sign Up: ok / user={session?.User?.Id} / aud={session?.User?.Aud}");
        }
        catch (Exception ex)
        {
            SetStatus("Sign Up: error / " + ex.Message);
        }
    }

    private async UniTask OnSignIn()
    {
        try
        {
            var session = await AuthService.SignInAsync(_emailInput.text, _passwordInput.text);
            SetStatus($"Sign In: ok / user={session?.User?.Id} / email={session?.User?.Email}");
        }
        catch (Exception ex)
        {
            SetStatus("Sign In: error / " + ex.Message);
        }
    }

    private async UniTask OnCreateFamily()
    {
        try
        {
            var result = await ApiService.CreateFamilyWithParentAsync(
                DefaultFamilyName, DefaultParentNickname, DefaultChildNickname,
                DefaultPasscode, ConsentVersion);
            if (result.ResultCode == "created")
            {
                _familyId       = ParseGuid(result.Raw, "family_id");
                _parentMemberId = ParseGuid(result.Raw, "parent_member_id");
                _childMemberId  = ParseGuid(result.Raw, "child_member_id");
                await AuthService.RefreshSessionAsync();
            }
            Report("Create Family", result);
        }
        catch (Exception ex)
        {
            SetStatus("Create Family: error / " + ex.Message);
        }
    }

    private async UniTask OnAddHabit()
    {
        if (!_childMemberId.HasValue)
        {
            SetStatus("Add Habit: child_member_id 未取得. 先に Create Family を実行.");
            return;
        }
        try
        {
            var result = await ApiService.AddHabitAsync(
                _childMemberId.Value,
                templateId: null,
                customTitle: "テスト習慣",
                intensity: "medium");
            Report("Add Habit", result);
        }
        catch (Exception ex)
        {
            SetStatus("Add Habit: error / " + ex.Message);
        }
    }

    private async UniTask OnListHabits()
    {
        try
        {
            var resp = await SupabaseService.Client.From<HabitModel>().Get();
            var list = resp?.Models ?? new List<HabitModel>();
            _firstHabitId = list.Count > 0 ? list[0].Id : (Guid?)null;
            var first = list.Count > 0
                ? $"id={list[0].Id} title={list[0].Title} intensity={list[0].Intensity}"
                : "(none)";
            SetStatus($"List Habits: count={list.Count} / first: {first}");
        }
        catch (Exception ex)
        {
            SetStatus("List Habits: error / " + ex.Message);
        }
    }

    private async UniTask OnRecordHabit()
    {
        if (!_firstHabitId.HasValue || !_childMemberId.HasValue)
        {
            SetStatus("Record Habit: habit_id / child_member_id 未取得. List Habits と Create Family を先に.");
            return;
        }
        try
        {
            var result = await ApiService.RecordHabitAsync(
                _childMemberId.Value, _firstHabitId.Value, Guid.NewGuid());
            Report("Record Habit", result);
        }
        catch (Exception ex)
        {
            SetStatus("Record Habit: error / " + ex.Message);
        }
    }

    private async UniTask OnTryDirectStageUpdate()
    {
        try
        {
            var resp = await SupabaseService.Client.From<CreatureModel>().Get();
            var list = resp?.Models ?? new List<CreatureModel>();
            if (list.Count == 0)
            {
                SetStatus("Try Direct Stage UPDATE: creatures 0 件. Create Family を先に.");
                return;
            }
            var creature = list[0];
            creature.Stage = "grown";
            try
            {
                await SupabaseService.Client.From<CreatureModel>().Update(creature);
                SetStatus("Try Direct Stage UPDATE: UNEXPECTED success. 列 GRANT 設定確認要.");
            }
            catch (Exception inner)
            {
                SetStatus("Try Direct Stage UPDATE: EXPECTED reject / " + inner.Message);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Try Direct Stage UPDATE: error / " + ex.Message);
        }
    }

    private async UniTask OnSignOut()
    {
        try
        {
            await AuthService.SignOutAsync();
            _familyId = null;
            _parentMemberId = null;
            _childMemberId = null;
            _firstHabitId = null;
            SetStatus("Sign Out: ok");
        }
        catch (Exception ex)
        {
            SetStatus("Sign Out: error / " + ex.Message);
        }
    }

    private async UniTask OnVerifyPasscode()
    {
        try
        {
            var result = await ApiService.VerifyPasscodeAsync(_passcodeInput.text);
            Report("Verify Passcode", result);
        }
        catch (Exception ex)
        {
            SetStatus("Verify Passcode: error / " + ex.Message);
        }
    }

    private async UniTask OnChangePasscode()
    {
        try
        {
            var result = await ApiService.ChangePasscodeAsync(_passcodeInput.text, _newPasscodeInput.text);
            Report("Change Passcode", result);
        }
        catch (Exception ex)
        {
            SetStatus("Change Passcode: error / " + ex.Message);
        }
    }

    private async UniTask OnRequestAccountDeletion()
    {
        try
        {
            var result = await ApiService.RequestAccountDeletionAsync();
            if (result.ResultCode == "requested" || result.ResultCode == "already_pending")
            {
                _deletionRequestId = ParseGuid(result.Raw, "deletion_request_id");
            }
            Report("Request Account Deletion", result);
        }
        catch (Exception ex)
        {
            SetStatus("Request Account Deletion: error / " + ex.Message);
        }
    }

    private async UniTask OnCancelAccountDeletion()
    {
        if (!_deletionRequestId.HasValue)
        {
            SetStatus("Cancel Account Deletion: deletion_request_id 未取得. 先に Request Account Deletion を実行.");
            return;
        }
        try
        {
            var result = await ApiService.CancelAccountDeletionAsync(_deletionRequestId.Value);
            Report("Cancel Account Deletion", result);
        }
        catch (Exception ex)
        {
            SetStatus("Cancel Account Deletion: error / " + ex.Message);
        }
    }

    private static Guid? ParseGuid(JObject raw, string key)
    {
        var v = raw?[key]?.ToString();
        return Guid.TryParse(v, out var g) ? g : (Guid?)null;
    }
}
