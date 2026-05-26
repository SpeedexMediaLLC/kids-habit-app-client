// SupabaseConfig (ScriptableObject)
//
// Phase 1 (M1) Unity クライアントが Supabase に接続するための設定値を保持する.
// 値の実体は Assets/Resources/SupabaseConfig.asset に格納し, ランタイムでは
// Resources.Load<SupabaseConfig>("SupabaseConfig") で読み込む.
//
// === 鍵の扱い (重要) ===
//
// - AnonKey: RLS 保護を前提とした公開鍵. 本ファイル / .asset に格納して
//   client repo に含めてよい. クライアントバンドルに埋め込まれて配布される
//   ことを前提に Supabase が設計している鍵 (役割は JWT の role=anon).
// - service_role key: 絶対にここに置かない. サーバー操作専用で, server repo
//   の .env にのみ保持する. service_role が万一クライアントに露出すると
//   RLS バイパスでデータ全消去等が可能になる.
//
// === M7 でのキー体系移行 ===
//
// 現在は Supabase の legacy anon / service_role key 体系を使用. Supabase は
// 2026 年末で legacy 廃止予定で, 新しい publishable / secret key 体系へ
// 移行する (server repo docs/DECISIONS.md §4.2.1). M7 のストア提出前後で
// 本ファイル / .asset を新キーに差し替えること.

using UnityEngine;

[CreateAssetMenu(fileName = "SupabaseConfig", menuName = "kids-habit-app/Supabase Config")]
public class SupabaseConfig : ScriptableObject
{
    [Tooltip("Supabase project API endpoint (e.g. https://<project-ref>.supabase.co). Dashboard URL とは別物.")]
    public string Url;

    [Tooltip("Supabase anon key (JWT, role=anon). RLS 保護を前提とした公開鍵. service_role key は置かない.")]
    public string AnonKey;
}
