// SqliteService: ローカル送信キュー pending_logs の SQLite 永続化 (M5 / 計画 §5.4.2, :691).
//
// 静的サービス (SupabaseService と同型)。Application.persistentDataPath 配下に DB を作り、
// HabitSyncService / HomePanel から使う。EnsureInitialized() で遅延初期化するため呼出順に
// 依存しない。ネイティブ SQLite が使えない環境では Available=false になり、HabitSyncService は
// キュー無しの直接送信にフォールバックする (アプリは止めない)。
//
// 制約 (§5.4.2):
//   - UNIQUE(habit_id) WHERE status='pending'  → 同一 habit に同時 pending は 1 件のみ
//   - 全 habit 合計の pending 上限 50 件 (キュー溢れ対策)
//   partial UNIQUE は sqlite-net 属性で表現できないため CreateTable 後に raw SQL で作成する。
//
// 時刻はすべて ServerClock 由来の推定サーバー時刻 (壁時計非依存)。CreatedServerUtc は ISO "o"
// (UTC・固定オフセット "+00:00") で保存するため、文字列の辞書順 = 時系列順 (prune の範囲比較に使用)。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SQLite;
using UnityEngine;

public static class SqliteService
{
    public const int CooldownMinutes = 10;
    public const int MaxPending = 50;
    private const string DbFileName = "kids_habit.db";

    // 非 pending 行をどれだけ保持してから掃除するか (窓 10 分より十分長く取る)。
    private static readonly TimeSpan PruneAge = TimeSpan.FromHours(1);

    private static SQLiteConnection _db;
    private static bool _initTried;

    public static bool Available { get; private set; }

    // 初期化失敗時の原因 (型 + メッセージ)。fallback ログ等の診断用。成功時は null。
    public static string InitError { get; private set; }

    // 検証用 (review finding): prechecks 通過後に _db.Insert が失敗するケースを模擬する。
    // true の間 TryEnqueue の insert を擬似失敗させ Failed を返す (= 成功演出が出ないことの確認用)。
    // 既定 false・UI 無し・本番経路に影響しない。
    public static bool DebugForceInsertFailure = false;

    public static void EnsureInitialized()
    {
        if (_initTried) return;
        _initTried = true;
        // 必ず「開始」を出す = EnsureInitialized が走ったことの確証 (握りつぶし誤認の防止)。
        Debug.Log($"[SqliteService] init begin platform={Application.platform}");
        try
        {
            // e_sqlite3 プロバイダ登録 (bundle_green)。ここで対象 ABI のネイティブ
            // libe_sqlite3.so を P/Invoke するため、未同梱だと以降で DllNotFoundException 等になる。
            SQLitePCL.Batteries_V2.Init();
            var path = Path.Combine(Application.persistentDataPath, DbFileName);
            _db = new SQLiteConnection(path);
            _db.CreateTable<PendingLog>();
            // 同一 habit の同時 pending を 1 件に強制する partial UNIQUE index (§5.4.2)。
            _db.Execute(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_pendinglog_habit_pending " +
                "ON PendingLog (HabitId) WHERE Status = 'pending';");
            Available = true;
            InitError = null;
            Debug.Log($"[SqliteService] init OK (offline queue enabled) at {path}");
        }
        catch (Exception ex)
        {
            Available = false;
            InitError = ex.GetType().Name + ": " + ex.Message;
            // 例外の型・メッセージ・スタックを必ず残す。原因の多くは Android のネイティブ
            // libe_sqlite3.so が対象 ABI で APK に同梱されていない (DllNotFoundException 等)。
            // 同梱先: Assets/Plugins/Android/libs/<abi>/libe_sqlite3.so (arm64-v8a 必須)。
            Debug.LogError(
                "[SqliteService] init FAILED — offline queue disabled (direct send fallback). " +
                "原因の多くは Android ネイティブ libe_sqlite3.so の未同梱/ABI 不一致。 " +
                $"InitError={InitError}\n{ex}");
        }
    }

