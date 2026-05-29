// MemberModel: members テーブルの Postgrest POCO (M3 で共通化).
// 旧 AppFlowController.AppFlowMemberRow を統合. RLS で自家族 + deletion_pending=false にスコープ.

using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("members")]
public class MemberModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("family_id")] public Guid FamilyId { get; set; }
    [Column("role")]      public string Role { get; set; }       // 'parent' / 'child'
    [Column("nickname")]  public string Nickname { get; set; }
}
