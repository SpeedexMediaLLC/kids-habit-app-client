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
        // M4 S2: 大人モード復帰はパスコード照合を通す. 照合ゲート (AppFlowController) を開き,
        // 照合成功時のみ PasscodeGatePanel が SwitchToAdult を実行する.
        // AppFlowController が無い検証用シーン (TestM1Scene 等, 家族/パスコード不在) では
        // 従来どおり即時遷移にフォールバックする.
        var flow = AppFlowController.Instance;
        if (flow != null)
        {
            flow.RequestAdultUnlock();
            return;
        }
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
