// M4 Step 2: 汎用 6桁パスコード入力ウィジェット.
//
// 役割は「6桁数字の入力 + 形式検証 + インラインメッセージ表示」までの再利用部品.
//   - 入力は OnboardingPanel と同じ InputField (ContentType.Pin, characterLimit 6) 方式に揃える.
//   - 決定ボタン押下で ^[0-9]{6}$ を検証し, 合格時のみ Submitted(code) を発火する.
//   - RPC 呼び出しや「パスコード変更」等の業務ロジックは持たない (照合は S2 の PasscodeGatePanel,
//     変更は S3 が各々ハンドリングする). この部品は入力 UI に徹し, S3 でも共用する.
//
// 使い方: 任意の親 (VerticalLayoutGroup を持つ列) を渡して Initialize() すると, その親に
//   [任意の prompt ラベル] / 入力欄 / 決定ボタン / メッセージ行 を順に生成する.

using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

public class PasscodeEntry : MonoBehaviour
{
    // 形式合格 (数字6桁) の入力で決定された時のみ発火. 引数は入力された 6 桁コード.
    public event Action<string> Submitted;

    private Font _font;
    private InputField _input;
    private Button _submitButton;
    private Text _message;

    // parent: この部品の UI を積む親 (VerticalLayoutGroup を持つ列を想定).
    // prompt: 入力欄の上に出す見出し (null/空なら省略). submitLabel: 決定ボタンの文言.
    public void Initialize(GameObject parent, Font font, string prompt, string submitLabel)
    {
        _font = font;
        if (!string.IsNullOrEmpty(prompt))
        {
            CreatePrompt(parent, prompt);
        }
        _input = CreateInput(parent, "数字6桁");
        _submitButton = CreateButton(parent, submitLabel, new Color(0.20f, 0.45f, 0.70f), OnSubmit);
        _message = CreateMessage(parent);
    }

    public void Clear()
    {
        if (_input != null) _input.text = "";
    }

    public void SetMessage(string message)
    {
        if (_message != null) _message.text = message ?? "";
    }

    public void SetInteractable(bool value)
    {
        if (_input != null) _input.interactable = value;
        if (_submitButton != null) _submitButton.interactable = value;
    }

    // 表示直後に入力欄へフォーカスし, 端末の数字キーボードを促す (best-effort).
    public void Focus()
    {
        if (_input != null)
        {
            _input.Select();
            _input.ActivateInputField();
        }
    }

    private void OnSubmit()
    {
        var code = _input != null && _input.text != null ? _input.text : "";
        if (!Regex.IsMatch(code, "^[0-9]{6}$"))
        {
            SetMessage("数字6桁で入力してください");
            return;
        }
        Submitted?.Invoke(code);
    }

    // ---------- UI helpers (手続き生成, OnboardingPanel 準拠) ----------

    private Text CreatePrompt(GameObject parent, string prompt)
    {
        var go = new GameObject("Prompt");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 56;
        var text = go.AddComponent<Text>();
        text.text = prompt;
        text.font = _font;
        text.fontSize = 30;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private InputField CreateInput(GameObject parent, string placeholder)
    {
        var go = new GameObject("Input_Passcode");
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
        text.fontSize = 36;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleCenter;
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
        ph.alignment = TextAnchor.MiddleCenter;
        ph.text = placeholder;

        input.textComponent = text;
        input.placeholder = ph;
        input.contentType = InputField.ContentType.Pin;
        input.characterLimit = 6;
        return input;
    }

    private Button CreateButton(GameObject parent, string label, Color color,
                                UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button_" + label);
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 88;
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

    private Text CreateMessage(GameObject parent)
    {
        var go = new GameObject("Message");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 70;
        var text = go.AddComponent<Text>();
        text.font = _font;
        text.fontSize = 26;
        text.color = new Color(1f, 0.9f, 0.6f);
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }
}
