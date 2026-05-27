using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class CreatureDisplay : MonoBehaviour
{
    [SerializeField] private string stage = "egg";

    private GameObject _currentInstance;
    private AsyncOperationHandle<GameObject> _currentHandle;

    private void Start()
    {
        LoadStage(stage);
    }

    private void LoadStage(string newStage)
    {
        string key = $"creature_v1_{newStage}";
        _currentHandle = Addressables.LoadAssetAsync<GameObject>(key);
        _currentHandle.Completed += OnPrefabLoaded;
    }

    private void OnPrefabLoaded(AsyncOperationHandle<GameObject> handle)
    {
        if (handle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"[CreatureDisplay] Failed to load creature prefab for stage='{stage}': {handle.OperationException}");
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
    }

    private void OnDestroy()
    {
        if (_currentInstance != null)
        {
            Destroy(_currentInstance);
            _currentInstance = null;
        }
        if (_currentHandle.IsValid())
        {
            Addressables.Release(_currentHandle);
        }
    }
}
