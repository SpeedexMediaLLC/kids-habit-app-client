using UnityEngine;

public class ModeSwitcher : MonoBehaviour
{
    [SerializeField] private GameStateService gameStateService;
    [SerializeField] private GameObject toChildButton;
    [SerializeField] private GameObject toAdultButton;

    private void Start()
    {
        var service = ResolveService();
        if (service == null)
        {
            Debug.LogError(
                "[ModeSwitcher] GameStateService が見つからない。MainScene に GameState GameObject を配置し、" +
                "ModeSwitcher の Inspector に割当てるか、シーン内に存在することを確認してください。");
            return;
        }
        service.ModeChanged += OnModeChanged;
        ApplyVisibility(service.CurrentMode);
    }

    private void OnDestroy()
    {
        if (gameStateService != null)
        {
            gameStateService.ModeChanged -= OnModeChanged;
        }
    }

    public void OnSwitchToChild()
    {
        var service = ResolveService();
        if (service == null)
        {
            return;
        }
        service.SwitchToChild();
    }

    public void OnSwitchToAdult()
    {
        // M2 範囲: パスコード入力 UI は M4 で追加、ここでは即時遷移のみ
        var service = ResolveService();
        if (service == null)
        {
            return;
        }
        service.SwitchToAdult();
    }

    private GameStateService ResolveService()
    {
        if (gameStateService != null)
        {
            return gameStateService;
        }
        gameStateService = GameStateService.Instance;
        return gameStateService;
    }

    private void OnModeChanged(GameStateService.GameMode mode)
    {
        ApplyVisibility(mode);
    }

    private void ApplyVisibility(GameStateService.GameMode mode)
    {
        bool isAdult = (mode == GameStateService.GameMode.Adult);
        if (toChildButton != null)
        {
            toChildButton.SetActive(isAdult);
        }
        if (toAdultButton != null)
        {
            toAdultButton.SetActive(!isAdult);
        }
    }
}
