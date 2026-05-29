// HabitModel: habits テーブルの Postgrest POCO (M1Tester から共通化移設).

using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("habits")]
public class HabitModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")] public Guid FamilyId { get; set; }
    [Column("member_id")] public Guid MemberId { get; set; }
    [Column("title")]     public string Title { get; set; }
    [Column("intensity")] public string Intensity { get; set; }  // 'small'/'medium'/'large'
    [Column("is_active")] public bool IsActive { get; set; }
}
