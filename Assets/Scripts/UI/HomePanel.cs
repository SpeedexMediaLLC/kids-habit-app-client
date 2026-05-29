// M3 Step 4-5: ホーム画面 (大人モード).
//
// AppFlowController が生成した HomePanel GameObject にアタッチされ, Initialize() で骨格を作り,
// Refresh() で members/creatures/habits/habit_logs を取得して表示する (手続き生成, TMP 非依存).
//   Step 4: parent/child メンバー切替タブ + 選択メンバーの creature を CreatureDisplay.SetStage で表示
//   Step 5: 選択メンバーの active habit 一覧 (0 件は空状態) + サマリー
//           (合計回数 = 生涯 habit_logs 件数 / 最高記録 = 1 日の最多達成回数, 自己ベスト)
//
// データ取得方針: family スコープは RLS が保証. Phase 1 は 1 家族 2 メンバーで小規模のため
//   family 全件を取得し C# 側で member フィルタ (計画「オンザフライ集計, Phase 1 は性能問題なし」).
//
// スコープ: Step 4-5 のみ. habit 選択は状態保持まで (「子供モードに渡す」は Step 6).

using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class HomePanel : MonoBehaviour
{
    private AppFlowController _appFlow;
    private Font _font;

    private GameObject _tabRow;
    private GameObject _habitListContent;
    private Text _summaryText;
    private Text _statusText;

    private readonly List<MemberModel> _members = new List<MemberModel>();
    private readonly List<CreatureModel> _creatures = new List<CreatureModel>();
    private readonly List<HabitModel> _habits = new List<HabitModel>();
    private readonly List<HabitLogModel> _logs = new List<HabitLogModel>();
    private readonly Dictionary<Guid, Image> _tabBg = new Dictionary<Guid, Image>();
    private readonly Dictionary<Guid, Image> _habitRowBg = new Dictionary<Guid, Image>();

    private MemberModel _selectedMember;
    private HabitModel _selectedHabit;
    private CreatureDisplay _creatureDisplay;
    private Button _passButton;
    private GameObject _confirmDialog;

    private bool _built;
    private bool _loading;

    private static readonly Color TabOn = new Color(0.25f, 0.55f, 0.85f);
    private static readonly Color TabOff = new Color(0.30f, 0.30f, 0.35f);
    private static readonly Color RowOn = new Color(0.30f, 0.50f, 0.40f);
    private static readonly Color RowOff = new Color(1f, 1f, 1f, 0.10f);

    public void Initialize(AppFlowController appFlow, Font font)
    {
        _appFlow = appFlow;
        _font = font;
        BuildSkeleton();
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
            var creaturesResp = await client.From<CreatureModel>().Get();
            var habitsResp = await client.From<HabitModel>().Get();
            var logsResp = await client.From<HabitLogModel>().Get();

            _members.Clear(); _members.AddRange(membersResp?.Models ?? new List<MemberModel>());
            _creatures.Clear(); _creatures.AddRange(creaturesResp?.Models ?? new List<CreatureModel>());
            _habits.Clear(); _habits.AddRange(habitsResp?.Models ?? new List<HabitModel>());
            _logs.Clear(); _logs.AddRange(logsResp?.Models ?? new List<HabitLogModel>());

            // parent を先, child を後に安定ソート
            _members.Sort((a, b) => RoleRank(a.Role).CompareTo(RoleRank(b.Role)));

            BuildTabs();

            // 再 Refresh 時は前回選択メンバーを維持, 無ければ先頭
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
            Debug.LogWarning($"[HomePanel] refresh failed: {ex.Message}");
            SetStatus("読み込みに失敗しました: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private static int RoleRank(string role) => role == "parent" ? 0 : 1;

    // ---------- 選択と表示更新 ----------

    private void SelectMember(MemberModel member)
    {
        _selectedMember = member;
        _selectedHabit = null;
        UpdateTabHighlight();
        UpdateCreature();
        BuildHabitList();
        UpdateSummary();
        UpdatePassButton();
    }

    private void UpdateTabHighlight()
    {
        foreach (var kv in _tabBg)
        {
            bool on = _selectedMember != null && kv.Key == _selectedMember.Id;
            kv.Value.color = on ? TabOn : TabOff;
        }
    }

    private void UpdateCreature()
    {
        if (_selectedMember == null) return;
        if (_creatureDisplay == null)
        {
            _creatureDisplay = FindFirstObjectByType<CreatureDisplay>();
        }
        if (_creatureDisplay == null)
        {
            Debug.LogWarning("[HomePanel] CreatureDisplay がシーンに見つからない (CreatureRoot 確認)");
            return;
        }
        var creature = _creatures.FirstOrDefault(c => c.MemberId == _selectedMember.Id);
        if (creature != null && !string.IsNullOrEmpty(creature.Stage))
        {
            // SetStage は同 stage では no-op (別メンバーでも同段階なら見た目は同一 = 仕様どおり)
            _creatureDisplay.SetStage(creature.Stage);
        }
    }

    private void UpdateSummary()
    {
        if (_summaryText == null) return;
        if (_selectedMember == null)
        {
            _summaryText.text = "";
            return;
        }
        var mine = _logs.Where(l => l.MemberId == _selectedMember.Id).ToList();
        int total = mine.Count;
        // 最高記録 = 1 日の最多達成回数 (自己ベスト・下がらない, 連続日数ではない).
        // created_at(UTC) を JST 日付にしてグループ化し最大件数.
        int best = mine
            .GroupBy(l => l.CreatedAt.ToUniversalTime().AddHours(9).Date)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();
        _summaryText.text = $"合計回数: {total} 回    最高記録: {best} 回/日";
    }

    // ---------- 骨格 (1 回だけ) ----------

    private void BuildSkeleton()
    {
        if (_built) return;
        _built = true;

        var root = new GameObject("HomeRoot");
        root.transform.SetParent(transform, false);
        var rootRect = root.AddComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.04f, 0.04f);
        rootRect.anchorMax = new Vector2(0.96f, 0.96f);
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        var vlg = root.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 14;
        vlg.childControlWidth = true;
        vlg.childForceExpandWidth = true;
        vlg.childControlHeight = false;
        vlg.childForceExpandHeight = false;

        CreateHeader(root, "ホーム");

        // メンバー切替タブ行
        _tabRow = new GameObject("TabRow");
        _tabRow.transform.SetParent(root.transform, false);
        var tabLe = _tabRow.AddComponent<LayoutElement>();
        tabLe.minHeight = 84;
        var tabHlg = _tabRow.AddComponent<HorizontalLayoutGroup>();
        tabHlg.spacing = 12;
        tabHlg.childControlWidth = true;
        tabHlg.childForceExpandWidth = true;
        tabHlg.childControlHeight = true;
        tabHlg.childForceExpandHeight = true;

        // habit 一覧 (簡易: 固定領域 + 縦並び. Phase 1 は数件想定)
        var listLabel = CreateHeader(root, "やること");
        listLabel.fontSize = 28;

        var listContainer = new GameObject("HabitList");
        listContainer.transform.SetParent(root.transform, false);
        var listLe = listContainer.AddComponent<LayoutElement>();
        listLe.minHeight = 520;
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

        // サマリー
        _summaryText = CreateLine(root, "", 30, new Color(1f, 0.95f, 0.75f));
        var sumLe = _summaryText.gameObject.GetComponent<LayoutElement>();
        if (sumLe != null) sumLe.minHeight = 70;

        // 「子供モードに渡す」(habit 選択時のみ有効, Step 6)
        _passButton = CreateButton(root, "このやることを こどもにわたす",
            new Color(0.85f, 0.45f, 0.20f), OnPassClicked);
        _passButton.interactable = false;

        // ステータス
        _statusText = CreateLine(root, "", 24, new Color(1f, 0.9f, 0.6f));
    }

    // ---------- タブ / habit 行の動的生成 ----------

    private void BuildTabs()
    {
        ClearChildren(_tabRow);
        _tabBg.Clear();
        foreach (var m in _members)
        {
            var captured = m;
            var go = new GameObject("Tab_" + m.Role);
            go.transform.SetParent(_tabRow.transform, false);
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

            _tabBg[m.Id] = img;
        }
    }

    private void BuildHabitList()
    {
        ClearChildren(_habitListContent);
        _habitRowBg.Clear();
        if (_selectedMember == null) return;

        var mine = _habits
            .Where(h => h.MemberId == _selectedMember.Id && h.IsActive)
            .ToList();

        if (mine.Count == 0)
        {
            var empty = CreateLine(_habitListContent,
                "やることがまだありません\n(習慣の追加は設定画面 = M4 で対応)", 26,
                new Color(0.85f, 0.85f, 0.85f));
            empty.alignment = TextAnchor.MiddleCenter;
            var le = empty.gameObject.GetComponent<LayoutElement>();
            if (le != null) le.minHeight = 120;
            return;
        }

        foreach (var h in mine)
        {
            CreateHabitRow(_habitListContent, h);
        }
    }

    private void CreateHabitRow(GameObject parent, HabitModel habit)
    {
        var captured = habit;
        var go = new GameObject("Habit_" + habit.Id);
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 84;
        var img = go.AddComponent<Image>();
        img.color = RowOff;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() => OnSelectHabit(captured));

        var label = new GameObject("Text");
        label.transform.SetParent(go.transform, false);
        var lr = label.AddComponent<RectTransform>();
        lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
        lr.offsetMin = new Vector2(20, 0); lr.offsetMax = new Vector2(-20, 0);
        var t = label.AddComponent<Text>();
        t.text = habit.Title;
        t.font = _font;
        t.fontSize = 30;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleLeft;

        _habitRowBg[habit.Id] = img;
    }

    private void OnSelectHabit(HabitModel habit)
    {
        _selectedHabit = habit;
        foreach (var kv in _habitRowBg)
        {
            kv.Value.color = (kv.Key == habit.Id) ? RowOn : RowOff;
        }
        Debug.Log($"[HomePanel] habit selected: {habit.Title} ({habit.Id})");
        UpdatePassButton();
    }

    // ---------- 子供モードに渡す (Step 6) ----------

    private void UpdatePassButton()
    {
        if (_passButton != null)
        {
            // 子供にわたすのは child メンバーの habit のみ (要確認① 設定さん判断 2026-05-29).
            // parent タブ選択時は無効. 併用条件: child タブ かつ habit 1 件選択中.
            bool isChild = _selectedMember != null && _selectedMember.Role == "child";
            _passButton.interactable = (isChild && _selectedHabit != null);
        }
    }

    private void OnPassClicked()
    {
        if (_selectedMember == null || _selectedHabit == null)
        {
            return;
        }
        ShowConfirmDialog();
    }

    private void ShowConfirmDialog()
    {
        CloseDialog();

        // 全画面オーバーレイ (HomePanel 直下 = 同 Canvas 内で最後の子 = 最前面).
        var overlay = new GameObject("ConfirmDialog");
        overlay.transform.SetParent(transform, false);
        var orect = overlay.AddComponent<RectTransform>();
        orect.anchorMin = Vector2.zero; orect.anchorMax = Vector2.one;
        orect.offsetMin = Vector2.zero; orect.offsetMax = Vector2.zero;
        var obg = overlay.AddComponent<Image>();
        obg.color = new Color(0f, 0f, 0f, 0.75f);
        _confirmDialog = overlay;

        var box = new GameObject("Box");
        box.transform.SetParent(overlay.transform, false);
        var brect = box.AddComponent<RectTransform>();
        brect.anchorMin = new Vector2(0.1f, 0.34f);
        brect.anchorMax = new Vector2(0.9f, 0.66f);
        brect.offsetMin = Vector2.zero; brect.offsetMax = Vector2.zero;
        var bbg = box.AddComponent<Image>();
        bbg.color = new Color(0.15f, 0.18f, 0.25f, 1f);
        var vlg = box.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 18;
        vlg.padding = new RectOffset(30, 30, 30, 30);
        vlg.childControlWidth = true; vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.MiddleCenter;

        var msg = CreateLine(box, $"「{_selectedHabit.Title}」を\nこどもにわたしますか?", 30, Color.white);
        var msgLe = msg.gameObject.GetComponent<LayoutElement>();
        if (msgLe != null) msgLe.minHeight = 150;

        CreateButton(box, "わたす", new Color(0.20f, 0.55f, 0.35f), DoPass);
        CreateButton(box, "やめる", new Color(0.45f, 0.45f, 0.50f), CloseDialog);
    }

    private void DoPass()
    {
        CloseDialog();
        if (_selectedMember == null || _selectedHabit == null)
        {
            return;
        }
        // M2 の巨大ボタン (HabitButton) に選択 member/habit を注入 (Inspector 固定値を上書き).
        var habitButton = FindFirstObjectByType<HabitButton>();
        if (habitButton == null)
        {
            SetStatus("子供モードのボタンが見つかりません (HabitButton)");
            return;
        }
        habitButton.SetTarget(_selectedMember.Id.ToString(), _selectedHabit.Id.ToString());

        var gs = GameStateService.Instance;
        if (gs == null)
        {
            SetStatus("GameStateService が見つかりません");
            return;
        }
        // SwitchToChild → ModeChanged → AppFlowController が Home パネルを隠し M2 キャラ画面を表示.
        gs.SwitchToChild();
        Debug.Log($"[HomePanel] pass to child: member={_selectedMember.Id} habit={_selectedHabit.Id}");
    }

    private void CloseDialog()
    {
        if (_confirmDialog != null)
        {
            Destroy(_confirmDialog);
            _confirmDialog = null;
        }
    }

    // ---------- UI helpers ----------

    private Text CreateHeader(GameObject parent, string title)
    {
        var go = new GameObject("Header");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 70;
        var t = go.AddComponent<Text>();
        t.text = title;
        t.font = _font;
        t.fontSize = 40;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        return t;
    }

    private Button CreateButton(GameObject parent, string label, Color color,
                                UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button_" + label);
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 96;
        var img = go.AddComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var tr = textGO.AddComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
        var t = textGO.AddComponent<Text>();
        t.text = label;
        t.font = _font;
        t.fontSize = 30;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        return btn;
    }

    private Text CreateLine(GameObject parent, string text, int size, Color color)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 50;
        var t = go.AddComponent<Text>();
        t.text = text;
        t.font = _font;
        t.fontSize = size;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    private void SetStatus(string s)
    {
        if (_statusText != null) _statusText.text = s;
        if (!string.IsNullOrEmpty(s)) Debug.Log("[HomePanel] " + s);
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
