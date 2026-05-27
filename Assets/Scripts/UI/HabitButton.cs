using UnityEngine;

public class HabitButton : MonoBehaviour
{
    [SerializeField] private PressEffectPlayer pressEffectPlayer;

    public void OnClick()
    {
        Debug.Log("habit button pressed");
        if (pressEffectPlayer != null)
        {
            pressEffectPlayer.Play();
        }
    }
}
