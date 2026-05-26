// SupabaseService: Supabase クライアントの単一初期化 + 静的保持 (static class)

using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

public static class SupabaseService
{
    public static Supabase.Client Client { get; private set; }
    public static bool IsInitialized { get; private set; }

    public static async UniTask InitializeAsync()
    {
        if (IsInitialized) return;

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
        IsInitialized = true;
    }
}
