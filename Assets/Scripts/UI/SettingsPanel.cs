// M4 Step 0: 設定画面の器 (大人モード専用).
//
// AppFlowController が生成した SettingsPanel GameObject にアタッチされ, Initialize() で UI を
// 手続き生成する (LoginPanel / OnboardingPanel / HomePanel と同じ Legacy UnityEngine.UI 方式, TMP 非依存).
//   - Home ヘッダの「設定」導線 → AppFlowController.OpenSettings() で全画面オーバーレイ表示
//   - 「ホームにもどる」      → AppFlowController.CloseSettings()
//
// ※ S0 の範囲は「器 + 開閉導線」のみ. 以下の中身は本 Step では実装せず, 後続 Step で本パネルに足していく:
//     習慣 CRUD (S1) / パスコード照合・変更・再設定 (S2,S3) / アカウント削除 (S4) / データ最小化注記・規約リンク (S5).

using UnityEngine;
using UnityEngine.UI;

public class SettingsPanel : MonoBehaviour
{
    private AppFlowController _appFlow;
    private Font _font;
    private bool _built;

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

        CreateTitle(column, "設定");
        // S0 は器のみ. 各機能 (習慣の管理 / パスコード / アカウント削除 / このアプリについて) は後続 Step で追加する.
        CreateLine(column, "設定項目は順次追加します");
        CreateButton(column, "ホームにもどる", new Color(0.45f, 0.45f, 0.50f), OnBack);
    }

    private void OnBack()
    {
        if (_appFlow != null)
        {
            _appFlow.CloseSettings();
        }
    }

    // ---------- UI helpers (手続き生成, LoginPanel 準拠) ----------

    private GameObject CreateColumn()
    {
        var col = new GameObject("Column");
        col.transform.SetParent(transform, false);
        var rect = col.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.06f, 0.12f);
        rect.anchorMax = new Vector2(0.94f, 0.88f);
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
        le.minHeight = 60;
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
