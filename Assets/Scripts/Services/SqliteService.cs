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

    public static void EnsureInitialized()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            SQLitePCL.Batteries_V2.Init();
            var path = Path.Combine(Application.persistentDataPath, DbFileName);
            _db = new SQLiteConnection(path);
            _db.CreateTable<PendingLog>();
            // 同一 habit の同時 pending を 1 件に強制する partial UNIQUE index (§5.4.2)。
            _db.Execute(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_pendinglog_habit_pending " +
                "ON PendingLog (HabitId) WHERE Status = 'pending';");
            Available = true;
            Debug.Log($"[SqliteService] initialized at {path}");
        }
        catch (Exception ex)
        {
            Available = false;
            Debug.LogError(
                $"[SqliteService] init failed; offline queue disabled (direct send fallback): {ex}");
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

    // 新規 pending を投入する。1 habit 1 pending + 50 件上限を満たさなければ false。
    public static bool TryEnqueue(string habitId, string memberId, string clientEventId, DateTimeOffset? createdUtc)
    {
        EnsureInitialized();
        if (!Available) return false;
        try
        {
            int totalPending = _db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM PendingLog WHERE Status = 'pending'");
            if (totalPending >= MaxPending)
            {
                Debug.LogWarning($"[SqliteService] pending cap reached ({totalPending}/{MaxPending}); drop enqueue");
                return false;
            }
            int habitPending = _db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM PendingLog WHERE HabitId = ? AND Status = 'pending'", habitId);
            if (habitPending > 0)
            {
                Debug.Log($"[SqliteService] habit already has pending; skip enqueue habit={habitId}");
                return false;
            }

            string iso = createdUtc.HasValue
                ? createdUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : "";
            _db.Insert(new PendingLog
            {
                HabitId = habitId,
                MemberId = memberId,
                ClientEventId = clientEventId,
                Status = PendingLog.StatusPending,
                CreatedServerUtc = iso,
                UpdatedServerUtc = iso,
            });
            return true;
        }
        catch (Exception ex)
        {
            // partial UNIQUE index 違反 (同時 pending) 等は想定内 → false で握る。
            Debug.LogWarning($"[SqliteService] enqueue failed (likely unique constraint): {ex.Message}");
            return false;
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