    // 押下時の連打/重複防止判定 (§5.4.2 / §5.4.3)。
    //   - 同一 habit に未送信 pending がある → blocked (新規 pending を作らない)
    //   - 10 分窓内に synced / rejected / (時刻付き) pending がある → blocked (クールダウン中)
    // cooldownUntil は窓終端 (大人ホームの残時間表示用。pending のみで時刻不明なら null)。
    public static bool HasBlockingEntry(string habitId, DateTimeOffset nowUtc, out DateTimeOffset? cooldownUntil)
    {
        cooldownUntil = null;
        EnsureInitialized();
        if (!Available) return false;

        bool blocked = false;
        var threshold = nowUtc - TimeSpan.FromMinutes(CooldownMinutes);
        try
        {
            var rows = _db.Query<PendingLog>(
                "SELECT * FROM PendingLog WHERE HabitId = ?", habitId);
            foreach (var r in rows)
            {
                bool relevant = r.Status == PendingLog.StatusPending
                    || r.Status == PendingLog.StatusSynced
                    || r.Status == PendingLog.StatusRejected;
                if (!relevant) continue;

                if (r.Status == PendingLog.StatusPending)
                {
                    blocked = true; // 未送信が残る間は新規を作らない (1 habit 1 pending)
                }
                if (TryParseUtc(r.CreatedServerUtc, out var created) && created > threshold)
                {
                    blocked = true;
                    var until = created + TimeSpan.FromMinutes(CooldownMinutes);
                    if (cooldownUntil == null || until > cooldownUntil.Value) cooldownUntil = until;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SqliteService] HasBlockingEntry failed: {ex.Message}");
            return false;
        }
        return blocked;
    }

    // 大人ホーム表示用: 当該 habit のクールダウン窓終端 (無ければ null)。
    public static DateTimeOffset? GetCooldownUntil(string habitId, DateTimeOffset nowUtc)
    {
        HasBlockingEntry(habitId, nowUtc, out var until);
        return until;
    }

    // pending 投入結果。呼び出し側はこれで「成功演出を出してよいか」「正直に弾くか」を判断する。
    public enum EnqueueResult
    {
        Enqueued,       // 新規 pending を保存した
        AlreadyPending, // 同一 habit に未送信 pending が既にある (UNIQUE 制約 / pending 既存・データ損失ではない)
        CapReached,     // 全体 50 件上限に到達 = 保存できない (進捗が失われるので成功を偽らない)
        Unavailable,    // SQLite 不可 (init 失敗)
        Failed,         // prechecks 通過後の insert/storage 失敗 (DB full/locked/corrupt/schema 等・保存できていない)
    }

    // 新規 pending を投入する (§5.4.2: 1 habit 1 pending + 全体 50 件上限)。
    // 上限時の方針 = reject-newest: 最古を破棄 (eviction) せず新規を弾く。eviction は確定済みの古い
    // 進捗を失うため採らない。CapReached のとき呼び出し側は成功演出も「あとで送る」表示も出さず正直に弾く。
    public static EnqueueResult TryEnqueue(string habitId, string memberId, string clientEventId, DateTimeOffset? createdUtc)
    {
        EnsureInitialized();
        if (!Available) return EnqueueResult.Unavailable;
        try
        {
            int totalPending = _db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM PendingLog WHERE Status = 'pending'");
            if (totalPending >= MaxPending)
            {
                Debug.LogWarning($"[SqliteService] pending cap reached ({totalPending}/{MaxPending}); reject newest (no eviction)");
                return EnqueueResult.CapReached;
            }
            int habitPending = _db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM PendingLog WHERE HabitId = ? AND Status = 'pending'", habitId);
            if (habitPending > 0)
            {
                Debug.Log($"[SqliteService] habit already has pending; skip enqueue habit={habitId}");
                return EnqueueResult.AlreadyPending;
            }

            string iso = createdUtc.HasValue
                ? createdUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : "";
            if (DebugForceInsertFailure)
            {
                // 検証用: prechecks 通過後に保存が失敗するケースを模擬 (review finding の確認)。
                throw new Exception("DebugForceInsertFailure (test): simulated storage write failure after prechecks");
            }
            _db.Insert(new PendingLog
            {
                HabitId = habitId,
                MemberId = memberId,
                ClientEventId = clientEventId,
                Status = PendingLog.StatusPending,
                CreatedServerUtc = iso,
                UpdatedServerUtc = iso,
            });
            return EnqueueResult.Enqueued;
        }
        catch (SQLiteException ex) when (ex.Result == SQLite3.Result.Constraint)
        {
            // partial UNIQUE 制約違反のみ = 同一 habit に pending が既にある (並行 insert 等・データ損失でない)。
            Debug.LogWarning($"[SqliteService] enqueue unique-constraint -> already pending: {ex.Message}");
            return EnqueueResult.AlreadyPending;
        }
        catch (Exception ex)
        {
            // prechecks 通過後の insert/query/storage 実失敗 (DB full/locked/corrupt/schema 不一致等)。
            // 保存できていない → AlreadyPending に丸めず Failed を返す (呼び出し側が成功演出を抑止し honest 通知)。
            Debug.LogError($"[SqliteService] enqueue FAILED (not persisted): {ex.GetType().Name}: {ex.Message}");
            return EnqueueResult.Failed;
        }
    }

    public static List<PendingLog> GetPending()
    {
        EnsureInitialized();
        if (!Available) return new List<PendingLog>();
        try
        {
            return _db.Query<PendingLog>(
                "SELECT * FROM PendingLog WHERE Status = 'pending' ORDER BY Id ASC");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SqliteService] GetPending failed: {ex.Message}");
            return new List<PendingLog>();
        }
    }

