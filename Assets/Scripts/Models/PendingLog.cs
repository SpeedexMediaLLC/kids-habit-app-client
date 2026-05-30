// PendingLog: SQLite ローカルキュー pending_logs の行 (M5 / 計画 §5.4.2, :691).
//
// 頑張ったボタン押下を「ローカル保存 → 通信時に record_habit RPC へ送信」するための
// 送信待ち行。オフライン中もここに溜め、接続復活で順次送る。
//
// status:
//   pending  = 未送信 (送信キュー)。同一 habit に同時 1 件のみ (partial UNIQUE index で強制)。
//   synced   = server が recorded / duplicate を返し確定 (送信キューから外す。窓判定用に保持)。
//   rejected = server が cooldown_active を返した (10 分窓内重複。creature は次回起動で整合)。
//   invalid  = server が invalid_habit を返した / 行データ不正 (除外)。
//
// created_server_utc は押下時の ServerClock 推定サーバー時刻 (ISO 8601 "o" / UTC)。
// 10 分クールダウン窓判定に使う (壁時計非依存)。時刻基準が無い間は空文字 (窓判定対象外)。

using SQLite;

public class PendingLog
{
    public const string StatusPending = "pending";
    public const string StatusSynced = "synced";
    public const string StatusRejected = "rejected";
    public const string StatusInvalid = "invalid";

    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string HabitId { get; set; }

    public string MemberId { get; set; }

    // record_habit の冪等キー。再送でも同じ値を使う → server は UNIQUE(family_id, client_event_id)
    // 違反を duplicate として返すため、二重カウントが起きない (再送は常に安全)。
    [Unique]
    public string ClientEventId { get; set; }

    [Indexed]
    public string Status { get; set; }

    public string CreatedServerUtc { get; set; }

    public string UpdatedServerUtc { get; set; }
}
