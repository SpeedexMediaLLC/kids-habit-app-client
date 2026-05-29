// M3 Step 2: ログイン画面 (Email+PW).
//
// AppFlowController が生成した LoginPanel GameObject にアタッチされ, Initialize() で UI を
// 手続き生成する (M1Tester と同じ Legacy UnityEngine.UI 方式, TMP 非依存).
//   - ログイン成功 → AppFlowController.Reroute() で再ルーティング (家族有→Home / 無→Onboarding)
//   - 「新規作成」    → AppFlowController.ShowOnboarding()
// ※ Step 2 範囲: パスコード/習慣 等には触れない. 認証は AuthService.SignInAsync のみ.

using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class LoginPanel : MonoBehaviour
{
    private AppFlowController _appFlow;
    private Font _font;

    private InputField _emailInput;
    private InputField _passwordInput;
    private Button _loginButton;
    private Button _signUpNavButton;
    private Text _statusText;

    public void Initialize(AppFlowController appFlow, Font font)
    {
        _appFlow = appFlow;
        _font = font;
        BuildUI();
    }

    private void BuildUI()
    {
        var column = CreateColumn();

        CreateTitle(column, "ログイン");
        _emailInput = CreateInput(column, "メールアドレス", InputField.ContentType.EmailAddress);
        _passwordInput = CreateInput(column, "パスワード", InputField.ContentType.Password);
        _loginButton = CreateButton(column, "ログイン", new Color(0.20f, 0.45f, 0.85f),
            () => OnLogin().Forget());
        _signUpNavButton = CreateButton(column, "新規作成", new Color(0.30f, 0.55f, 0.40f),
            OnSignUpNav);
        _statusText = CreateStatus(column);
    }

    private void OnSignUpNav()
    {
        if (_appFlow != null)
        {
            _appFlow.ShowOnboarding();
        }
    }

    private async UniTask OnLogin()
    {
        var email = _emailInput.text != null ? _emailInput.text.Trim() : "";
        var password = _passwordInput.text ?? "";
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            SetStatus("メールアドレスとパスワードを入力してください");
            return;
        }

        SetInteractable(false);
        SetStatus("ログイン中...");
        try
        {
            await AuthService.SignInAsync(email, password);
            SetStatus("ログイン成功. 画面を切り替えます...");
            // 成功時はパネルが切り替わるので interactable は戻さない.
            _appFlow.Reroute();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LoginPanel] SignIn failed: {ex.Message}");
            SetStatus("ログイン失敗: " + ex.Message);
            SetInteractable(true);
        }
    }

    private void SetInteractable(bool value)
    {
        if (_loginButton != null) _loginButton.interactable = value;
        if (_signUpNavButton != null) _signUpNavButton.interactable = value;
        if (_emailInput != null) _emailInput.interactable = value;
        if (_passwordInput != null) _passwordInput.interactable = value;
    }

    private void SetStatus(string s)
    {
        if (_statusText != null) _statusText.text = s;
        Debug.Log("[LoginPanel] " + s);
    }

    // ---------- UI helpers (手続き生成, M1Tester 準拠) ----------

    private GameObject CreateColumn()
    {
        var col = new GameObject("Column");
        col.transform.SetParent(transform, false);
        var rect = col.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.08f, 0.22f);
        rect.anchorMax = new Vector2(0.92f, 0.78f);
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

    private InputField CreateInput(GameObject parent, string placeholder, InputField.ContentType contentType)
    {
        var go = new GameObject("Input_" + placeholder);
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
        text.fontSize = 30;
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
        input.contentType = contentType;
        return input;
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

    private Text CreateStatus(GameObject parent)
    {
        var go = new GameObject("Status");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 90;
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
