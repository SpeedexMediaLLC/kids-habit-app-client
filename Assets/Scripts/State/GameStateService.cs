using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class GameStateService : MonoBehaviour
{
    public enum GameMode
    {
        Adult,
        Child,
    }

    public static GameStateService Instance { get; private set; }

    public event Action<GameMode> ModeChanged;

    public GameMode CurrentMode { get; private set; } = GameMode.Adult;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning(
                "[GameStateService] Duplicate instance detected; destroying this one. " +
                "MainScene に GameState GameObject は 1 つだけ配置する想定。");
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnEnable()
    {
        Application.wantsToQuit += OnApplicationWantsToQuit;
    }

    private void OnDisable()
    {
        Application.wantsToQuit -= OnApplicationWantsToQuit;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void SwitchToChild()
    {
        if (CurrentMode == GameMode.Child)
        {
            return;
        }
        CurrentMode = GameMode.Child;
        Debug.Log("[GameStateService] mode -> Child");
        ModeChanged?.Invoke(CurrentMode);
    }

    public void SwitchToAdult()
    {
        // M2 範囲: パスコード照合なし、状態切替のみ (M4 でパスコード本体実装)
        if (CurrentMode == GameMode.Adult)
        {
            return;
        }
        CurrentMode = GameMode.Adult;
        Debug.Log("[GameStateService] mode -> Adult");
        ModeChanged?.Invoke(CurrentMode);
    }

    private void Update()
    {
        // 方式 A: Esc/Android-back を新 Input System で捕捉。
        // Active Input Handling = 新 Input System のみなので旧 Input API は使用不可。
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }
        if (keyboard.escapeKey.wasPressedThisFrame && CurrentMode == GameMode.Child)
        {
            Debug.Log("[GameStateService] Esc/back pressed in Child mode - ignored");
        }
    }

    private bool OnApplicationWantsToQuit()
    {
        // 方式 C: Android back で Application.Quit が走った場合の二重防御。
        // 子供モード中はキャンセル、大人モードは通常 quit を許可。
        if (CurrentMode == GameMode.Child)
        {
            Debug.Log("[GameStateService] Application quit blocked (Child mode)");
            return false;
        }
        return true;
    }
}
