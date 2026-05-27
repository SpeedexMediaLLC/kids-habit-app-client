using System.Collections;
using UnityEngine;

public class PressEffectPlayer : MonoBehaviour
{
    [SerializeField] private GameObject pressEffectPrefab;
    [SerializeField] private float effectLifetime = 1.0f;
    [SerializeField] private float shakeDuration = 0.3f;
    [SerializeField] private float shakeAmplitude = 0.08f;
    [SerializeField] private float shakeFrequencyHz = 14f;
    [SerializeField] private float beepFrequency = 660f;
    [SerializeField] private float beepDuration = 0.15f;
    [SerializeField] private float beepVolume = 0.4f;

    private AudioClip _beepClip;
    private Vector3 _basePosition;
    private Coroutine _shakeCoroutine;

    private void Awake()
    {
        _beepClip = GenerateBeep(beepFrequency, beepDuration, beepVolume);
        _basePosition = transform.localPosition;
    }

    public void Play()
    {
        SpawnEffect();

        if (_shakeCoroutine != null)
        {
            StopCoroutine(_shakeCoroutine);
            transform.localPosition = _basePosition;
        }
        _shakeCoroutine = StartCoroutine(ShakeRoutine());
    }

    private void SpawnEffect()
    {
        if (pressEffectPrefab == null)
        {
            Debug.LogWarning("[PressEffectPlayer] pressEffectPrefab is not assigned");
            return;
        }

        GameObject effect = Instantiate(pressEffectPrefab, transform.position, Quaternion.identity);

        ParticleSystem ps = effect.GetComponent<ParticleSystem>();
        if (ps == null)
        {
            ps = effect.AddComponent<ParticleSystem>();
            ConfigureParticleSystem(ps);
        }
        ps.Play();

        AudioSource audio = effect.GetComponent<AudioSource>();
        if (audio != null && _beepClip != null)
        {
            audio.clip = _beepClip;
            audio.Play();
        }

        Destroy(effect, effectLifetime);
    }

    private void ConfigureParticleSystem(ParticleSystem ps)
    {
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.duration = 0.5f;
        main.loop = false;
        main.startLifetime = 0.6f;
        main.startSpeed = 1.5f;
        main.startSize = 0.15f;
        main.startColor = new Color(1f, 0.9f, 0.4f, 1f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 50;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(0f, 30)
        });

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.2f;

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (renderer != null && renderer.sharedMaterial == null)
        {
            renderer.material = new Material(Shader.Find("Sprites/Default"));
        }
    }

    private IEnumerator ShakeRoutine()
    {
        float elapsed = 0f;
        while (elapsed < shakeDuration)
        {
            float t = elapsed / shakeDuration;
            float damp = 1f - t;
            float offsetX = Mathf.Sin(elapsed * Mathf.PI * 2f * shakeFrequencyHz) * shakeAmplitude * damp;
            transform.localPosition = _basePosition + new Vector3(offsetX, 0f, 0f);
            elapsed += Time.deltaTime;
            yield return null;
        }
        transform.localPosition = _basePosition;
        _shakeCoroutine = null;
    }

    private static AudioClip GenerateBeep(float frequency, float duration, float volume)
    {
        const int sampleRate = 44100;
        int sampleCount = Mathf.Max(1, Mathf.CeilToInt(sampleRate * duration));
        float[] samples = new float[sampleCount];
        int fadeSamples = Mathf.Max(1, Mathf.CeilToInt(sampleRate * 0.02f));
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            float wave = Mathf.Sin(2f * Mathf.PI * frequency * t);
            float envelope = 1f;
            if (i < fadeSamples)
            {
                envelope = (float)i / fadeSamples;
            }
            else if (i > sampleCount - fadeSamples)
            {
                envelope = (float)(sampleCount - i) / fadeSamples;
            }
            samples[i] = wave * volume * envelope;
        }
        AudioClip clip = AudioClip.Create("PressBeep", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
