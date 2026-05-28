using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class CreatureDisplay : MonoBehaviour
{
    [SerializeField] private string stage = "egg";

    private GameObject _currentInstance;
    private AsyncOperationHandle<GameObject> _currentHandle;
    private bool _hasCurrentHandle;

    private void Start()
    {
        LoadStage(stage, fireStageUpTrigger: false, useGrownTrigger: false);
    }

    public void SetStage(string newStage)
    {
        if (string.IsNullOrEmpty(newStage))
        {
            Debug.LogError("[CreatureDisplay] SetStage called with null/empty newStage");
            return;
        }
        if (newStage == stage)
        {
            Debug.Log($"[CreatureDisplay] SetStage: already at stage='{stage}', no change");
            return;
        }
        Debug.Log($"[CreatureDisplay] SetStage: {stage} -> {newStage}");
        // Step 7 中間案ルール: 新 stage が 'grown' (=昇格前 child) のときのみ
        // Stage_Up_To_Grown Trigger、それ以外 (egg→baby / baby→child) は共通 Stage_Up Trigger
        bool useGrownTrigger = (newStage == "grown");
        stage = newStage;
        LoadStage(stage, fireStageUpTrigger: true, useGrownTrigger: useGrownTrigger);
    }

    private void LoadStage(string newStage, bool fireStageUpTrigger, bool useGrownTrigger)
    {
        // 前 stage の AssetHandle を Release してから新 stage を Load (leak 防止)
        if (_hasCurrentHandle && _currentHandle.IsValid())
        {
            Addressables.Release(_currentHandle);
        }
        _hasCurrentHandle = false;

        string key = $"creature_v1_{newStage}";
        _currentHandle = Addressables.LoadAssetAsync<GameObject>(key);
        _hasCurrentHandle = true;
        _currentHandle.Completed += handle =>
            OnPrefabLoaded(handle, fireStageUpTrigger, useGrownTrigger);
    }

    private void OnPrefabLoaded(AsyncOperationHandle<GameObject> handle,
                                bool fireStageUpTrigger, bool useGrownTrigger)
    {
        if (handle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError(
                $"[CreatureDisplay] Failed to load creature prefab for stage='{stage}': " +
                $"{handle.OperationException}");
            return;
        }

        if (_currentInstance != null)
        {
            Destroy(_currentInstance);
            _currentInstance = null;
        }

        _currentInstance = Instantiate(handle.Result, transform);
        _currentInstance.transform.localPosition = Vector3.zero;
        _currentInstance.transform.localRotation = Quaternion.identity;
        _currentInstance.name = $"Creature_{stage}";

        if (fireStageUpTrigger)
        {
            // 設計判断 B1: Trigger は新 Prefab の Animator に対して発火する。
            // 新 Prefab が一瞬で出現 → 直後に Stage_Up クリップ (scale 一時拡大 + 戻し) を再生。
            var animator = _currentInstance.GetComponent<Animator>();
            if (animator != null)
            {
                string triggerName = useGrownTrigger ? "Stage_Up_To_Grown" : "Stage_Up";
                animator.SetTrigger(triggerName);
                Debug.Log(
                    $"[CreatureDisplay] Animator trigger fired: {triggerName} (new stage='{stage}')");
            }
            else
            {
                Debug.LogWarning(
                    $"[CreatureDisplay] Creature_{stage} prefab has no Animator component; " +
                    "stage-up animation skipped (Step 7 の設定さん側 Editor 作業が未完了の可能性)");
            }
        }
    }

    private void OnDestroy()
    {
        if (_currentInstance != null)
        {
            Destroy(_currentInstance);
            _currentInstance = null;
        }
        if (_hasCurrentHandle && _currentHandle.IsValid())
        {
            Addressables.Release(_currentHandle);
        }
    }
}
