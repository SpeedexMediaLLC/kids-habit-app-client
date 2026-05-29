// M4 Step 1: 設定画面の「習慣の管理」(追加 / 編集 / 削除). 大人モード専用.
//
// S0 で作った器 (タイトル + ホームにもどる) に習慣 CRUD を載せる. サービス層 RPC
// (ApiService.AddHabitAsync / UpdateHabitAsync / DeleteHabitAsync) は M1 実装済みで, 本 Step は
// client UI のみ. データ取得は HomePanel と同じ直 SELECT 方式 (family スコープは RLS が保証,
// Phase 1 は小規模なので全件取得して C# 側でフィルタ).
//
// フロー (報告どおり・中継層 GO 済):
//   追加: 対象メンバー (parent/child) を選ぶ → 「定番から選ぶ」or「自由に入力」
//         - 定番  : habit_templates を sort_order 昇順で一覧 (強度を併記). 選択で add_habit を
//                   p_template_id 指定 + p_intensity=null で呼ぶ → サーバが template の
//                   default_intensity を採用する (仕様 v2 §8「定番テンプレ＝強度つき」).
//         - 自由入力: タイトル + 強度 (小/中/大) を入力. 両方入るまで「追加」無効. add_habit を
//                   p_custom_title + p_intensity で呼ぶ (仕様 v2 §8「自由入力は本人が強度を選ぶ」).
//   編集: title + intensity のみ (判断⑤. is_active トグル/並べ替えは出さない). update_habit には
//         現在の is_active をそのまま透過する.
//   削除: 確認ダイアログ → delete_habit.
//
// 結果コードは 3 RPC 共通で added/updated/deleted (成功) / invalid_template / not_found /
//   deletion_pending / not_authorized を分岐 (API_BOUNDARY §1). deletion_pending・not_authorized は
//   S2 同様に簡易文言のみ (本格ルーティングは S4 委譲), 生 JSON は画面に出さず Console のみに残す.
//
// 設定を閉じると AppFlowController.CloseSettings() が HomePanel.Refresh() を呼ぶため, ここでの
//   追加/編集/削除はホームに反映される (完了条件 :687).
//
// レイアウト方針: 既存 HomePanel と同じく ScrollRect は使わず固定領域 + VerticalLayoutGroup.
//   Phase 1 想定件数 (テンプレ 10 件 / メンバーあたり習慣 4〜5 件) で収まる前提.

