// HabitTemplateModel: habit_templates テーブルの Postgrest POCO (M4 S1, 習慣追加のテンプレ選択用).
//
// habit_templates は全家族共通の定番テンプレ (SCHEMA §3.3, 認証済みは全行 SELECT 可). クライアントは
// is_active=true のものを sort_order 昇順で一覧し, 選択時は add_habit に template_id を渡す.
// 強度はサーバが template の default_intensity を採用するため p_intensity=null で送る
// (仕様 v2 §8「定番テンプレ＝強度つき」, 0013_rpc_habit_management.sql の COALESCE).

using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("habit_templates")]
public class HabitTemplateModel : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("title")]             public string Title { get; set; }
    [Column("default_intensity")] public string DefaultIntensity { get; set; }  // 'small'/'medium'/'large'
    [Column("is_active")]         public bool IsActive { get; set; }
    [Column("sort_order")]        public int SortOrder { get; set; }
}
