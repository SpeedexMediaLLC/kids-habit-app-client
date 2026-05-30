// HabitSyncService: 頑張ったボタンのオフライン先行コーディネータ (M5 / 計画 §5.1, :692-696)。
//
// record_habit 呼び出しを「ローカル 10 分窓チェック → ローカル保存 → 即演出 (押下側) →
// 通信時 RPC 送信」に変える中核 (計画 :692)。MainScene 起動時に自己ブートストラップする
// (AppFlowController と同方式・MainScene 限定)。
//
// 役割:
//   - RequestRecord: 窓チェック → pending 投入 → オンラインなら即 flush / オフラインならキュー保持
//   - 結果コード 6 種のクライアント状態遷移 (§5.5)
//   - ネットワーク復活検知 (Application.internetReachability polling + OnApplicationFocus, :693)
//   - キュー flush (接続復活時にキュー内全件を順次送信。冪等のため再送は常に安全)
//
// 触らない: server (record_habit は M1 実装済・無改修)、MainScene (手続き生成のまま)、
//   creature の楽観昇格はしない (stage 変更は server 確定 recorded + stage_changed のみ)。

using System;
using System.Globalization;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class HabitSyncService : MonoBehaviour
{
    public static HabitSyncService Instance { get; private set; }

    // 検証用 (Editor で機内モードを模擬する): true の間オフライン扱いにする。UI は持たず本番経路に
    // 影響しない (既定 false)。設定さんが Editor でオフライン→同期を確認する際に外部から立てる用途。
    public static bool DebugForceOffline = false;

    // 検証用 (finding 2): true の間、送信を擬似的に失敗させる (オンラインのままサーバ一時障害を模擬)。
    // オンライン継続中のバックオフ再送が効くかを Editor で確認する用途。既定 false・UI 無し。
    public static bool DebugForceSendFailure = false;

    private const string MainSceneName = "MainScene";
    private static bool _bootstrapped;

    private const float PollInterval = 3f;
    private float _pollTimer;
    private bool _lastOnline;
    private bool _flushing;

    // finding 2 (medium): オンライン継続中でもサーバ一時障害で pending が滞留しないよう、
    // バックオフ付きで定期再送する。最短 5s → 失敗ごとに倍化 → 最長 120s でクランプ、捌け切れば最短に戻す。
    private const float RetryMinSec = 5f;
    private const float RetryMaxSec = 120f;
    private float _retryAccum;
    private float _retryIntervalSec = RetryMinSec;

    // cross-session ガード (安いガード・review high): キューの持ち主 (認証ユーザー id) を 1 値だけ
    // PlayerPrefs に保持する。PendingLog のスキーマは変えない。フルの owner-scoped 化 (PendingLog に
    // owner/family 列 + マイグレーション) は別タスク (DECISIONS §4.13 follow-up)。
    private const string QueueOwnerPrefKey = "m5_queue_owner";

    private enum FlushStep { Continue, StopRetry, StopTerminal }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_bootstrapped) return;
        var active = SceneManager.GetActiveScene().name;
        if (active != MainSceneName)
        {
            Debug.Log($"[HabitSync] Bootstrap skipped (scene='{active}')");
            return;
        }
        _bootstrapped = true;
        var go = new GameObject("HabitSync");
        go.AddComponent<HabitSyncService>();
        Debug.Log("[HabitSync] Bootstrap: HabitSync GameObject created in MainScene");
    }

    private void Awake()
    {
        Instance = this;
        SqliteService.EnsureInitialized();
        _lastOnline = IsOnline;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _bootstrapped = false;
    }

    private void Start()
    {
        // 前回オフラインで溜めたキューがあれば起動時に送る。
        TryFlush("startup");
    }

    private void Update()
    {
        _pollTimer += Time.unscaledDeltaTime;
        if (_pollTimer < PollInterval) return;
        _pollTimer = 0f;

        // オンライン状態の遷移を監視する。IsOnline は internetReachability に加え DebugForceOffline も
        // 反映するため、実機の機内モード解除と Editor 検証時のフラグ解除の両方で復活を拾える (:693)。
        bool online = IsOnline;
        if (online != _lastOnline)
        {
            Debug.Log($"[HabitSync] online {_lastOnline} -> {online}");
            _lastOnline = online;
            if (online)
            {
                _retryIntervalSec = RetryMinSec; // 復活直後は最短間隔から
                _retryAccum = 0f;
                TryFlush("online-recovered");
            }
            return; // 遷移したフレームは上の TryFlush に任せる
        }

        // finding 2: オンライン継続中でも pending が残っていれば、バックオフ付きで定期再送する。
        // internetReachability は online のまま Supabase/DNS/TLS が一時失敗するケースを救う
        // (遷移・focus・押下トリガだけに依存しない)。最短 5s 間隔なので busy-loop にはならない。
        if (online && !_flushing)
        {
            _retryAccum += PollInterval;
            if (_retryAccum >= _retryIntervalSec)
            {
                _retryAccum = 0f;
                if (SqliteService.HasPending())
                {
                    Debug.Log($"[HabitSync] online retry (interval={_retryIntervalSec:0}s)");
                    TryFlush("online-retry-backoff");
                    // バックオフは FlushAsync 完了時に結果で調整する (成功→最短 / 失敗→倍化)。
                }
            }
        }
    }

    private void OnApplicationFocus(bool focus)
    {
        // バックグラウンド復帰 (機内モード解除後の復帰など) でも同期を試みる (:693)。
        if (focus && IsOnline) TryFlush("app-focus");
    }

    private bool IsOnline =>
        !DebugForceOffline && Application.internetReachability != NetworkReachability.NotReachable;

    // ---------- 押下入口 (HabitButton から) ----------

    // 押下入口。ローカル窓チェック → pending 投入 → 送信/キューを同期的に判断し、
    // 「成功演出を出してよいか」を返す (true=出す / false=保存できなかったので出さない)。
    // 演出 (光る/サイズアップ/音) を実際に再生するのは押下側 (HabitButton)。通信は待たない (§5.1-5)。
    // finding 1: 上限到達など「保存できない」ときは成功を偽らない (演出も「あとで送る」も出さず正直に弾く)。
    public bool RequestRecord(Guid memberId, Guid habitId)
    {
        string habit = habitId.ToString();
        string member = memberId.ToString();
        bool haveTime = ServerClock.TryNowUtc(out var nowUtc);

        // cross-session ガード (finding high): 認証ユーザーが取れない状態 (起動時のセッション復元が非同期 /
        // 未ログイン) では「保存できない」。owner を記録できないまま pending を作ると、後の認証済み flush で
        // owner 空+pending=stale 判定でクリアされ偽の成功になる。queue でも direct でも同じなので、ここで
        // 非空ユーザーを必須にし、取れなければ正直に弾く (pending を作らない・演出も出さない)。
        string ownerId = CurrentUserId();
        if (string.IsNullOrEmpty(ownerId))
        {
            Debug.LogWarning("[HabitSync] no authenticated user; reject press (no enqueue / no success effect)");
            ShowCannotSaveMessage();
            return false;
        }

        // SQLite が使えない稀な環境 (ネイティブ未配置等)。
        if (!SqliteService.Available)
        {
            SqliteService.EnsureInitialized();
            if (!SqliteService.Available)
            {
                if (IsOnline)
                {
                    // オンラインならキュー無しで直接送信 (送信見込みあり → 演出 OK)。
                    Debug.LogWarning($"[HabitSync] sqlite unavailable ({SqliteService.InitError}); direct online send");
                    SendDirectFallbackAsync(member, habit).Forget();
                    return true;
                }
                // オフライン + キュー不可 = 保存できない → 正直に弾く (演出を出さない)。
                Debug.LogWarning($"[HabitSync] sqlite unavailable ({SqliteService.InitError}) + offline; reject press");
                ShowCannotSaveMessage();
                return false;
            }
        }

        // cross-session ガード: キューの持ち主を現在のユーザーに揃える。別アカウント/不明の残存 pending は
        // ここでクリアされ、別セッションで他人の記録が replay されない。新規 pending は現ユーザー所有になる。
        ReconcileQueueOwner(ownerId);

        // 連打/重複防止 (§5.4.2/§5.4.3): 未送信 pending or 10 分窓内の成功/却下があれば新規を作らない。
        // 子供画面では演出だけ流す (= 体験を壊さない・データ損失なし)。大人モードは控えめにクールダウン表示。
        if (haveTime && SqliteService.HasBlockingEntry(habit, nowUtc, out var cooldownUntil))
        {
            Debug.Log($"[HabitSync] press within cooldown/pending habit={habit}; effect only");
            ShowCooldownToastIfAdult(cooldownUntil, nowUtc);
            return true;
        }

        string clientEventId = Guid.NewGuid().ToString();
        DateTimeOffset? created = haveTime ? nowUtc : (DateTimeOffset?)null;
        var enq = SqliteService.TryEnqueue(habit, member, clientEventId, created);
        switch (enq)
        {
            case SqliteService.EnqueueResult.Enqueued:
                Debug.Log($"[HabitSync] enqueued habit={habit} event={clientEventId} online={IsOnline}");
                if (IsOnline) TryFlush("press");
                else ShowToastIfAdult("オフラインのため、あとで自動で送信します");
                return true;

            case SqliteService.EnqueueResult.AlreadyPending:
                // 既に未送信 pending がある (時刻基準が無く窓判定を通過したケース)。データ損失ではない。
                Debug.Log($"[HabitSync] already pending habit={habit}; effect only");
                if (IsOnline) TryFlush("press-existing");
                return true;

            case SqliteService.EnqueueResult.CapReached:
            case SqliteService.EnqueueResult.Failed:
            case SqliteService.EnqueueResult.Unavailable:
            default:
                // 上限到達 / 保存失敗 (insert/storage) / SQLite 不可 = いずれも保存できていない →
                // 成功を偽らず正直に弾く (演出を出さない・honest 通知のみ)。データ損失を隠さない。
                Debug.LogWarning($"[HabitSync] cannot save press (result={enq}); reject without success effect");
                ShowCannotSaveMessage();
                return false;
        }
    }

    // ---------- flush (キュー送信) ----------

    private void TryFlush(string reason)
    {
        if (_flushing || !IsOnline || !SqliteService.Available) return;
        FlushAsync(reason).Forget();
    }

    private async UniTask FlushAsync(string reason)
    {
        if (_flushing) return;
        _flushing = true;
        try
        {
            // cross-session ガード: セッションが無ければ送らない (誤った JWT で他人の記録を送らない)。
            // 現在のユーザーにキュー持ち主を揃え、別アカウント/不明の残存 pending はクリアしてから送る。
            string uid = CurrentUserId();
            if (string.IsNullOrEmpty(uid))
            {
                Debug.Log($"[HabitSync] flush ({reason}) skipped: no session");
                return;
            }
            ReconcileQueueOwner(uid);

            var pending = SqliteService.GetPending();
            if (pending.Count == 0) return;
            Debug.Log($"[HabitSync] flush ({reason}) count={pending.Count}");
            foreach (var row in pending)
            {
                if (!IsOnline) break;
                // cross-session ガード (finding medium): await をまたいで認証ユーザーが変わって/消えていないか
                // 毎回再確認する。旧スナップショットの後続行を新セッションで送らない。SendOneAsync 内でも送信直前に
                // 同じ uid で再確認する (二重ガード)。変化時はループを抜け、後段で新ユーザーの reconcile を走らせる。
                if (CurrentUserId() != uid) break;
                var step = await SendOneAsync(row, uid);
                if (step == FlushStep.StopRetry || step == FlushStep.StopTerminal) break;
            }
            // flush 中に認証が変わっていたら、新ユーザーで持ち主を整合させる (旧ユーザーの残存 pending はクリア。
            // 新ユーザーが空=ログアウト中なら reconcile は no-op で旧キューは保持＝同一ユーザー再ログインで再同期)。
            if (CurrentUserId() != uid)
            {
                Debug.Log($"[HabitSync] auth user changed during flush (was '{uid}'); reconcile under current");
                ReconcileQueueOwner(CurrentUserId());
            }
            SqliteService.Prune(ServerClock.TryNowUtc(out var n) ? n : (DateTimeOffset?)null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[HabitSync] flush error: {ex.Message}");
        }
        finally
        {
            _flushing = false;
            // finding 2: 次回バックオフ間隔を結果で調整する。pending が残れば倍化 (サーバを叩き過ぎない)、
            // 捌け切れば最短にリセット。retry の計時もリセットし次回まで間隔を空ける。
            _retryAccum = 0f;
            _retryIntervalSec = SqliteService.PendingCount() > 0
                ? Mathf.Min(_retryIntervalSec * 2f, RetryMaxSec)
                : RetryMinSec;
        }
    }

    private async UniTask<FlushStep> SendOneAsync(PendingLog row, string expectedUid)
    {
        if (!Guid.TryParse(row.MemberId, out var member)
            || !Guid.TryParse(row.HabitId, out var habit)
            || !Guid.TryParse(row.ClientEventId, out var clientEventId))
        {
            Debug.LogError($"[HabitSync] bad row ids id={row.Id}; mark invalid");
            SqliteService.MarkInvalid(row.Id, NowIso());
            return FlushStep.Continue;
        }

        // cross-session ガード (finding medium): 送信直前に認証ユーザーが flush 開始時の expected と一致するか
        // 再確認する。await をまたいでサインアウト/イン等が起きていたら、旧ユーザー宛の行を新セッションで送らない
        // (pending のまま保留・FlushAsync 後段の reconcile が新ユーザーで整合させる)。
        if (CurrentUserId() != expectedUid)
        {
            Debug.Log($"[HabitSync] skip send id={row.Id}: auth user changed (expected '{expectedUid}'); keep pending");
            return FlushStep.StopRetry;
        }

        try
        {
            if (DebugForceSendFailure)
            {
                // 検証用: オンラインのままサーバ到達だけ失敗するケースを模擬 (finding 2 のバックオフ確認)。
                throw new Exception("DebugForceSendFailure (test): simulated transient server failure");
            }
            var result = await ApiService.RecordHabitAsync(member, habit, clientEventId);
            // cross-session ガード (final review #2 high): RPC await をまたいで認証ユーザーが変わって/消えて
            // いたら、旧ユーザーの応答を新セッションに適用しない。最重要の await はこのネットワーク呼び出しなので、
            // 適用直前に再確認する (送信直前チェックだけでは await 中の変化を拾えない)。これを欠くと旧応答で
            // ApplyResult が走り、MarkSynced/MarkRejected や deletion_pending/not_authorized の terminal
            // アクション (ClearAll + 削除予約画面 / ログアウト) を新セッションに誤誘発しうる。結果を捨て pending の
            // まま保留し、後段の FlushAsync reconcile が現 owner で pending のクリア/保持を担当する (DECISIONS §4.13)。
            if (CurrentUserId() != expectedUid)
            {
                Debug.Log($"[HabitSync] auth user changed during send id={row.Id} (expected '{expectedUid}'); discard result, keep pending");
                return FlushStep.StopRetry;
            }
            return ApplyResult(row, result);
        }
        catch (Exception ex)
        {
            // 通信失敗等 → pending のまま残し、後で再送 (§5.1 7b/9b)。冪等なので再送は安全。
            Debug.LogWarning($"[HabitSync] send failed id={row.Id}; keep pending: {ex.Message}");
            ShowToastIfAdult("同期に失敗しました。あとで自動で再送します");
            return FlushStep.StopRetry;
        }
    }

    // §5.5 結果コード 6 種のクライアント状態遷移。
    private FlushStep ApplyResult(PendingLog row, ApiService.RpcResult result)
    {
        switch (result.ResultCode)
        {
            case "recorded":
                SqliteService.MarkSynced(row.Id, NowIso());
                ApplyCreatureStage(result);
                return FlushStep.Continue;

            case "duplicate":
                // 再送扱い (既に記録済・growth は加算しない)。pending をキューから外す。
                SqliteService.MarkSynced(row.Id, NowIso());
                return FlushStep.Continue;

            case "cooldown_active":
                // 10 分窓内の重複。creature は次回起動で server から読み直して整合 (:698)。
                SqliteService.MarkRejected(row.Id, NowIso());
                return FlushStep.Continue;

            case "invalid_habit":
                // habit 削除済 / 同 family 外。除外 + 大人モードのみ警告 (§5.5)。
                SqliteService.MarkInvalid(row.Id, NowIso());
                Debug.LogWarning($"[HabitSync] invalid_habit id={row.Id} habit={row.HabitId}");
                ShowToastIfAdult("この習慣は記録できませんでした");
                return FlushStep.Continue;

            case "deletion_pending":
                // 削除予約中 → 全ローカル状態を破棄して削除予約画面へ強制遷移 (§5.5)。
                Debug.Log("[HabitSync] deletion_pending -> deletion reserved");
                SqliteService.ClearAll();
                GoToDeletionReserved();
                return FlushStep.StopTerminal;

            case "not_authorized":
                // 認可失効 → ログアウト + 再ログイン要求 (§5.5)。
                Debug.Log("[HabitSync] not_authorized -> logout");
                SqliteService.ClearAll();
                LogoutToLogin();
                return FlushStep.StopTerminal;

            default:
                // 不明 / 空コード (非 JSON 応答等)。確定扱いにせず pending のまま据え置き再送。
                // 冪等 (client_event_id) のため再送しても二重カウントしない = データ欠落も二重も防ぐ。
                Debug.LogWarning($"[HabitSync] unknown result_code='{result.ResultCode}' id={row.Id}; keep pending");
                return FlushStep.StopRetry;
        }
    }

    // recorded + stage_changed のときだけ creature を昇格表示する (楽観昇格しない)。
    // かつ子供モードで対象 creature を表示中のときのみ反映 = 次回起動で server と整合 (:698)。
    // 大人モードの HomePanel は自前で server から creature を読み直すため触らない
    // (表示中の別メンバー creature を誤更新しない)。
    private void ApplyCreatureStage(ApiService.RpcResult result)
    {
        var gs = GameStateService.Instance;
        if (gs == null || gs.CurrentMode != GameStateService.GameMode.Child) return;

        var raw = result.Raw;
        var stageToken = raw?["stage_changed"];
        bool stageChanged = stageToken != null && stageToken.Type == JTokenType.Boolean && stageToken.Value<bool>();
        string newStage = raw?["new_stage"]?.ToString();
        if (!stageChanged || string.IsNullOrEmpty(newStage) || newStage == "null") return;

        var disp = FindFirstObjectByType<CreatureDisplay>();
        if (disp != null)
        {
            disp.SetStage(newStage);
            Debug.Log($"[HabitSync] creature stage -> {newStage}");
        }
    }

    // ---------- SQLite 不可時の直接送信フォールバック ----------

    private async UniTask SendDirectFallbackAsync(string member, string habit)
    {
        try
        {
            if (!Guid.TryParse(member, out var m) || !Guid.TryParse(habit, out var h)) return;
            // cross-session ガード (final review #2): direct 経路も RPC await をまたいだ認証変化で旧ユーザーの
            // 応答を新セッションに適用しない (deletion_pending/not_authorized の terminal アクションや stage 反映を
            // 旧応答で誘発しない)。送信時の uid を控え、適用直前に再確認して変化していれば結果を捨てる。
            string expectedUid = CurrentUserId();
            var result = await ApiService.RecordHabitAsync(m, h, Guid.NewGuid());
            if (CurrentUserId() != expectedUid)
            {
                Debug.Log("[HabitSync] auth user changed during direct send; discard result");
                return;
            }
            switch (result.ResultCode)
            {
                case "recorded": ApplyCreatureStage(result); break;
                case "deletion_pending": GoToDeletionReserved(); break;
                case "not_authorized": LogoutToLogin(); break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[HabitSync] direct send failed: {ex.Message}");
        }
    }

    // ---------- 遷移ヘルパー ----------

    private void GoToDeletionReserved()
    {
        GameStateService.Instance?.SwitchToAdult();
        AppFlowController.Instance?.GoToDeletionReserved();
    }

    private void LogoutToLogin()
    {
        GameStateService.Instance?.SwitchToAdult();
        AppFlowController.Instance?.LogoutToLogin();
    }

    private void ShowToastIfAdult(string msg)
    {
        // ShowSyncToast 側で子供モードは抑止する (二重ガード)。
        AppFlowController.Instance?.ShowSyncToast(msg);
    }

    // finding 1: 保存できなかった (上限到達 / キュー不可) ことを正直に伝える。成功を偽らない。
    // この通知だけは子供画面でも出す (成功演出を出さない代わりに「保存できなかった」と知らせるため)。
    // 文言は最小実装。設定さんが後で調整しやすいよう短く保つ。
    private void ShowCannotSaveMessage()
    {
        var gs = GameStateService.Instance;
        bool isChild = gs != null && gs.CurrentMode == GameStateService.GameMode.Child;
        string msg = isChild
            ? "いまは ほぞんできないよ。ネットに つないでね"
            : "保存できませんでした（送信待ちが上限）。接続後にもう一度お試しください";
        AppFlowController.Instance?.ShowToast(msg, allowInChild: true);
    }

    private void ShowCooldownToastIfAdult(DateTimeOffset? until, DateTimeOffset now)
    {
        if (until == null) return;
        var remain = until.Value - now;
        if (remain <= TimeSpan.Zero) return;
        int mins = Mathf.CeilToInt((float)remain.TotalMinutes);
        ShowToastIfAdult($"クールダウン中（あと約 {mins} 分）");
    }

    private static string NowIso() =>
        (ServerClock.TryNowUtc(out var n) ? n : default).ToString("o", CultureInfo.InvariantCulture);

    // ---------- cross-session ガード (安いガード・review high) ----------

    private static string CurrentUserId()
    {
        return SupabaseService.Client?.Auth?.CurrentSession?.User?.Id ?? "";
    }

    // キューの持ち主を現在のユーザーに揃える。持ち主が違う/不明なのに pending が残っていれば、
    // それは別アカウント (or 旧データ) の記録なのでローカルから破棄してから持ち主を更新する。
    // → 別アカウント B のセッションで A の pending が replay される事故を防ぐ。
    // 設計メモ: ログアウト単体ではクリアしない (持ち主は A のまま) ため「A ログアウト → A 再ログイン」は
    //   持ち主一致で pending 保持＆通常同期され無駄に捨てない。「B が別アカウントでログイン」のときだけ
    //   クリアされる。cross-session replay 自体が起きないので invalid_habit の terminal 扱いは現状維持でよい。
    //   A の未同期分を残して A 再同期まで保持するフル対応は owner-scoped follow-up (DECISIONS §4.13)。
    private void ReconcileQueueOwner(string currentUserId)
    {
        if (string.IsNullOrEmpty(currentUserId)) return; // セッション無し → 触らない
        string owner = PlayerPrefs.GetString(QueueOwnerPrefKey, "");
        if (owner == currentUserId) return; // 同一の持ち主 → キュー保持
        if (SqliteService.HasPending())
        {
            Debug.Log($"[HabitSync] queue owner changed ('{owner}' -> '{currentUserId}'); clearing stale pending");
            SqliteService.ClearAll();
        }
        PlayerPrefs.SetString(QueueOwnerPrefKey, currentUserId);
        PlayerPrefs.Save();
    }
}
