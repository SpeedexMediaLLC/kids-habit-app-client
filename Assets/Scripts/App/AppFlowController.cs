// AppFlowController: M3 Step 1 — 起動時ルーティング骨格.
//
// 役割: MainScene 起動時に Supabase 初期化を待ち, セッション有無 + members 件数で
//   {Login / Onboarding / Home} の 3 状態に分岐し, 対応する空パネルを表示する.
//   本 Step ではパネル中身は分岐確認用ラベルのみ (実 UI は Step 2 以降).
//
// 配置方式 (重要・要確認): Unity Scene YAML の手編集による破損リスク (CLAUDE.md 既知リスク) を
//   避けるため, MainScene.unity は編集せず, 既存 SupabaseService と同じ
//   [RuntimeInitializeOnLoadMethod] で MainScene 限定に自己ブートストラップする.
//   → Play 時に "AppFlow" GameObject + Canvas + 3 パネルが MainScene 階層へ生成される.
//   設定さんが Editor で GameObject を手配置する方式を希望する場合は, この Bootstrap を外し
//   通常の MonoBehaviour として MainScene にアタッチする形へ切替可能 (報告書参照).
//
// UI 生成: M1Tester と同じ手続き生成方式 (レガシー UnityEngine.UI, TMP 非依存).
// M2 のキャラ画面要素 (CreatureDisplay / HabitButton / GameStateService 等) は一切触らず維持する.

using System;
using Cysharp.Threading.Tasks;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// members 件数判定用の最小 POCO (Step 1 専用, id のみ).
// Step 4 で Scripts/Models/ に正式な MemberModel を作る際に置き換える想定.
[Table("members")]
public class AppFlowMemberRow : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }
}

public class AppFlowController : MonoBehaviour
{
    public enum AppScreen { Login, Onboarding, Home }

    private const string MainSceneName = "MainScene";

    // 多重生成防止 (Play 中に再入しないためのガード).
    private static bool _bootstrapped;

    private Font _font;
    private GameObject _loginPanel;
    private GameObject _onboardingPanel;
    private GameObject _homePanel;
    private Text _statusText;

