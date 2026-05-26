// AuthService: SupabaseService.Client.Auth の UniTask ラッパ (static class)

using Cysharp.Threading.Tasks;
using Supabase.Gotrue;

public static class AuthService
{
    public static User CurrentUser => SupabaseService.Client.Auth.CurrentUser;
    public static Session CurrentSession => SupabaseService.Client.Auth.CurrentSession;

    public static async UniTask<Session> SignUpAsync(string email, string password)
    {
        return await SupabaseService.Client.Auth.SignUp(email, password);
    }

    public static async UniTask<Session> SignInAsync(string email, string password)
    {
        return await SupabaseService.Client.Auth.SignInWithPassword(email, password);
    }

    public static async UniTask SignOutAsync()
    {
        await SupabaseService.Client.Auth.SignOut();
    }

    public static async UniTask<Session> RefreshSessionAsync()
    {
        return await SupabaseService.Client.Auth.RefreshSession();
    }
}
