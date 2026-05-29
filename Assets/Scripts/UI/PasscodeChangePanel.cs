// M4 Step 3: パスコード変更パネル (大人モード・設定画面から開く全画面オーバーレイ).
//
// SettingsPanel が必要時に生成し, 閉じる時に破棄する子オーバーレイ. PasscodeEntry (S2 の汎用
// 6桁入力ウィジェット) を 1 個ホストし, 「現在のパスコード」→「新しいパスコード」の 2 ステップで
// change_passcode RPC を呼ぶ (PasscodeEntry は入力 UI に徹し, 変更ロジックは本パネルが持つ = S2 設計).
//
//   現在(6桁) → 決定 → 新規(6桁) → 決定 → ChangePasscodeAsync(old, new)
//     changed          : 成功. Close(true) → SettingsPanel が「パスコードを変更しました」を表示
//     mismatch         : 「現在のパスコードが違います」→ ステップ1 (現在の入力) に戻す
//                        (サーバ仕様: 旧不一致は attempts++, 3回でファミリを1分ロック = verify と共通)
//     locked           : locked_until から残時間を静的算出し「約 N 秒お待ちください」(S2 と同方式)
//     invalid_passcode : 「新しいパスコードは数字6桁で入力してください」(基本は PasscodeEntry が事前検証)
//     deletion_pending /
//     not_authorized   : 簡易エラー (本格ルーティングは S4 委譲)
//   「もどる」: 変更せず閉じる (Close(false)).
//
// 文言は S2 (PasscodeGatePanel) の保護者向けトーンに揃える. 生 JSON / 例外は Console のみ.
// 再設定 (パスコードを忘れた場合) の導線は SettingsPanel 側 (ナビのみ = LogoutToLogin).

using System;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

public class PasscodeChangePanel : MonoBehaviour
{
    private enum Step { Current, New }

    private Font _font;
    private Action<bool> _onClose;   // 引数 = 変更成功したか (true で SettingsPanel が完了表示)
    private PasscodeEntry _entry;
    private Text _heading;
    private Button _backButton;
    private bool _built;
    private bool _busy;

    private Step _step;
    private string _oldPasscode;

    // font: 手続き生成用フォント. onClose(changed): 閉じる時に呼ぶ (SettingsPanel がオーバーレイを破棄).
    public void Initialize(Font font, Action<bool> onClose)
    {
        _font = font;
        _onClose = onClose;
        BuildUI();
        BeginCurrentStep();
    }

    private void BuildUI()
    {
        if (_built) return;
        _built = true;

        var column = CreateColumn();
        CreateTitle(column, "パスコードを変更");
        _heading = CreateLine(column, "");

        _entry = gameObject.AddComponent<PasscodeEntry>();
        _entry.Initialize(column, _font, null, "決定");
        _entry.Submitted += OnSubmitted;

        _backButton = CreateButton(column, "もどる", new Color(0.45f, 0.45f, 0.50f), OnCancel);
    }

    private void BeginCurrentStep()
    {
        _step = Step.Current;
        _oldPasscode = null;
        if (_heading != null) _heading.text = "現在のパスコードを入力してください";
        ResetEntry();
    }

    private void BeginNewStep()
    {
        _step = Step.New;
        if (_heading != null) _heading.text = "新しいパスコードを入力してください";
        ResetEntry();
    }

    private void ResetEntry()
    {
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
        if (_busy) return;
        if (_step == Step.Current)
        {
            // 形式 (^[0-9]{6}$) は PasscodeEntry が検証済. 旧コードを保持して新規入力へ.
            _oldPasscode = code;
            BeginNewStep();
            return;
        }
        // Step.New: 旧 + 新で変更を実行.
        ChangeAsync(_oldPasscode, code).Forget();
    }

    private async UniTask ChangeAsync(string oldCode, string newCode)
    {
        if (_busy) return;
        _busy = true;
        _entry.SetInteractable(false);
        if (_backButton != null) _backButton.interactable = false;   // 送信中は閉じさせない (破棄回避)
        _entry.SetMessage("確認中...");
        try
        {
            var result = await ApiService.ChangePasscodeAsync(oldCode, newCode);
            switch (result.ResultCode)
            {
                case "changed":
                    _entry.SetMessage("");
                    Close(true);
                    return;
                case "mismatch":
                    // 現在のパスコードが違う → ステップ1に戻して再入力させる.
                    BeginCurrentStep();
                    _entry.SetMessage("現在のパスコードが違います");
                    break;
                case "locked":
                    BeginCurrentStep();
                    _entry.SetMessage(BuildLockedMessage(result.Raw));
                    break;
                case "invalid_passcode":
                    // 新パスコードが数字6桁でない (基本は PasscodeEntry の事前検証で予防).
                    _entry.Clear();
                    _entry.SetMessage("新しいパスコードは数字6桁で入力してください");
                    break;
                case "deletion_pending":
                case "not_authorized":
                    // 本格ルーティング (ログアウト等) は S4 委譲. ここは簡易表示にとどめる.
                    _entry.Clear();
                    _entry.SetMessage("現在この操作はできません。ログインし直してください");
                    break;
                default:
                    // 想定外 result_code. 生応答は Console のみ, 画面は固定文言.
                    Debug.LogWarning($"[PasscodeChangePanel] unexpected result_code='{result.ResultCode}'");
                    _entry.Clear();
                    _entry.SetMessage("通信エラーが発生しました。もう一度お試しください");
                    break;
            }
        }
        catch (Exception ex)
        {
            // 例外メッセージ (サーバ応答の生 JSON 等) は Console のみ. 画面は固定文言.
            Debug.LogWarning($"[PasscodeChangePanel] change failed: {ex}");
            _entry.Clear();
            _entry.SetMessage("通信エラーが発生しました。もう一度お試しください");
        }
        finally
        {
            // changed は Close 済 (これからオーバーレイ破棄). それ以外は再試行できるよう戻す.
            if (_busy)
            {
                _entry.SetInteractable(true);
                if (_backButton != null) _backButton.interactable = true;
                _busy = false;
            }
        }
    }

    private void OnCancel()
    {
        Close(false);
    }

    private void Close(bool changed)
    {
        // オーバーレイ GameObject の破棄は SettingsPanel (生成側) が行う.
        _onClose?.Invoke(changed);
    }

    // locked_until (timestamptz) と現在時刻から残り秒数を静的算出する (S2 PasscodeGatePanel と同方式).
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
            Debug.LogWarning($"[PasscodeChangePanel] locked_until parse failed: {ex.Message}");
        }
        return "何回か間違えました。しばらくお待ちください";
    }

    // ---------- UI helpers (手続き生成, PasscodeGatePanel 準拠) ----------

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
