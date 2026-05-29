// CreatureModel: creatures テーブルの Postgrest POCO (M1Tester から共通化移設).
// stage / total_growth_points はクライアント UPDATE 不可 (列 GRANT, RPC 専用). 表示にのみ使用.

using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("creatures")]
public class CreatureModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")] public Guid FamilyId { get; set; }
    [Column("member_id")] public Guid MemberId { get; set; }
    [Column("name")]      public string Name { get; set; }
    [Column("stage")]     public string Stage { get; set; }      // 'egg'/'baby'/'child'/'grown'
    [Column("total_growth_points")] public int TotalGrowthPoints { get; set; }
}