using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class SettingsPanel : MonoBehaviour
{
    private AppFlowController _appFlow;
    private Font _font;
    private bool _built;
    private bool _loading;   // データ取得中
    private bool _busy;      // RPC 送信中 (二重送信防止)

    private GameObject _memberTabRow;
    private GameObject _habitListContent;
    private Text _statusText;

    private readonly List<MemberModel> _members = new List<MemberModel>();
    private readonly List<HabitModel> _habits = new List<HabitModel>();
    private readonly List<HabitTemplateModel> _templates = new List<HabitTemplateModel>();
    private readonly Dictionary<Guid, Image> _memberTabBg = new Dictionary<Guid, Image>();

    private MemberModel _selectedMember;

    // ---- 現在開いているダイアログ (同時に 1 つだけ) ----
    private GameObject _dialog;
    private CanvasGroup _dialogGroup;
    private Text _dialogMessage;

    // ---- パスコード変更オーバーレイ (M4 S3, 開いている間だけ存在) ----
    private GameObject _passcodeChangeOverlay;

    // ---- 習慣フォーム (自由入力の追加 / 編集で共用) の状態 ----
    private HabitModel _formEditTarget;   // null = 自由入力の追加, 非 null = 編集
    private InputField _formTitleInput;
    private string _formIntensity;        // null = 未選択
    private Button _formSubmitButton;
    private readonly Dictionary<string, Image> _formIntensityBg = new Dictionary<string, Image>();

    private static readonly string[] IntensityCodes = { "small", "medium", "large" };

    private static readonly Color TabOn = new Color(0.25f, 0.55f, 0.85f);
    private static readonly Color TabOff = new Color(0.30f, 0.30f, 0.35f);
    private static readonly Color PickOn = new Color(0.25f, 0.55f, 0.85f);
    private static readonly Color PickOff = new Color(0.30f, 0.30f, 0.35f);

    public void Initialize(AppFlowController appFlow, Font font)
    {
        _appFlow = appFlow;
        _font = font;
        BuildSkeleton();
    }

    // パネルが SetActive(true) されるたびに最新データを読み込む. 生成直後の AddComponent でも
    // 発火するが, その時点では _built=false (Initialize 前) のためスキップする.
    private void OnEnable()
    {
        if (_built)
        {
            CloseDialog();              // 前回のダイアログが残らないよう, 開くたびに初期化する.
            ClosePasscodeChangePanel(); // パスコード変更オーバーレイの残留も掃除する.
            Refresh();
        }
    }

    public void Refresh()
    {
        RefreshAsync().Forget();
    }

    // ---------- データ取得 ----------

    private async UniTask RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        SetStatus("読み込み中...");
        try
        {
            var client = SupabaseService.Client;
            var membersResp = await client.From<MemberModel>().Get();
            var habitsResp = await client.From<HabitModel>().Get();
            var templatesResp = await client.From<HabitTemplateModel>().Get();

            _members.Clear(); _members.AddRange(membersResp?.Models ?? new List<MemberModel>());
            _habits.Clear(); _habits.AddRange(habitsResp?.Models ?? new List<HabitModel>());
            _templates.Clear(); _templates.AddRange(templatesResp?.Models ?? new List<HabitTemplateModel>());

            // parent を先, child を後に安定ソート (HomePanel と同じ).
            _members.Sort((a, b) => RoleRank(a.Role).CompareTo(RoleRank(b.Role)));

            BuildMemberTabs();

            // 再取得時は前回選択メンバーを維持, 無ければ先頭. 既定は決め打ちせず, 選択は
            // 利用者がタブで切り替える (判断③: 追加対象は parent/child セレクタで選ばせる).
            MemberModel keep = null;
            if (_selectedMember != null)
            {
                keep = _members.FirstOrDefault(m => m.Id == _selectedMember.Id);
            }
            SelectMember(keep ?? _members.FirstOrDefault());

            SetStatus(_members.Count == 0 ? "メンバーが見つかりません" : "");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SettingsPanel] refresh failed: {ex.Message}");
            SetStatus("読み込みに失敗しました");
        }
        finally
        {
            _loading = false;
        }
    }

    private static int RoleRank(string role) => role == "parent" ? 0 : 1;

    // ---------- メンバータブ / 習慣一覧 ----------

    private void BuildMemberTabs()
    {
        ClearChildren(_memberTabRow);
        _memberTabBg.Clear();
        foreach (var m in _members)
        {
            var captured = m;
            var go = new GameObject("Tab_" + m.Role);
            go.transform.SetParent(_memberTabRow.transform, false);
            var img = go.AddComponent<Image>();
            img.color = TabOff;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => SelectMember(captured));

            var label = new GameObject("Text");
            label.transform.SetParent(go.transform, false);
            var lr = label.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var t = label.AddComponent<Text>();
            t.text = $"{m.Nickname}\n({(m.Role == "parent" ? "保護者" : "こども")})";
            t.font = _font;
            t.fontSize = 26;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;

            _memberTabBg[m.Id] = img;
        }
    }

    private void SelectMember(MemberModel member)
    {
        _selectedMember = member;
        foreach (var kv in _memberTabBg)
        {
            bool on = _selectedMember != null && kv.Key == _selectedMember.Id;
            kv.Value.color = on ? TabOn : TabOff;
        }
        BuildHabitList();
    }

    private void BuildHabitList()
    {
        ClearChildren(_habitListContent);
        if (_selectedMember == null) return;

        var mine = _habits
            .Where(h => h.MemberId == _selectedMember.Id && h.IsActive)
            .ToList();

        if (mine.Count == 0)
        {
            var empty = CreateLine(_habitListContent, "習慣がまだありません", 26,
                new Color(0.85f, 0.85f, 0.85f));
            var le = empty.gameObject.GetComponent<LayoutElement>();
            if (le != null) le.minHeight = 100;
            return;
        }

        foreach (var h in mine)
        {
            CreateHabitRow(_habitListContent, h);
        }
    }

    // 1 行 = [タイトル（強度）] [編集] [削除]. 設定画面は大人モードなので強度を表示してよい
    // (仕様 v2 §8: 強度を出さないのは子供画面のみ).
    private void CreateHabitRow(GameObject parent, HabitModel habit)
    {
        var captured = habit;
        var row = new GameObject("Habit_" + habit.Id);
        row.transform.SetParent(parent.transform, false);
        var le = row.AddComponent<LayoutElement>();
        le.minHeight = 88;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8;
        hlg.childControlWidth = true;
        hlg.childForceExpandWidth = false;
        hlg.childControlHeight = true;
        hlg.childForceExpandHeight = true;
        hlg.childAlignment = TextAnchor.MiddleLeft;

        // タイトル + 強度 (残り幅を占有)
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(row.transform, false);
        var labelLe = labelGo.AddComponent<LayoutElement>();
        labelLe.flexibleWidth = 1f;
        var labelImg = labelGo.AddComponent<Image>();
        labelImg.color = new Color(1f, 1f, 1f, 0.06f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(labelGo.transform, false);
        var tr = textGo.AddComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(16, 0); tr.offsetMax = new Vector2(-8, 0);
        var t = textGo.AddComponent<Text>();
        t.text = $"{habit.Title}（{IntensityLabel(habit.Intensity)}）";
        t.font = _font;
        t.fontSize = 28;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleLeft;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;

        CreateButton(row, "編集", new Color(0.20f, 0.45f, 0.70f),
            () => ShowHabitFormDialog(captured), 88f, 120f);
        CreateButton(row, "削除", new Color(0.60f, 0.25f, 0.25f),
            () => ShowDeleteConfirmDialog(captured), 88f, 120f);
    }

    // ---------- 追加フロー ----------

    private void OnAddClicked()
    {
        if (_selectedMember == null)
        {
            SetStatus("対象のメンバーを選んでください");
            return;
        }
        ShowAddChooseDialog();
    }

    // 入力方法の選択 (定番 / 自由入力).
    private void ShowAddChooseDialog()
    {
        var box = BeginDialog("習慣を追加");
        CreateLine(box, $"{_selectedMember.Nickname} に追加します", 26, Color.white);
        CreateButton(box, "定番から選ぶ", new Color(0.20f, 0.45f, 0.70f), ShowTemplatePickDialog);
        CreateButton(box, "自由に入力", new Color(0.20f, 0.55f, 0.35f), () => ShowHabitFormDialog(null));
        _dialogMessage = CreateLine(box, "", 24, new Color(1f, 0.9f, 0.6f));
        CreateButton(box, "とじる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);
    }

    // 定番テンプレ一覧. is_active=true を sort_order 昇順. 選択で即追加 (強度はテンプレ既定).
    private void ShowTemplatePickDialog()
    {
        var box = BeginDialog("定番から選ぶ");
        var active = _templates.Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToList();
        if (active.Count == 0)
        {
            CreateLine(box, "テンプレートがありません", 26, Color.white);
        }
        else
        {
            foreach (var tmpl in active)
            {
                var captured = tmpl;
                CreateButton(box, $"{tmpl.Title}（{IntensityLabel(tmpl.DefaultIntensity)}）",
                    new Color(0.22f, 0.30f, 0.42f), () => OnTemplateChosen(captured), 68f);
            }
        }
        _dialogMessage = CreateLine(box, "", 24, new Color(1f, 0.9f, 0.6f));
        CreateButton(box, "もどる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);
    }

    private void OnTemplateChosen(HabitTemplateModel template)
    {
        if (_busy || _selectedMember == null) return;
        // テンプレ選択: p_intensity=null → サーバが template の default_intensity を採用 (仕様 §8).
        RunMutationAsync(
            ApiService.AddHabitAsync(_selectedMember.Id, template.Id, null, null),
            "added", "追加しました").Forget();
    }

    // 自由入力の追加 (existing=null) と編集 (existing=非null) で共用するフォーム.
    private void ShowHabitFormDialog(HabitModel existing)
    {
        // BeginDialog → CloseDialog でフォーム状態が一旦リセットされるため, フォーム状態の初期化は
        // BeginDialog の後に行う (編集時の title/intensity プリフィルが消えないように).
        var box = BeginDialog(existing != null ? "習慣を編集" : "自由に入力");
        _formEditTarget = existing;
        _formIntensity = existing != null ? existing.Intensity : null;
        _formIntensityBg.Clear();

        if (existing == null && _selectedMember != null)
        {
            CreateLine(box, $"{_selectedMember.Nickname} に追加します", 24, new Color(0.85f, 0.85f, 0.85f));
        }

        CreateLine(box, "なまえ", 24, new Color(0.8f, 0.8f, 0.8f));
        _formTitleInput = CreateTextInput(box, "例: はみがき", existing != null ? existing.Title : "", 30);
        _formTitleInput.onValueChanged.AddListener(_ => UpdateFormSubmitState());

        CreateLine(box, "つよさ", 24, new Color(0.8f, 0.8f, 0.8f));
        BuildIntensitySelector(box);

        _dialogMessage = CreateLine(box, "", 24, new Color(1f, 0.9f, 0.6f));

        _formSubmitButton = CreateButton(box, existing != null ? "保存" : "追加",
            new Color(0.20f, 0.55f, 0.35f), OnFormSubmit);
        CreateButton(box, "もどる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);

        UpdateFormSubmitState();
    }

    private void BuildIntensitySelector(GameObject parent)
    {
        var row = new GameObject("IntensityRow");
        row.transform.SetParent(parent.transform, false);
        var le = row.AddComponent<LayoutElement>();
        le.minHeight = 84;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 10;
        hlg.childControlWidth = true;
        hlg.childForceExpandWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandHeight = true;

        foreach (var code in IntensityCodes)
        {
            var captured = code;
            var go = new GameObject("Intensity_" + code);
            go.transform.SetParent(row.transform, false);
            var img = go.AddComponent<Image>();
            img.color = PickOff;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => OnIntensityChosen(captured));

            var tGo = new GameObject("Text");
            tGo.transform.SetParent(go.transform, false);
            var tr = tGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
            var t = tGo.AddComponent<Text>();
            t.text = IntensityLabel(code);
            t.font = _font;
            t.fontSize = 30;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;

            _formIntensityBg[code] = img;
        }
        UpdateIntensityHighlight();
    }

    private void OnIntensityChosen(string code)
    {
        _formIntensity = code;
        UpdateIntensityHighlight();
        UpdateFormSubmitState();
    }

    private void UpdateIntensityHighlight()
    {
        foreach (var kv in _formIntensityBg)
        {
            kv.Value.color = (kv.Key == _formIntensity) ? PickOn : PickOff;
        }
    }

    // 「追加」/「保存」は タイトル非空 + 強度選択済 + 非 busy のときだけ有効.
    private void UpdateFormSubmitState()
    {
        if (_formSubmitButton == null) return;
        bool titleOk = _formTitleInput != null && !string.IsNullOrWhiteSpace(_formTitleInput.text);
        bool intensityOk = !string.IsNullOrEmpty(_formIntensity);
        _formSubmitButton.interactable = titleOk && intensityOk && !_busy;
    }

    private void OnFormSubmit()
    {
        if (_busy) return;
        var title = _formTitleInput != null ? (_formTitleInput.text ?? "").Trim() : "";
        var intensity = _formIntensity;
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(intensity)) return;

        if (_formEditTarget != null)
        {
            // 編集: title + intensity のみ変更. is_active は現値を透過する (判断⑤).
            var target = _formEditTarget;
            RunMutationAsync(
                ApiService.UpdateHabitAsync(target.Id, title, intensity, target.IsActive),
                "updated", "保存しました").Forget();
        }
        else
        {
            if (_selectedMember == null) return;
            // 自由入力の追加: template_id=null, custom_title + intensity を渡す.
            RunMutationAsync(
                ApiService.AddHabitAsync(_selectedMember.Id, null, title, intensity),
                "added", "追加しました").Forget();
        }
    }

    // ---------- 削除フロー ----------

    private void ShowDeleteConfirmDialog(HabitModel habit)
    {
        var captured = habit;
        var box = BeginDialog("習慣を削除");
        CreateLine(box, $"「{habit.Title}」を削除しますか?", 28, Color.white);
        _dialogMessage = CreateLine(box, "", 24, new Color(1f, 0.9f, 0.6f));
        CreateButton(box, "削除する", new Color(0.60f, 0.25f, 0.25f), () =>
        {
            if (_busy) return;
            RunMutationAsync(ApiService.DeleteHabitAsync(captured.Id), "deleted", "削除しました").Forget();
        });
        CreateButton(box, "やめる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);
    }

    // ---------- パスコード (M4 S3) ----------

    // パスコード変更: 専用オーバーレイ (PasscodeChangePanel) を生成する. 変更ロジック・結果分岐は
    // そのパネルが持つ (PasscodeEntry を 2 ステップで再利用). 閉じる時は OnPasscodeChangeClosed.
    private void OnChangePasscodeClicked()
    {
        CloseDialog();                       // S1 系ダイアログが開いていれば閉じる
        if (_passcodeChangeOverlay != null) return;   // 二重生成防止

        var overlay = new GameObject("PasscodeChangeOverlay");
        overlay.transform.SetParent(transform, false); // SettingsPanel 直下 = 最後の子 = 最前面
        var rect = overlay.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        var bg = overlay.AddComponent<Image>();
        bg.color = new Color(0.10f, 0.12f, 0.20f, 1f);  // 設定画面と同系の不透明背景で下を覆う
        _passcodeChangeOverlay = overlay;

        var panel = overlay.AddComponent<PasscodeChangePanel>();
        panel.Initialize(_font, OnPasscodeChangeClosed);
    }

    // changed=true のときだけ完了メッセージを設定ステータスに出す.
    private void OnPasscodeChangeClosed(bool changed)
    {
        ClosePasscodeChangePanel();
        if (changed)
        {
            SetStatus("パスコードを変更しました");
        }
    }

    private void ClosePasscodeChangePanel()
    {
        if (_passcodeChangeOverlay != null)
        {
            Destroy(_passcodeChangeOverlay);
            _passcodeChangeOverlay = null;
        }
    }

    // パスコードを忘れた場合 (再ログイン導線・ナビのみ = 判断①). 確認後にログアウトしてログイン画面へ.
    // 新パスコードのリセット書込みは作らない (server に reset RPC 不在 = M4 残ゲート).
    private void OnForgotPasscodeClicked()
    {
        var box = BeginDialog("パスコードを忘れた場合");
        CreateLine(box, "一度ログアウトして、メールとパスワードで\n再ログインします。よろしいですか?",
            26, Color.white);
        _dialogMessage = CreateLine(box, "", 24, new Color(1f, 0.9f, 0.6f));
        CreateButton(box, "ログアウトする", new Color(0.60f, 0.25f, 0.25f), OnConfirmLogout);
        CreateButton(box, "やめる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);
    }

    private void OnConfirmLogout()
    {
        CloseDialog();
        if (_appFlow != null)
        {
            _appFlow.LogoutToLogin();
        }
    }

    // ---------- アカウント削除 (M4 S4) ----------

    // 削除申請の確認ダイアログ. 申請すると families.deletion_pending=true + 14 日後予約 (それまで取り消し可).
    private void OnDeleteAccountClicked()
    {
        var box = BeginDialog("アカウントを削除");
        CreateLine(box, "アカウントとすべてのデータを削除します。\n申請から14日後に完全に削除されます。\nそれまでは取り消せます。",
            26, Color.white);
        _dialogMessage = CreateLine(box, "", 24, new Color(1f, 0.9f, 0.6f));
        CreateButton(box, "削除を申請する", new Color(0.60f, 0.25f, 0.25f), OnConfirmDeleteAccount);
        CreateButton(box, "やめる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);
    }

    private void OnConfirmDeleteAccount()
    {
        if (_busy) return;
        RunDeletionRequestAsync().Forget();
    }

    // request_account_deletion → 成功 (requested / already_pending) は削除予約中画面へ遷移する.
    // 習慣 CRUD の RunMutationAsync とは成功後の挙動が異なる (一覧更新でなく画面遷移) ため別メソッド.
    // requested 応答の deletion_request_id / scheduled_delete_at は使わない (予約中画面が自前 SELECT で
    // id と残り日数を取得する). deletion_pending は本 RPC は返さない (0014: 自前で already_pending を返す).
    private async UniTask RunDeletionRequestAsync()
    {
        if (_busy) return;
        _busy = true;
        SetDialogInteractable(false);
        SetDialogMessage("申請中...");
        try
        {
            var result = await ApiService.RequestAccountDeletionAsync();
            var rc = result.ResultCode;
            if (rc == "requested" || rc == "already_pending")
            {
                // 設定/ダイアログを閉じ削除予約中画面へ (起動時もそこへルーティングされる).
                CloseDialog();
                if (_appFlow != null) _appFlow.GoToDeletionReserved();
                return;
            }

            // 以降はダイアログを開いたまま固定文言で再試行させる. 生応答は Console のみ.
            string msg;
            switch (rc)
            {
                case "not_authorized":
                    msg = "この操作は保護者のみ可能です";
                    break;
                default:
                    Debug.LogWarning($"[SettingsPanel] unexpected result_code='{rc}' (request_account_deletion)");
                    msg = "通信エラーが発生しました。もう一度お試しください";
                    break;
            }
            SetDialogMessage(msg);
        }
        catch (Exception ex)
        {
            // 例外メッセージ (サーバ応答の生 JSON 等) は Console のみ. 画面は固定文言.
            Debug.LogWarning($"[SettingsPanel] deletion request failed: {ex}");
            SetDialogMessage("通信エラーが発生しました。もう一度お試しください");
        }
        finally
        {
            // requested / already_pending は return 済 (画面遷移済). それ以外は再試行できるよう戻す.
            if (_busy)
            {
                _busy = false;
                SetDialogInteractable(true);
            }
        }
    }

    // ---------- RPC 実行 + 結果コード分岐 (3 RPC 共通) ----------

    private async UniTask RunMutationAsync(UniTask<ApiService.RpcResult> call,
                                           string successCode, string successToast)
    {
        if (_busy) return;
        _busy = true;
        SetDialogInteractable(false);
        SetDialogMessage("送信中...");
        try
        {
            var result = await call;
            var rc = result.ResultCode;

            if (rc == successCode)
            {
                CloseDialog();
                SetStatus(successToast);
                Refresh();           // 一覧を最新化 (設定内). ホームは CloseSettings で再取得.
                return;
            }
            if (rc == "not_found")
            {
                // 他端末で既に削除済み等. ダイアログを閉じ一覧を最新化する.
                CloseDialog();
                SetStatus("対象が見つかりませんでした");
                Refresh();
                return;
            }

            // 以降はダイアログを開いたまま, 固定文言で再試行/修正させる. 生応答は Console のみ.
            string msg;
            switch (rc)
            {
                case "invalid_template":
                    msg = "入力内容を確認してください";
                    break;
                case "deletion_pending":
                    // 本格ルーティング (ログアウト + 削除予約画面) は S4 委譲. ここは簡易文言のみ.
                    msg = "アカウント削除申請中のため変更できません";
                    break;
                case "not_authorized":
                    // 本格処理 (再ログイン要求) は S4/別途委譲. ここは簡易文言のみ.
                    msg = "この操作の権限がありません。ログインし直してください";
                    break;
                default:
                    Debug.LogWarning($"[SettingsPanel] unexpected result_code='{rc}'");
                    msg = "通信エラーが発生しました。もう一度お試しください";
                    break;
            }
            SetDialogMessage(msg);
        }
        catch (Exception ex)
        {
            // 例外メッセージ (サーバ応答の生 JSON 等) は Console のみ. 画面は固定文言.
            Debug.LogWarning($"[SettingsPanel] mutation failed: {ex}");
            SetDialogMessage("通信エラーが発生しました。もう一度お試しください");
        }
        finally
        {
            // 成功 / not_found は return 済 (ダイアログは閉じている). それ以外は再試行できるよう戻す.
            if (_busy)
            {
                _busy = false;
                SetDialogInteractable(true);
                UpdateFormSubmitState();
            }
        }
    }

    // ---------- ダイアログ (全画面オーバーレイ, 同時に 1 つ) ----------

    private GameObject BeginDialog(string title)
    {
        CloseDialog();

        var overlay = new GameObject("Dialog");
        overlay.transform.SetParent(transform, false);
        var orect = overlay.AddComponent<RectTransform>();
        orect.anchorMin = Vector2.zero; orect.anchorMax = Vector2.one;
        orect.offsetMin = Vector2.zero; orect.offsetMax = Vector2.zero;
        var obg = overlay.AddComponent<Image>();
        obg.color = new Color(0f, 0f, 0f, 0.78f);
        _dialog = overlay;
        _dialogGroup = overlay.AddComponent<CanvasGroup>();

        var box = new GameObject("Box");
        box.transform.SetParent(overlay.transform, false);
        var brect = box.AddComponent<RectTransform>();
        brect.anchorMin = new Vector2(0.06f, 0.05f);
        brect.anchorMax = new Vector2(0.94f, 0.95f);
        brect.offsetMin = Vector2.zero; brect.offsetMax = Vector2.zero;
        var bbg = box.AddComponent<Image>();
        bbg.color = new Color(0.13f, 0.16f, 0.24f, 1f);
        var vlg = box.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 12;
        vlg.padding = new RectOffset(26, 26, 26, 26);
        vlg.childControlWidth = true; vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperCenter;

        CreateSubheader(box, title);
        return box;
    }

    private void SetDialogInteractable(bool v)
    {
        if (_dialogGroup != null) _dialogGroup.interactable = v;
    }

    private void SetDialogMessage(string m)
    {
        if (_dialogMessage != null) _dialogMessage.text = m ?? "";
    }

    private void CloseDialog()
    {
        if (_dialog != null)
        {
            Destroy(_dialog);
            _dialog = null;
        }
        _dialogGroup = null;
        _dialogMessage = null;
        _formTitleInput = null;
        _formSubmitButton = null;
        _formEditTarget = null;
        _formIntensity = null;
        _formIntensityBg.Clear();
    }

    private void OnBack()
    {
        CloseDialog();
        if (_appFlow != null)
        {
            _appFlow.CloseSettings();
        }
    }

    private static string IntensityLabel(string code)
    {
        switch (code)
        {
            case "small": return "小";
            case "medium": return "中";
            case "large": return "大";
            default: return code ?? "";
        }
    }

    // ---------- 骨格 (1 回だけ) ----------

    private void BuildSkeleton()
    {
        if (_built) return;
        _built = true;

        var column = CreateColumn();

        CreateTitle(column, "設定");
        CreateSubheader(column, "習慣の管理");
        CreateLine(column, "だれの習慣を管理するか選んでください", 24, new Color(0.8f, 0.8f, 0.8f));

        // 対象メンバー (parent/child) タブ
        _memberTabRow = new GameObject("MemberTabRow");
        _memberTabRow.transform.SetParent(column.transform, false);
        var tabLe = _memberTabRow.AddComponent<LayoutElement>();
        tabLe.minHeight = 84;
        var tabHlg = _memberTabRow.AddComponent<HorizontalLayoutGroup>();
        tabHlg.spacing = 12;
        tabHlg.childControlWidth = true;
        tabHlg.childForceExpandWidth = true;
        tabHlg.childControlHeight = true;
        tabHlg.childForceExpandHeight = true;

        // 習慣一覧 (HomePanel と同じ固定領域 + 縦並び. Phase 1 は数件想定)
        var listContainer = new GameObject("HabitList");
        listContainer.transform.SetParent(column.transform, false);
        var listLe = listContainer.AddComponent<LayoutElement>();
        listLe.minHeight = 360;
        listLe.flexibleHeight = 1;
        var listImg = listContainer.AddComponent<Image>();
        listImg.color = new Color(1f, 1f, 1f, 0.05f);
        var listVlg = listContainer.AddComponent<VerticalLayoutGroup>();
        listVlg.spacing = 8;
        listVlg.padding = new RectOffset(12, 12, 12, 12);
        listVlg.childControlWidth = true;
        listVlg.childForceExpandWidth = true;
        listVlg.childControlHeight = false;
        listVlg.childForceExpandHeight = false;
        _habitListContent = listContainer;

        // 追加導線
        CreateButton(column, "習慣を追加", new Color(0.20f, 0.55f, 0.35f), OnAddClicked);

        // パスコード (M4 S3): 変更 (現+新) と, 忘れた場合の再ログイン導線 (ナビのみ).
        CreateSubheader(column, "パスコード");
        CreateButton(column, "パスコードを変更", new Color(0.20f, 0.45f, 0.70f), OnChangePasscodeClicked);
        CreateButton(column, "パスコードを忘れた場合（再ログイン）",
            new Color(0.45f, 0.45f, 0.50f), OnForgotPasscodeClicked);

        // アカウント (M4 S4): 家族アカウントの削除申請. 申請後は 14 日猶予で取り消し可能 (計画 :683,:687).
        CreateSubheader(column, "アカウント");
        CreateButton(column, "アカウントを削除", new Color(0.60f, 0.25f, 0.25f), OnDeleteAccountClicked);

        // ステータス
        _statusText = CreateLine(column, "", 24, new Color(1f, 0.9f, 0.6f));

        // 戻る
        CreateButton(column, "ホームにもどる", new Color(0.45f, 0.45f, 0.50f), OnBack);
    }

    // ---------- UI helpers (手続き生成, HomePanel / S0 準拠) ----------

    private GameObject CreateColumn()
    {
        var col = new GameObject("Column");
        col.transform.SetParent(transform, false);
        var rect = col.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.05f, 0.05f);
        rect.anchorMax = new Vector2(0.95f, 0.96f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var vlg = col.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 14;
        vlg.childControlWidth = true;
        vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperCenter;
        return col;
    }

    private Text CreateTitle(GameObject parent, string title)
    {
        var go = new GameObject("Title");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 76;
        var text = go.AddComponent<Text>();
        text.text = title;
        text.font = _font;
        text.fontSize = 48;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        return text;
    }

    private Text CreateSubheader(GameObject parent, string title)
    {
        var go = new GameObject("Subheader");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 60;
        var text = go.AddComponent<Text>();
        text.text = title;
        text.font = _font;
        text.fontSize = 36;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        return text;
    }

    private Text CreateLine(GameObject parent, string body, int size, Color color)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 50;
        var text = go.AddComponent<Text>();
        text.text = body;
        text.font = _font;
        text.fontSize = size;
        text.color = color;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    // minHeight: 行高さ. fixedWidth>0 のとき固定幅ボタン (一覧行の編集/削除用).
    private Button CreateButton(GameObject parent, string label, Color color,
                                UnityEngine.Events.UnityAction onClick,
                                float minHeight = 84f, float fixedWidth = 0f)
    {
        var go = new GameObject("Button_" + label);
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = minHeight;
        if (fixedWidth > 0f)
        {
            le.minWidth = fixedWidth;
            le.preferredWidth = fixedWidth;
            le.flexibleWidth = 0f;
        }
        var img = go.AddComponent<Image>();
        img.color = color;
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
        text.fontSize = 30;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        return btn;
    }

    // 1 行テキスト入力 (PasscodeEntry の InputField 生成に準拠. ContentType は Standard).
    private InputField CreateTextInput(GameObject parent, string placeholder, string initial, int charLimit)
    {
        var go = new GameObject("Input");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 76;
        var img = go.AddComponent<Image>();
        img.color = Color.white;
        var input = go.AddComponent<InputField>();

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16, 0);
        textRect.offsetMax = new Vector2(-16, 0);
        var text = textGO.AddComponent<Text>();
        text.font = _font;
        text.fontSize = 32;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        var phGO = new GameObject("Placeholder");
        phGO.transform.SetParent(go.transform, false);
        var phRect = phGO.AddComponent<RectTransform>();
        phRect.anchorMin = Vector2.zero;
        phRect.anchorMax = Vector2.one;
        phRect.offsetMin = new Vector2(16, 0);
        phRect.offsetMax = new Vector2(-16, 0);
        var ph = phGO.AddComponent<Text>();
        ph.font = _font;
        ph.fontSize = 28;
        ph.color = new Color(0.5f, 0.5f, 0.5f);
        ph.alignment = TextAnchor.MiddleLeft;
        ph.text = placeholder;

        input.textComponent = text;
        input.placeholder = ph;
        input.contentType = InputField.ContentType.Standard;
        input.lineType = InputField.LineType.SingleLine;
        input.characterLimit = charLimit;
        input.text = initial ?? "";
        return input;
    }

    private void SetStatus(string s)
    {
        if (_statusText != null) _statusText.text = s ?? "";
        if (!string.IsNullOrEmpty(s)) Debug.Log("[SettingsPanel] " + s);
    }

    private static void ClearChildren(GameObject parent)
    {
        if (parent == null) return;
        var toDelete = new List<GameObject>();
        foreach (Transform child in parent.transform)
        {
            toDelete.Add(child.gameObject);
        }
        foreach (var go in toDelete)
        {
            Destroy(go);
        }
    }
}
