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

    // 初期化は AsyncLazy で表現する。AsyncLazy は内部に専用の UniTaskCompletionSource を 1 つ持ち、
    // DoInitializeAsync の完了をそこへ中継するため、各呼び出し元が await するのは毎回その
    // completionSource.Task (正規の TCS) になる。これにより「初期化を一度だけ実行」しつつ
    // 「完了前・完了後を問わず複数経路から安全に await」できる。
    //
    // 旧 Unitask.Preserve() 方式では NG だった理由:
    //   Preserve() の MemoizeSource は source 完了「後」の再 await は救うが、完了「前」に複数経路が
    //   同時に await すると内側 source (async ステートマシンの UniTaskCompletionSourceCore) へ
    //   continuation を二重登録し "Already continuation registered, can not await twice" を投げる。
    //   起動時は BeforeSceneLoad (AutoInit) と AfterSceneLoad (AppFlowController) が両方とも
    //   完了前に await するため必ず衝突していた。
    private static readonly AsyncLazy _initLazy = new AsyncLazy(DoInitializeAsync);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInitOnAppStart()
    {
        InitializeAsync().Forget();
    }

    public static UniTask InitializeAsync()
    {
        if (IsInitialized)
        {
            Debug.Log("[SupabaseService] InitializeAsync called: already initialized (immediate return)");
            return UniTask.CompletedTask;
        }
        // AsyncLazy.Task は初回アクセスで factory を一度だけ起動し、以降は同じ
        // completionSource を返す。複数経路からの同時 await も安全。
        Debug.Log("[SupabaseService] InitializeAsync called: awaiting AsyncLazy init task");
        return _initLazy.Task;
    }

    private static async UniTask DoInitializeAsync()
    {
        try
        {
            Debug.Log("[SupabaseService] init begin");

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
            Debug.Log($"[SupabaseService] config loaded url={config.Url}");

            var options = new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = false,
                AutoRefreshToken = true,
            };

            Client = new Supabase.Client(config.Url, config.AnonKey, options);
            Debug.Log("[SupabaseService] client created");

            Debug.Log("[SupabaseService] Client.InitializeAsync begin");
            await Client.InitializeAsync();
            Debug.Log("[SupabaseService] Client.InitializeAsync done");

            Client.Auth.AddStateChangedListener(OnAuthStateChanged);
            Debug.Log("[SupabaseService] auth state listener attached");

            var saved = SessionPersistence.Load();
            Debug.Log($"[SupabaseService] session restore: file_exists={saved.HasValue}");
            if (saved.HasValue)
            {
                try
                {
                    Debug.Log("[SupabaseService] SetSession begin");
                    var session = await Client.Auth.SetSession(
                        saved.Value.AccessToken, saved.Value.RefreshToken);
                    Debug.Log($"[SupabaseService] SetSession ok / session.user.id={session?.User?.Id}");
                }
                catch (Exception ex)
                {
                    // 捕捉済み・回復可能な想定内事象 (保存 refresh token が失効/使用済み 等) のため
                    // Error ではなく Warning。クリアして以降は未ログイン扱い → 再ログインで解消する。
                    // ここで例外を握りつぶすので初期化自体は継続し IsInitialized=true で完了する
                    // (AppFlowController は CurrentSession=null を見て Login へクリーンに分岐)。
                    Debug.LogWarning(
                        $"[SupabaseService] saved session restore failed; cleared, re-login required: {ex.ToString()}");
                    SessionPersistence.Clear();
                }
            }

            IsInitialized = true;
            Debug.Log("[SupabaseService] init complete");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SupabaseService] init failed: {ex.ToString()}");
            throw;
        }
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
