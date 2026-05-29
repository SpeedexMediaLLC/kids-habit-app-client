// M4 Step 4: 削除予約中画面 (全画面・不透明). deletion_pending な family の起動先 + 申請直後の遷移先.
//
// AppFlowController が生成 (背景は不透明 = 背後 M2 の ToChildButton を覆い raycast を遮断するため,
// MainScene 非編集のまま「削除を取り消す」以外の操作をブロックできる. 計画 :768-769). 表示のたびに
// AppFlowController.Show(DeletionReserved) が Load() を呼ぶ (HomePanel と同じ明示ロード方式. Settings 系の
// ような OnEnable 自動更新はしない = 最上位画面なので).
//
// フロー:
//   表示 → deletion_requests を SELECT → status=='pending' の行
//     有り: 「あと N 日で…削除されます」+「削除を取り消す」
//     無し: 既にキャンセル/実行済 → Reroute() で再判定 (通常 Home)
//   削除を取り消す → cancel_account_deletion(id)
//     cancelled : Reroute() (deletion_pending=false → Home 復帰)
//     not_found : Reroute() (他端末で既に解除/実行 → 再判定)
//     not_authorized / 例外 / 想定外: 固定文言 + 再試行
//
// 「ログアウト」等の他導線は出さない (計画 :768-769「キャンセルのみ」). 文言は S2/S3 の保護者向けトーン.
// 生 JSON / 例外は Console のみ.

using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class DeletionReservedPanel : MonoBehaviour
{
    private AppFlowController _appFlow;
    private Font _font;
    private bool _built;
    private bool _busy;      // cancel 送信中 (二重送信防止)
    private bool _loading;   // 読み込み中

    private Text _daysText;
    private Text _messageText;
    private Button _cancelButton;
    private Guid _pendingRequestId;
    private bool _hasPending;

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
        CreateTitle(column, "アカウント削除を申請中です");
        _daysText = CreateLine(column, "");
        CreateLine(column, "この期間内に取り消すと、これまでどおりお使いいただけます。");
        _messageText = CreateLine(column, "");
        _messageText.color = new Color(1f, 0.9f, 0.6f);
        _cancelButton = CreateButton(column, "削除を取り消す", new Color(0.20f, 0.55f, 0.35f), OnCancelClicked);
    }

    // AppFlowController が画面表示時に呼ぶ (HomePanel.Refresh と同じ役割).
    public void Load()
    {
        LoadAsync().Forget();
    }

    private async UniTask LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _busy = false;
        _hasPending = false;
        SetMessage("");
        SetCancelInteractable(false);
        SetDays("読み込み中...");
        try
        {
            var resp = await SupabaseService.Client.From<DeletionRequestModel>().Get();
            DeletionRequestModel pending = null;
            if (resp?.Models != null)
            {
                foreach (var r in resp.Models)
                {
                    if (r.Status == "pending") { pending = r; break; }
                }
            }

            if (pending == null)
            {
                // 既に解除/実行済. この画面に留まる理由が無いので通常ルーティングに委ねる.
                Debug.Log("[DeletionReservedPanel] no pending request -> reroute");
                if (_appFlow != null) _appFlow.Reroute();
                return;
            }

            _pendingRequestId = pending.Id;
            _hasPending = true;
            SetDays(BuildDaysMessage(pending.ScheduledDeleteAt));
            SetCancelInteractable(true);
        }
        catch (Exception ex)
        {
            // 取得失敗 (通信不調 / 逆シリアライズ). 画面は固定文言, 生応答は Console のみ.
            Debug.LogWarning($"[DeletionReservedPanel] load failed: {ex.Message}");
            SetDays("削除予約の情報を取得できませんでした");
            SetMessage("通信環境を確認して、開き直してください");
            SetCancelInteractable(false);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnCancelClicked()
    {
        if (_busy || !_hasPending) return;
        CancelAsync().Forget();
    }

    private async UniTask CancelAsync()
    {
        if (_busy) return;
        _busy = true;
        SetCancelInteractable(false);
        SetMessage("取り消し中...");
        try
        {
            var result = await ApiService.CancelAccountDeletionAsync(_pendingRequestId);
            switch (result.ResultCode)
            {
                case "cancelled":
                    // deletion_pending=false に戻った. 再ルーティングで Home へ.
                    SetMessage("");
                    if (_appFlow != null) _appFlow.Reroute();
                    return;
                case "not_found":
                    // 他端末で既に解除/実行済. 再判定に委ねる.
                    if (_appFlow != null) _appFlow.Reroute();
                    return;
                case "not_authorized":
                    SetMessage("この操作は保護者のみ可能です");
                    break;
                default:
                    // 想定外 result_code. 生応答は Console のみ, 画面は固定文言.
                    Debug.LogWarning($"[DeletionReservedPanel] unexpected result_code='{result.ResultCode}'");
                    SetMessage("通信エラーが発生しました。もう一度お試しください");
                    break;
            }
        }
        catch (Exception ex)
        {
            // 例外メッセージ (サーバ応答の生 JSON 等) は Console のみ. 画面は固定文言.
            Debug.LogWarning($"[DeletionReservedPanel] cancel failed: {ex}");
            SetMessage("通信エラーが発生しました。もう一度お試しください");
        }
        finally
        {
            // cancelled / not_found は return 済 (Reroute 済). それ以外は再試行できるよう戻す.
            if (_busy)
            {
                _busy = false;
                SetCancelInteractable(true);
            }
        }
    }

    // scheduled_delete_at と現在時刻から残り日数を算出する (静的表示・毎秒更新はしない).
    private string BuildDaysMessage(DateTimeOffset scheduledDeleteAt)
    {
        var remaining = scheduledDeleteAt - DateTimeOffset.UtcNow;
        int days = (int)Math.Ceiling(remaining.TotalDays);
        if (days < 0) days = 0;
        return $"あと {days} 日でアカウントとすべてのデータが削除されます";
    }

    private void SetDays(string s) { if (_daysText != null) _daysText.text = s ?? ""; }
    private void SetMessage(string s) { if (_messageText != null) _messageText.text = s ?? ""; }
    private void SetCancelInteractable(bool v) { if (_cancelButton != null) _cancelButton.interactable = v; }

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
        text.fontSize = 44;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
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
