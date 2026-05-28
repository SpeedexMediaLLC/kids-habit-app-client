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
        Debug.Log($"[SessionPersistence] Save begin path={FilePath} at_len={accessToken?.Length ?? -1} rt_len={refreshToken?.Length ?? -1}");
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            Debug.Log("[SessionPersistence] Save skipped: empty tokens");
            return;
        }
        try
        {
            var json = JsonUtility.ToJson(new Snapshot
            {
                access_token = accessToken,
                refresh_token = refreshToken,
            });
            File.WriteAllText(FilePath, json);
            Debug.Log("[SessionPersistence] Save ok");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionPersistence] Save failed: {ex.ToString()}");
        }
    }

    public static (string AccessToken, string RefreshToken)? Load()
    {
        Debug.Log($"[SessionPersistence] Load begin path={FilePath}");
        try
        {
            if (!File.Exists(FilePath))
            {
                Debug.Log("[SessionPersistence] Load: file_not_exists");
                return null;
            }
            var json = File.ReadAllText(FilePath);
            var snap = JsonUtility.FromJson<Snapshot>(json);
            if (snap == null
                || string.IsNullOrEmpty(snap.access_token)
                || string.IsNullOrEmpty(snap.refresh_token))
            {
                Debug.Log("[SessionPersistence] Load: file_exists_but_empty_or_invalid");
                return null;
            }
            Debug.Log($"[SessionPersistence] Load ok at_len={snap.access_token.Length} rt_len={snap.refresh_token.Length}");
            return (snap.access_token, snap.refresh_token);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionPersistence] Load failed: {ex.ToString()}");
            return null;
        }
    }

    public static void Clear()
    {
        Debug.Log($"[SessionPersistence] Clear begin path={FilePath}");
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                Debug.Log("[SessionPersistence] Clear ok (file deleted)");
            }
            else
            {
                Debug.Log("[SessionPersistence] Clear: file_not_exists (noop)");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionPersistence] Clear failed: {ex.ToString()}");
        }
    }
}
