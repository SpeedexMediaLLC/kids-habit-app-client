// M4 Step 2: 大人モード復帰のパスコード照合ゲート (全画面オーバーレイ).
//
// AppFlowController が生成した PasscodeGatePanel GameObject にアタッチされ, Initialize() で UI を
// 手続き生成する (S0 SettingsPanel と同じ列レイアウト方式). 入力は PasscodeEntry に委譲する.
//
// フロー: 子供モードで「おとなにもどる」→ ModeSwitcher → AppFlowController.RequestAdultUnlock() で
//   本パネルを前面表示 (Begin) → PasscodeEntry が 6桁を発火 → verify_passcode RPC.
//     verified         : ゲートを閉じ GameStateService.SwitchToAdult() (照合成功時のみ大人モードへ)
//     mismatch         : 入力クリア + 「パスコードが違います」(サーバ仕様上, 3回目の誤入力も mismatch を返す)
//     locked           : locked_until から残時間を静的算出し「約 N 秒お待ちください」(4回目以降に表に出る)
//     deletion_pending /
//     not_authorized   : S2 では簡易エラー + 再試行のみ (本格ルーティングは S4 に委譲)
//   「戻る」: 子供モードのまま閉じる (照合せず復帰しない).
//
// 残時間は表示専用. ロックの実効判定はサーバ側 (verify_passcode が locked_until > now() で拒否) のため,
// 端末時計をいじっても回避できない (計画書 claude-code-delightful-scone.md:441).

using System;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

public class PasscodeGatePanel : MonoBehaviour
{
    private AppFlowController _appFlow;
    private Font _font;
    private PasscodeEntry _entry;
    private bool _built;
    private bool _busy;

    public void Initialize(AppFlowController appFlow, Font font)
    {
        _appFlow = appFlow;
        _font = font;
        BuildUI();
    }

    private void BuildUI()
    {
        if (_built) return;
        _built = true;

        var column = CreateColumn();
        CreateTitle(column, "大人モードに戻る");
        CreateLine(column, "保護者の方がパスコードを入力してください");

        _entry = gameObject.AddComponent<PasscodeEntry>();
        _entry.Initialize(column, _font, null, "決定");
        _entry.Submitted += OnSubmitted;

        CreateButton(column, "戻る", new Color(0.45f, 0.45f, 0.50f), OnCancel);
    }

    // AppFlowController がパネル表示時に呼ぶ. 毎回の入場で入力状態をリセットする.
    public void Begin()
    {
        _busy = false;
        if (_entry != null)
        {
            _entry.SetInteractable(true);
            _entry.Clear();
            _entry.SetMessage("");
            _entry.Focus();
        }
    }

    private void OnSubmitted(string code)
    {
        VerifyAsync(code).Forget();
    }

    private async UniTask VerifyAsync(string code)
    {
        if (_busy) return;
        _busy = true;
        _entry.SetInteractable(false);
        _entry.SetMessage("確認中...");
        try
        {
            var result = await ApiService.VerifyPasscodeAsync(code);
            switch (result.ResultCode)
            {
                case "verified":
                    _entry.SetMessage("");
                    Close();
                    // 照合成功時のみ状態を切替える. SwitchToAdult の ModeChanged を受けて
                    // AppFlowController が Home を復元する.
                    var gs = GameStateService.Instance;
                    if (gs != null) gs.SwitchToAdult();
                    else Debug.LogWarning("[PasscodeGatePanel] GameStateService.Instance not found");
                    return;
                case "mismatch":
                    _entry.Clear();
                    _entry.SetMessage("パスコードが違います");
                    break;
                case "locked":
                    _entry.Clear();
                    _entry.SetMessage(BuildLockedMessage(result.Raw));
                    break;
                case "deletion_pending":
                case "not_authorized":
                    // S2 では強制ログアウト等のルーティングは行わず簡易表示にとどめる (S4 で対応).
                    _entry.Clear();
                    _entry.SetMessage("現在この操作はできません。ログインし直してください");
                    break;
                default:
                    // 想定外の result_code. 生の応答 (result_code) は Console のみに残し, 画面は固定文言.
                    Debug.LogWarning($"[PasscodeGatePanel] unexpected result_code='{result.ResultCode}'");
                    _entry.Clear();
                    _entry.SetMessage("通信エラーが発生しました。もう一度お試しください");
                    break;
            }
        }
        catch (Exception ex)
        {
            // 例外メッセージ (サーバ応答の生 JSON 等) は Console のみ. 画面には固定文言だけ出す.
            Debug.LogWarning($"[PasscodeGatePanel] verify failed: {ex}");
            _entry.Clear();
            _entry.SetMessage("通信エラーが発生しました。もう一度お試しください");
        }
        finally
        {
            // verified は return 済 (パネルは閉じている). それ以外は再試行できるよう戻す.
            if (_busy)
            {
                _entry.SetInteractable(true);
                _busy = false;
            }
        }
    }

    private void OnCancel()
    {
        // 照合せず子供モードのまま閉じる (子供の誤タップ救済). 状態は切替えない.
        Close();
    }

    private void Close()
    {
        gameObject.SetActive(false);
        Debug.Log("[PasscodeGatePanel] closed");
    }

    // locked_until (timestamptz) と現在時刻から残り秒数を静的算出する (毎秒更新はしない).
    private string BuildLockedMessage(JObject raw)
    {
        try
        {
            var token = raw != null ? raw["locked_until"] : null;
            if (token != null && token.Type != JTokenType.Null)
            {
                // JObject.Parse は timestamptz を Date(JValue=DateTime) として保持することがあり,
                // Value<DateTimeOffset>() は直接キャストで失敗する. ToObject<>() は型変換するため安全.
                var until = token.ToObject<DateTimeOffset>();
                int secs = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds);
                if (secs > 0)
                {
                    return $"何回か間違えました。約 {secs} 秒お待ちください";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[PasscodeGatePanel] locked_until parse failed: {ex.Message}");
        }
        return "何回か間違えました。しばらくお待ちください";
    }

    // ---------- UI helpers (手続き生成, SettingsPanel 準拠) ----------

    private GameObject CreateColumn()
    {
        var col = new GameObject("Column");
        col.transform.SetParent(transform, false);
        var rect = col.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.10f, 0.18f);
        rect.anchorMax = new Vector2(0.90f, 0.82f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var vlg = col.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 18;
        vlg.childControlWidth = true;
        vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        return col;
    }

    private Text CreateTitle(GameObject parent, string title)
    {
        var go = new GameObject("Title");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 80;
        var text = go.AddComponent<Text>();
        text.text = title;
        text.font = _font;
        text.fontSize = 48;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        return text;
    }

    private Text CreateLine(GameObject parent, string body)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 56;
        var text = go.AddComponent<Text>();
        text.text = body;
        text.font = _font;
        text.fontSize = 28;
        text.color = new Color(0.85f, 0.85f, 0.85f);
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private Button CreateButton(GameObject parent, string label, Color color,
                                UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button_" + label);
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 84;
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
        text.fontSize = 32;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        return btn;
    }
}
