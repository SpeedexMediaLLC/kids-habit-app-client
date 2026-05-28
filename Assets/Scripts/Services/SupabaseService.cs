// SupabaseService: Supabase クライアントの単一初期化 + 静的保持 (static class)
//
// シーン非依存:
//   [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)] で BeforeSceneLoad 段階で
//   InitializeAsync を fire-and-forget。MainScene / TestM1Scene どちらから起動
//   しても自動で Client を組み立てる。
//
// セッション復元:
//   起動時に SessionPersistence.Load → 残っていれば Client.Auth.SetSession で
//   復元 (SetSession が SignedIn を発火 → Postgrest の auth header に
//   access_token が載る)。Editor の Play 跨ぎや MainScene 単独起動でも
//   record_habit RPC が authenticated ロールで呼べる。
//
// 永続化フック:
//   Auth StateChanged を購読し、SignedIn / TokenRefreshed で
//   SessionPersistence.Save、SignedOut で Clear。AuthService.SignIn / SignOut /
//   RefreshSession を経由する既存呼び出しは全て自動で永続化対象になる。

using System;
using Cysharp.Threading.Tasks;
using Supabase.Gotrue;
using Supabase.Gotrue.Interfaces;
using UnityEngine;

public static class SupabaseService
{
    public static Supabase.Client Client { get; private set; }
    public static bool IsInitialized { get; private set; }

    private static UniTask? _initTask;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInitOnAppStart()
    {
        InitializeAsync().Forget();
    }

    public static UniTask InitializeAsync()
    {
        if (IsInitialized) return UniTask.CompletedTask;
        if (_initTask.HasValue) return _initTask.Value;
        _initTask = DoInitializeAsync();
        return _initTask.Value;
    }

    private static async UniTask DoInitializeAsync()
    {
        var config = Resources.Load<SupabaseConfig>("SupabaseConfig");
        if (config == null)
        {
            throw new InvalidOperationException(
                "SupabaseConfig.asset not found under Assets/Resources/. Step 5 で作成済みのはず.");
        }
        if (string.IsNullOrEmpty(config.Url) || string.IsNullOrEmpty(config.AnonKey))
        {
            throw new InvalidOperationException(
                "SupabaseConfig.Url / AnonKey が空. Resources/SupabaseConfig.asset を確認.");
        }

        var options = new Supabase.SupabaseOptions
        {
            AutoConnectRealtime = false,
            AutoRefreshToken = true,
        };

        Client = new Supabase.Client(config.Url, config.AnonKey, options);
        await Client.InitializeAsync();

        Client.Auth.AddStateChangedListener(OnAuthStateChanged);

        var saved = SessionPersistence.Load();
        if (saved.HasValue)
        {
            try
            {
                await Client.Auth.SetSession(saved.Value.AccessToken, saved.Value.RefreshToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[SupabaseService] saved session restore failed (cleared): {ex.Message}");
                SessionPersistence.Clear();
            }
        }

        IsInitialized = true;
    }

    private static void OnAuthStateChanged(IGotrueClient<User, Session> sender,
                                           Constants.AuthState state)
    {
        switch (state)
        {
            case Constants.AuthState.SignedIn:
            case Constants.AuthState.TokenRefreshed:
                var session = sender?.CurrentSession;
                if (session != null
                    && !string.IsNullOrEmpty(session.AccessToken)
                    && !string.IsNullOrEmpty(session.RefreshToken))
                {
                    SessionPersistence.Save(session.AccessToken, session.RefreshToken);
                }
                break;
            case Constants.AuthState.SignedOut:
                SessionPersistence.Clear();
                break;
        }
    }
}
