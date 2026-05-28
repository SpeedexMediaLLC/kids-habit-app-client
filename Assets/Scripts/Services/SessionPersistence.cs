// SessionPersistence: Supabase の access_token / refresh_token を
// Application.persistentDataPath の単一 JSON ファイルに保存し、Editor の
// Play を止めた後 / シーン切替の後でも再起動時に SetSession で復元できる
// ようにする (M2 Step 6 でシーン跨ぎの動作確認を可能にするための土台)。
//
// 保存先: Application.persistentDataPath/supabase_session.json
// セキュリティ: 端末のローカルファイル。Phase 1 の身内テスト用。
// M7 ストア提出前に OS 標準のセキュアストレージ (Keychain / KeyStore) への
// 切替を再評価する想定。

using System;
using System.IO;
using UnityEngine;

public static class SessionPersistence
{
    private const string FileName = "supabase_session.json";

    private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

    [Serializable]
    private class Snapshot
    {
        public string access_token;
        public string refresh_token;
    }

    public static void Save(string accessToken, string refreshToken)
    {
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken)) return;
        try
        {
            var json = JsonUtility.ToJson(new Snapshot
            {
                access_token = accessToken,
                refresh_token = refreshToken,
            });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionPersistence] Save failed: {ex.Message}");
        }
    }

    public static (string AccessToken, string RefreshToken)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            var snap = JsonUtility.FromJson<Snapshot>(json);
            if (snap == null
                || string.IsNullOrEmpty(snap.access_token)
                || string.IsNullOrEmpty(snap.refresh_token))
            {
                return null;
            }
            return (snap.access_token, snap.refresh_token);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionPersistence] Load failed: {ex.Message}");
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionPersistence] Clear failed: {ex.Message}");
        }
    }
}
