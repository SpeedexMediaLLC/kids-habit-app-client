// M3 Step 3: 新規家族作成オンボーディング.
//
// AppFlowController が生成した OnboardingPanel GameObject にアタッチされ, Initialize() で UI を
// 手続き生成する (ScrollRect + 多入力 + 同意 Toggle 2 個).
//   入力: email/password (新規) + 家族名 / 保護者ニックネーム / 子ニックネーム + 6桁パスコード
//   同意: 親同意 + データ使用同意 (両方 ON で「家族を作成」ボタンが有効)
//   文面: docs/CONSENT_TEXTS.md v1 (表示ミラー, 要確認: client ミラー管理方針)
//
// フロー: SignUp → SignIn (Gotrue 6.0.3 は SignUp で SignedIn 非発火のため明示 SignIn) →
//   CreateFamilyWithParent(consentVersion:"v1") → created なら RefreshSession → Reroute (→ Home).
//   already_in_family / invalid_passcode はエラー表示.

using System;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class OnboardingPanel : MonoBehaviour
{
    private const string ConsentVersion = "v1";

    // 表示用ミラー (SoT は server docs/CONSENT_TEXTS.md v1).
    // 要確認: 文面更新時の二重管理を避けるなら Resources アセット化 or サーバー取得を後続で検討.
    private const string ConsentText =
        "【親同意】\n" +
        "本アプリは未就学児(2〜4歳が中心)向けの生活習慣アプリです。保護者の方が設定・管理を行い、" +
        "お子さま自身が「頑張ったボタン」を押す場面を含みます。\n" +
        "・私は、お子さまの保護者(親権者)です。\n" +
        "・私は、お子さまに代わって本アプリの利用同意を行う権限を有します。\n" +
        "・お子さまの利用は、私の監督のもとで行われます。\n\n" +
        "【データ使用】\n" +
        "収集する情報: 保護者のメール/パスワード、家族の名前・ニックネーム、習慣のタイトル・強度・達成記録、同意の記録。\n" +
        "収集しない情報: お子さまの本名・年齢、写真、位置情報、連絡先、広告識別子。\n" +
        "送信先: 当社が管理するSupabaseのサーバー(東京)のみ。第三者の分析・広告へは送信しません。\n" +
        "保存期間: アカウントが有効な間。削除申請後14日経過時点で完全削除。";

    private AppFlowController _appFlow;
    private Font _font;

    private InputField _emailInput;
    private InputField _passwordInput;
    private InputField _familyNameInput;
    private InputField _parentNicknameInput;
    private InputField _childNicknameInput;
    private InputField _passcodeInput;
    private Toggle _parentalConsentToggle;
    private Toggle _dataUseConsentToggle;
    private Button _createButton;
    private Text _statusText;

    public void Initialize(AppFlowController appFlow, Font font)
    {
        _appFlow = appFlow;
        _font = font;
        BuildUI();
        UpdateCreateButtonInteractable();
    }

    private void BuildUI()
    {
        var content = CreateScrollContent();

        CreateTitle(content, "新規家族作成");
        _emailInput = CreateInput(content, "メールアドレス", InputField.ContentType.EmailAddress);
        _passwordInput = CreateInput(content, "パスワード", InputField.ContentType.Password);
        _familyNameInput = CreateInput(content, "家族の名前 (例: やまだ家)", InputField.ContentType.Standard);
        _parentNicknameInput = CreateInput(content, "保護者のニックネーム", InputField.ContentType.Standard);
        _childNicknameInput = CreateInput(content, "お子さまのニックネーム", InputField.ContentType.Standard);
        _passcodeInput = CreateInput(content, "大人用パスコード (数字6桁)", InputField.ContentType.Pin);
        _passcodeInput.characterLimit = 6;

        CreateConsentText(content, ConsentText);
        _parentalConsentToggle = CreateToggle(content,
            "保護者として上記に同意します (親権者・代理同意・監督)");
        _dataUseConsentToggle = CreateToggle(content,
            "上記のデータの収集・利用に同意します");
        _parentalConsentToggle.onValueChanged.AddListener(_ => UpdateCreateButtonInteractable());
        _dataUseConsentToggle.onValueChanged.AddListener(_ => UpdateCreateButtonInteractable());

        _createButton = CreateButton(content, "家族を作成", new Color(0.20f, 0.55f, 0.35f),
            () => OnCreate().Forget());
        // 「ログインに戻る」: 入力内容は破棄して Login パネルへ (ShowOnboarding と対).
        CreateButton(content, "ログインに戻る", new Color(0.35f, 0.35f, 0.40f), OnBackToLogin);
        _statusText = CreateStatus(content);
    }

    private void UpdateCreateButtonInteractable()
    {
        bool ok = _parentalConsentToggle != null && _parentalConsentToggle.isOn
                  && _dataUseConsentToggle != null && _dataUseConsentToggle.isOn;
        if (_createButton != null) _createButton.interactable = ok;
    }

    private void OnBackToLogin()
    {
        // 入力内容は破棄してよい (戻るだけ). パネル切替は AppFlowController に委譲.
        if (_appFlow != null)
        {
            _appFlow.ShowLogin();
        }
    }

    private async UniTask OnCreate()
    {
        var email = _emailInput.text != null ? _emailInput.text.Trim() : "";
        var password = _passwordInput.text ?? "";
        var familyName = _familyNameInput.text != null ? _familyNameInput.text.Trim() : "";
        var parentNick = _parentNicknameInput.text != null ? _parentNicknameInput.text.Trim() : "";
        var childNick = _childNicknameInput.text != null ? _childNicknameInput.text.Trim() : "";
        var passcode = _passcodeInput.text ?? "";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)
            || string.IsNullOrEmpty(familyName) || string.IsNullOrEmpty(parentNick)
            || string.IsNullOrEmpty(childNick))
        {
            SetStatus("すべての項目を入力してください");
            return;
        }
        if (!Regex.IsMatch(passcode, "^[0-9]{6}$"))
        {
            SetStatus("パスコードは数字6桁で入力してください");
            return;
        }
        // ボタンの gating に加え二重チェック (同意必須).
        if (!_parentalConsentToggle.isOn || !_dataUseConsentToggle.isOn)
        {
            SetStatus("2つの同意にチェックしてください");
            return;
        }

        SetInteractable(false);
        SetStatus("アカウント作成中...");
        try
        {
            // Gotrue 6.0.3 は SignUp で SignedIn を発火しないため, 続けて SignIn して
            // authenticated セッションを確立する (M1Tester と同じ運用).
            await AuthService.SignUpAsync(email, password);
            await AuthService.SignInAsync(email, password);

            SetStatus("家族を作成中...");
            var result = await ApiService.CreateFamilyWithParentAsync(
                familyName, parentNick, childNick, passcode, ConsentVersion);

            switch (result.ResultCode)
            {
                case "created":
                    // family_id を含む新 JWT を取得 (RLS/メンバー判定に必要).
                    await AuthService.RefreshSessionAsync();
                    SetStatus("作成完了. 画面を切り替えます...");
                    _appFlow.Reroute();
                    break;
                case "already_in_family":
                    SetStatus("このアカウントは既に家族に所属しています。ログイン画面からログインしてください。");
                    SetInteractable(true);
                    break;
                case "invalid_passcode":
                    SetStatus("パスコードは数字6桁で入力してください");
                    SetInteractable(true);
                    break;
                default:
                    SetStatus("作成に失敗しました: " + result.ResultCode);
                    SetInteractable(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OnboardingPanel] create failed: {ex.Message}");
            SetStatus("エラー: " + ex.Message);
            SetInteractable(true);
        }
    }

    private void SetInteractable(bool value)
    {
        if (_emailInput != null) _emailInput.interactable = value;
        if (_passwordInput != null) _passwordInput.interactable = value;
        if (_familyNameInput != null) _familyNameInput.interactable = value;
        if (_parentNicknameInput != null) _parentNicknameInput.interactable = value;
        if (_childNicknameInput != null) _childNicknameInput.interactable = value;
        if (_passcodeInput != null) _passcodeInput.interactable = value;
        if (_parentalConsentToggle != null) _parentalConsentToggle.interactable = value;
        if (_dataUseConsentToggle != null) _dataUseConsentToggle.interactable = value;
        // 作成ボタンの有効化は同意状態にも依存するため UpdateCreateButtonInteractable に委ねる.
        if (!value && _createButton != null) _createButton.interactable = false;
        if (value) UpdateCreateButtonInteractable();
    }

    private void SetStatus(string s)
    {
        if (_statusText != null) _statusText.text = s;
        Debug.Log("[OnboardingPanel] " + s);
    }

    // ---------- UI helpers (手続き生成, M1Tester 準拠) ----------

    // パネル全体を縦スクロール可能にし, その Content (VerticalLayout) に各要素を積む.
    private GameObject CreateScrollContent()
    {
        var scrollGO = new GameObject("Scroll");
        scrollGO.transform.SetParent(transform, false);
        var scrollRect = scrollGO.AddComponent<RectTransform>();
        scrollRect.anchorMin = new Vector2(0.05f, 0.05f);
        scrollRect.anchorMax = new Vector2(0.95f, 0.95f);
        scrollRect.offsetMin = Vector2.zero;
        scrollRect.offsetMax = Vector2.zero;
        var sr = scrollGO.AddComponent<ScrollRect>();
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 30f;

        var viewportGO = new GameObject("Viewport");
        viewportGO.transform.SetParent(scrollGO.transform, false);
        var vpRect = viewportGO.AddComponent<RectTransform>();
        vpRect.anchorMin = Vector2.zero;
        vpRect.anchorMax = Vector2.one;
        vpRect.offsetMin = Vector2.zero;
        vpRect.offsetMax = Vector2.zero;
        vpRect.pivot = new Vector2(0.5f, 1f);
        var vpImg = viewportGO.AddComponent<Image>();
        vpImg.color = new Color(1f, 1f, 1f, 0.04f);
        viewportGO.AddComponent<RectMask2D>();

        var contentGO = new GameObject("Content");
        contentGO.transform.SetParent(viewportGO.transform, false);
        var contentRect = contentGO.AddComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;
        var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 16;
        vlg.padding = new RectOffset(24, 24, 24, 24);
        vlg.childControlWidth = true;
        vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperCenter;
        var fitter = contentGO.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        sr.viewport = vpRect;
        sr.content = contentRect;
        return contentGO;
    }

    private Text CreateTitle(GameObject parent, string title)
    {
        var go = new GameObject("Title");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 70;
        var text = go.AddComponent<Text>();
        text.text = title;
        text.font = _font;
        text.fontSize = 44;
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

    private void CreateConsentText(GameObject parent, string body)
    {
        var go = new GameObject("ConsentText");
        go.transform.SetParent(parent.transform, false);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 360;
        var bgImg = go.AddComponent<Image>();
        bgImg.color = new Color(1f, 1f, 1f, 0.10f);

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16, 12);
        textRect.offsetMax = new Vector2(-16, -12);
        var text = textGO.AddComponent<Text>();
        text.text = body;
        text.font = _font;
        text.fontSize = 22;
        text.color = Color.white;
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
    }

    private Toggle CreateToggle(GameObject parent, string label)
    {
        var go = new GameObject("Toggle");
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<RectTransform>();
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 72;
        var toggle = go.AddComponent<Toggle>();
        toggle.isOn = false;

        var boxGO = new GameObject("Box");
        boxGO.transform.SetParent(go.transform, false);
        var boxRect = boxGO.AddComponent<RectTransform>();
        boxRect.anchorMin = new Vector2(0f, 0.5f);
        boxRect.anchorMax = new Vector2(0f, 0.5f);
        boxRect.pivot = new Vector2(0f, 0.5f);
        boxRect.sizeDelta = new Vector2(48, 48);
        boxRect.anchoredPosition = new Vector2(4, 0);
        var boxImg = boxGO.AddComponent<Image>();
        boxImg.color = Color.white;
        toggle.targetGraphic = boxImg;

        var checkGO = new GameObject("Check");
        checkGO.transform.SetParent(boxGO.transform, false);
        var checkRect = checkGO.AddComponent<RectTransform>();
        checkRect.anchorMin = Vector2.zero;
        checkRect.anchorMax = Vector2.one;
        checkRect.offsetMin = new Vector2(8, 8);
        checkRect.offsetMax = new Vector2(-8, -8);
        var checkImg = checkGO.AddComponent<Image>();
        checkImg.color = new Color(0.20f, 0.70f, 0.30f);
        toggle.graphic = checkImg;

        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(go.transform, false);
        var labelRect = labelGO.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(64, 0);
        labelRect.offsetMax = new Vector2(0, 0);
        var labelText = labelGO.AddComponent<Text>();
        labelText.text = label;
        labelText.font = _font;
        labelText.fontSize = 24;
        labelText.color = Color.white;
        labelText.alignment = TextAnchor.MiddleLeft;
        labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
        labelText.verticalOverflow = VerticalWrapMode.Overflow;

        return toggle;
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