    // 送信待ち件数 (finding 2: オンライン継続中のバックオフ再送の判定 / flush 後のバックオフ調整用)。
    public static int PendingCount()
    {
        EnsureInitialized();
        if (!Available) return 0;
        try
        {
            return _db.ExecuteScalar<int>("SELECT COUNT(*) FROM PendingLog WHERE Status = 'pending'");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SqliteService] PendingCount failed: {ex.Message}");
            return 0;
        }
    }

    public static bool HasPending() => PendingCount() > 0;

    public static void MarkSynced(int id, string updatedUtcIso) => SetStatus(id, PendingLog.StatusSynced, updatedUtcIso);
    public static void MarkRejected(int id, string updatedUtcIso) => SetStatus(id, PendingLog.StatusRejected, updatedUtcIso);
    public static void MarkInvalid(int id, string updatedUtcIso) => SetStatus(id, PendingLog.StatusInvalid, updatedUtcIso);

    private static void SetStatus(int id, string status, string updatedUtcIso)
    {
        EnsureInitialized();
        if (!Available) return;
        try
        {
            _db.Execute("UPDATE PendingLog SET Status = ?, UpdatedServerUtc = ? WHERE Id = ?",
                status, updatedUtcIso ?? "", id);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SqliteService] SetStatus failed id={id}: {ex.Message}");
        }
    }

    // 削除予約 / 認可失効時に全ローカルキューを破棄する (§5.5 deletion_pending / not_authorized)。
    public static void ClearAll()
    {
        EnsureInitialized();
        if (!Available) return;
        try { _db.Execute("DELETE FROM PendingLog"); }
        catch (Exception ex) { Debug.LogWarning($"[SqliteService] ClearAll failed: {ex.Message}"); }
    }

    // 非 pending の古い行を掃除してテーブルを小さく保つ。nowUtc 不明時は何もしない。
    public static void Prune(DateTimeOffset? nowUtc)
    {
        EnsureInitialized();
        if (!Available || !nowUtc.HasValue) return;
        try
        {
            string cutoff = (nowUtc.Value - PruneAge).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            _db.Execute(
                "DELETE FROM PendingLog WHERE Status <> 'pending' AND CreatedServerUtc <> '' AND CreatedServerUtc < ?",
                cutoff);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SqliteService] Prune failed: {ex.Message}");
        }
    }

    private static bool TryParseUtc(string iso, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrEmpty(iso)) return false;
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v))
        {
            value = v.ToUniversalTime();
            return true;
        }
        return false;
    }
}
