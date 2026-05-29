// DeletionRequestModel: deletion_requests テーブルの Postgrest POCO (M4 S4, 削除予約の起動ルーティング用).
//
// deletion_requests は SELECT が deletion_pending=true 中も可 (RLS 0010:196-198 は family_id() 一致のみで
// deletion_pending を見ない. family_id() は JWT app_metadata.family_id 由来 0001:16-25 のため members が
// 予約中 RLS で不可視でも解決できる). 起動ルーティング (AppFlowController) と削除予約中画面
// (DeletionReservedPanel) はこの POCO を直 SELECT し,
//   - status=='pending' の行の有無で「削除予約中か」を判定 (members は予約中 0 件のため代替),
//   - scheduled_delete_at から残り日数を表示,
//   - id を cancel_account_deletion(p_deletion_request_id) に渡す.
// に使う. INSERT/UPDATE は RPC 経由のみ (request_account_deletion / cancel_account_deletion).
//
// scheduled_delete_at は timestamptz. DateTimeOffset で受ける (Kind 曖昧を避ける. 万一 Postgrest の
// 逆シリアライズが崩れる場合は DateTime + ToUniversalTime にフォールバック = 設定さん検証で確認).

using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("deletion_requests")]
public class DeletionRequestModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")]           public Guid FamilyId { get; set; }
    [Column("status")]              public string Status { get; set; }   // 'pending'/'cancelled'/'executed'
    [Column("scheduled_delete_at")] public DateTimeOffset ScheduledDeleteAt { get; set; }
}
