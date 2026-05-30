// ServerClock: ローカル時計依存を排除したサーバー推定時刻 (M5 / 計画 §5.4.1, :694).
//
// 設計 (なぜ壁時計を使わないか):
//   端末の壁時計 (DateTime.Now / UtcNow) は一切使わない。使うと時計を巻き戻して
//   連打クールダウンを回避できてしまうため (= 完了条件 :698「端末時計を巻き戻しても
//   ローカルクールダウンが回避できない」)。代わりに:
//     - サーバー応答の HTTP `Date` ヘッダで得たサーバー時刻を server_time_anchor として保持
//     - 同時に Time.realtimeSinceStartup (プロセス開始からの単調増加秒・壁時計非依存) を anchor に保持
//     - 推定サーバー時刻 = server_time_anchor + (realtimeSinceStartup - uptime_anchor)
//   これによりプロセス内では壁時計の変更に影響されない単調増加の時刻が得られる。
//
//   サーバー時刻の取得元は専用 RPC ではなく既存応答の `Date` ヘッダ (server 無改修 =
//   M5 はクライアント実装のみ)。全 RPC (ApiService) と起動時の Postgrest GET
//   (AppFlowController / HomePanel) から FeedFromHttp する。
//
// 永続化 (オフライン再起動フォールバック・§5.4.1「オフラインで再起動した場合は最後の
//   server_time_anchor を使用」):
//   最後に得たサーバー時刻を persistentDataPath の JSON に保存 (SessionPersistence と同方式)。
//   次回起動でまだ新しい live anchor を得られていない間は、保存値を realtimeSinceStartup
//   0 起点として単調外挿する (壁時計は使わない)。アプリ閉局中の経過時間はカウントしない =
//   クールダウンを「長め」に見積もる安全側 (回避不可を優先・閉局で短縮されない)。

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using UnityEngine;

public static class ServerClock
{
    // live anchor (現プロセスで Date ヘッダから得た最新のサーバー時刻と、その時点の uptime)
    private static bool _hasLiveAnchor;
    private static DateTimeOffset _anchorUtc;
    private static float _anchorUptime;

    // 永続化 (オフライン再起動フォールバック用の最終既知サーバー時刻)
    private static bool _persistLoaded;
    private static DateTimeOffset? _persistedUtc;
    private static DateTimeOffset _lastDiskWriteUtc = DateTimeOffset.MinValue;

    private const string FileName = "server_clock_anchor.json";
    private static readonly TimeSpan PersistThrottle = TimeSpan.FromSeconds(30);

    private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

    [Serializable]
    private class Snapshot { public string server_utc; }

    // 時刻基準 (live or 永続) が一つでもあるか。
    public static bool HasAnchor => _hasLiveAnchor || PersistedUtc().HasValue;

    // 任意の HTTP レスポンスの Date ヘッダからサーバー時刻を供給する。
    public static void FeedFromHttp(HttpResponseMessage resp)
    {
        var date = resp?.Headers?.Date;
        if (date.HasValue)
        {
            FeedUtc(date.Value.ToUniversalTime());
        }
    }

    // サーバー時刻 (UTC) を供給。live anchor を更新し、必要なら永続化 (throttle)。
    public static void FeedUtc(DateTimeOffset serverUtc)
    {
        _anchorUtc = serverUtc;
        _anchorUptime = Time.realtimeSinceStartup;
        _hasLiveAnchor = true;

        if (!_persistLoaded) LoadPersist();
        // 30 秒以上サーバー時刻が進んだとき (または初回) だけディスク書込み = RPC ごとの I/O を抑える。
        if (!_persistedUtc.HasValue || serverUtc - _lastDiskWriteUtc >= PersistThrottle)
        {
            _persistedUtc = serverUtc;
            _lastDiskWriteUtc = serverUtc;
            SavePersist(serverUtc);
        }
        else if (serverUtc > _persistedUtc.Value)
        {
            _persistedUtc = serverUtc; // メモリ上は最新化 (ディスクは throttle)
        }
    }

    // 推定サーバー時刻 (UTC) を返す。基準が無ければ false (呼び出し側は窓判定を行わない)。
    public static bool TryNowUtc(out DateTimeOffset nowUtc)
    {
        if (_hasLiveAnchor)
        {
            float delta = Time.realtimeSinceStartup - _anchorUptime;
            if (delta < 0f) delta = 0f; // 単調ガード
            nowUtc = _anchorUtc + TimeSpan.FromSeconds(delta);
            return true;
        }
        var p = PersistedUtc();
        if (p.HasValue)
        {
            // live anchor 未取得 (例: オフライン再起動) → 最終既知サーバー時刻を uptime 0 起点で外挿。
            // 壁時計は使わない。閉局中の経過は数えない (安全側 = クールダウンが短縮されない)。
            float up = Time.realtimeSinceStartup;
            if (up < 0f) up = 0f;
            nowUtc = p.Value + TimeSpan.FromSeconds(up);
            return true;
        }
        nowUtc = default;
        return false;
    }

    private static DateTimeOffset? PersistedUtc()
    {
        if (!_persistLoaded) LoadPersist();
        return _persistedUtc;
    }

    private static void LoadPersist()
    {
        _persistLoaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var snap = JsonUtility.FromJson<Snapshot>(json);
            if (snap != null && !string.IsNullOrEmpty(snap.server_utc)
                && DateTimeOffset.TryParse(snap.server_utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var v))
            {
                _persistedUtc = v.ToUniversalTime();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ServerClock] load persist failed: {ex.Message}");
        }
    }

    private static void SavePersist(DateTimeOffset utc)
    {
        try
        {
            var json = JsonUtility.ToJson(new Snapshot
            {
                server_utc = utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ServerClock] save persist failed: {ex.Message}");
        }
    }
}
