// HabitLogModel: habit_logs テーブルの Postgrest POCO (M3 サマリー集計用).
// created_at は timestamptz (UTC). サマリーの「1 日の最多達成回数」は JST 日付でグループ化する.

using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("habit_logs")]
public class HabitLogModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")] public Guid FamilyId { get; set; }
    [Column("member_id")] public Guid MemberId { get; set; }
    [Column("habit_id")]  public Guid HabitId { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
