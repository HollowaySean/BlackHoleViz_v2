using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ReferenceRayTraceCamera : MonoBehaviour
{
    public ComputeShader RayTracingShader;
    public Texture SkyboxTexture;

    public float
        horizonRadius = 0.5f,
        diskMax = 2f;
    public int
        sampleRate = 1,
        noiseWidth = 512;
    public Vector2
        noiseOrigin = new Vector2(0f, 0f),
        noiseScale = new Vector2(1f, 1f);

    private RenderTexture _target;
    private Texture2D _NoiseTexture;
    private Camera _camera;

    private void Awake() {
        _camera = GetComponent<Camera>();
        _NoiseTexture = new Texture2D(noiseWidth, noiseWidth);
        UpdateNoiseTexture();
    }

    private void UpdateNoiseTexture() {
        
        // Create texture object
        Color[] pix = new Color[noiseWidth * noiseWidth];

        // Loop through pixels and apply noise
        float y = 0f;
        while (y < noiseWidth) {
            float x = 0f;
            while (x < noiseWidth) {
                float xCoord = noiseOrigin.x + (x * noiseScale.x) / noiseWidth;
                float yCoord = noiseOrigin.y + (y * noiseScale.y) / noiseWidth;
                float sample = Mathf.PerlinNoise(xCoord, yCoord);
                pix[(int)y * noiseWidth + (int)x] = new Color(sample, sample, sample);
                x++;
            }
            y++;
        }

        // Send to texture
        _NoiseTexture.SetPixels(pix);
        _NoiseTexture.Apply();
    }

    private void SetShaderParameters() {
        RayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        RayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        RayTracingShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);
        RayTracingShader.SetTexture(0, "_NoiseTexture", _NoiseTexture);
        RayTracingShader.SetFloat("horizonRadius", horizonRadius);
        RayTracingShader.SetFloat("diskMax", diskMax);
        RayTracingShader.SetInt("sampleRate", sampleRate);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination) {
        SetShaderParameters();
        Render(destination);
    }
    private void Render(RenderTexture destination) {
        // Make sure we have a current render target
        InitRenderTexture();
        // Set the target and dispatch the compute shader
        RayTracingShader.SetTexture(0, "Result", _target);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
        RayTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);
        // Blit the result texture to the screen
        Graphics.Blit(_target, destination);
    }
    private void InitRenderTexture() {
        if (_target == null || _target.width != Screen.width || _target.height != Screen.height) {
            // Release render texture if we already have one
            if (_target != null)
                _target.Release();
            // Get a render target for Ray Tracing
            _target = new RenderTexture(Screen.width, Screen.height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.enableRandomWrite = true;
            _target.Create();
        }
    }

    private void Update() {
        UpdateNoiseTexture();
    }
}