    // MainScene 起動後に自身を生成する. TestM1Scene / SampleScene 等では起動しない
    // (M1Tester と衝突させない).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }
        var activeScene = SceneManager.GetActiveScene().name;
        if (activeScene != MainSceneName)
        {
            Debug.Log($"[AppFlowController] Bootstrap skipped (active scene='{activeScene}', not '{MainSceneName}')");
            return;
        }
        _bootstrapped = true;
        var go = new GameObject("AppFlow");
        go.AddComponent<AppFlowController>();
        Debug.Log("[AppFlowController] Bootstrap: AppFlow GameObject created in MainScene");
    }

    private void Awake()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null)
        {
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        EnsureEventSystem();
        BuildUI();
    }

    private void OnDestroy()
    {
        // 次回 Play で再生成できるようにガードを解除.
        _bootstrapped = false;
    }

    private void Start()
    {
        RouteAsync().Forget();
    }

    // ---------- ルーティング ----------

    private async UniTask RouteAsync()
    {
        SetStatus("起動中: Supabase 初期化待ち...");
        try
        {
            await SupabaseService.InitializeAsync();
        }
        catch (Exception ex)
        {
            // 初期化失敗時は安全側として Login を表示 (再ログインで復帰可能).
            Debug.LogError($"[AppFlowController] Supabase init failed; routing to Login. {ex}");
            Show(AppScreen.Login, $"初期化失敗のため Login: {ex.Message}");
            return;
        }

        var screen = await DecideScreenAsync();
        Show(screen, null);
    }

    private async UniTask<AppScreen> DecideScreenAsync()
    {
        // ① セッション無 → Login
        var session = SupabaseService.Client?.Auth?.CurrentSession;
        bool hasSession = session != null
            && !string.IsNullOrEmpty(session.AccessToken)
            && session.User != null;
        if (!hasSession)
        {
            Debug.Log("[AppFlowController] route: no session -> Login");
            return AppScreen.Login;
        }

        // ② セッション有 → members 件数で分岐 (RLS で自家族 + deletion_pending=false にスコープ)
        //    1 件以上 = 家族あり → Home / 0 件 = 家族なし → Onboarding
        try
        {
            var resp = await SupabaseService.Client.From<AppFlowMemberRow>().Get();
            int count = resp?.Models?.Count ?? 0;
            var dst = count > 0 ? AppScreen.Home : AppScreen.Onboarding;
            Debug.Log($"[AppFlowController] route: session ok, members count={count} -> {dst}");
            return dst;
        }
        catch (Exception ex)
        {
            // members 取得失敗 (通信不調等) は安全側として Login を表示.
            // 要確認: 既存家族ユーザーを一時的に Login へ戻す挙動。堅牢なリトライは後続 Step で検討。
            Debug.LogError($"[AppFlowController] members query failed; routing to Login. {ex}");
            return AppScreen.Login;
        }
    }

    // ---------- 表示制御 ----------

    private void Show(AppScreen screen, string overrideMessage)
    {
        if (_loginPanel != null) _loginPanel.SetActive(screen == AppScreen.Login);
        if (_onboardingPanel != null) _onboardingPanel.SetActive(screen == AppScreen.Onboarding);
        if (_homePanel != null) _homePanel.SetActive(screen == AppScreen.Home);

        string msg = overrideMessage ?? $"分岐結果: {screen}";
        SetStatus(msg);
        Debug.Log($"[AppFlowController] Show: {screen} / {msg}");
    }

    // ---------- LoginPanel / OnboardingPanel から呼ばれる連携 API ----------

    // 「新規作成」導線 (Login → Onboarding).
    public void ShowOnboarding()
    {
        Show(AppScreen.Onboarding, "新規家族作成");
    }

    // 「ログインに戻る」導線 (Onboarding → Login). ShowOnboarding と対.
    public void ShowLogin()
    {
        Show(AppScreen.Login, "ログイン");
    }

    // ログイン / オンボ成功後の再ルーティング (家族有→Home / 無→Onboarding).
    // InitializeAsync は再 await しない (DecideScreenAsync は初期化済み Client を直接見る).
    public void Reroute()
    {
        RerouteAsync().Forget();
    }

    private async UniTask RerouteAsync()
    {
        SetStatus("再ルーティング中...");
        var screen = await DecideScreenAsync();
        Show(screen, null);
    }

    private void SetStatus(string s)
    {
        if (_statusText != null)
        {
            _statusText.text = $"[AppFlow] {s}";
        }
        Debug.Log($"[AppFlowController] status: {s}");
    }

    // ---------- UI 構築 (手続き生成) ----------

    private void EnsureEventSystem()
    {
        if (EventSystem.current != null)
        {
            return;
        }
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }

    private void BuildUI()
    {
        var canvasGO = new GameObject("AppFlowCanvas");
        canvasGO.transform.SetParent(transform, false);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100; // M2 の Canvas より前面に出して分岐結果を見えるようにする
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        canvasGO.AddComponent<GraphicRaycaster>();

        // Login (Step 2): 背景パネル + LoginPanel コンポーネントが UI を手続き生成.
        _loginPanel = CreateBarePanel(canvasGO, "LoginPanel", new Color(0.12f, 0.18f, 0.32f, 1f));
        _loginPanel.AddComponent<LoginPanel>().Initialize(this, _font);

        // Onboarding (Step 3): 背景パネル + OnboardingPanel コンポーネントが UI を手続き生成.
        _onboardingPanel = CreateBarePanel(canvasGO, "OnboardingPanel", new Color(0.10f, 0.20f, 0.15f, 1f));
        _onboardingPanel.AddComponent<OnboardingPanel>().Initialize(this, _font);

        // Home (Step 4 で実装): 半透明で背後の M2 キャラ画面が残ることを可視化.
        _homePanel = CreatePanel(canvasGO, "HomePanel",
            new Color(0.0f, 0.0f, 0.0f, 0.25f),
            "ホーム画面 (Step 4 で実装)\nセッション有・家族あり → ここに分岐\n(背後に M2 キャラ画面が残る)");

        _loginPanel.SetActive(false);
        _onboardingPanel.SetActive(false);
        _homePanel.SetActive(false);

        _statusText = CreateStatusText(canvasGO);
    }

    // ラベルなしの全画面背景パネル (中身は LoginPanel / OnboardingPanel が後から生成する).
    private GameObject CreateBarePanel(GameObject parent, string name, Color bg)
    {
        var panel = new GameObject(name);
        panel.transform.SetParent(parent.transform, false);
        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var img = panel.AddComponent<Image>();
        img.color = bg;
        return panel;
    }

    private GameObject CreatePanel(GameObject parent, string name, Color bg, string label)
    {
        var panel = new GameObject(name);
        panel.transform.SetParent(parent.transform, false);
        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var img = panel.AddComponent<Image>();
        img.color = bg;

        var textGO = new GameObject("Label");
        textGO.transform.SetParent(panel.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = new Vector2(0.1f, 0.3f);
        textRect.anchorMax = new Vector2(0.9f, 0.7f);
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var text = textGO.AddComponent<Text>();
        text.text = label;
        text.font = _font;
        text.fontSize = 44;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        return panel;
    }

    private Text CreateStatusText(GameObject parent)
    {
        var go = new GameObject("StatusBar");
        go.transform.SetParent(parent.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0.92f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = new Vector2(20, 0);
        rect.offsetMax = new Vector2(-20, -10);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.6f);

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 0);
        textRect.offsetMax = new Vector2(-10, 0);
        var text = textGO.AddComponent<Text>();
        text.font = _font;
        text.fontSize = 28;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }
}
