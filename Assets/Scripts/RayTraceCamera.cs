using System.Collections;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

public class RayTraceCamera : MonoBehaviour
{
    public ComputeShader cameraVectorShader;
    public ComputeShader rayUpdateShader;
    public ComputeShader simpleRayTracingShader;

    public Cubemap skyboxTexture;
    public Texture BlackbodyTexture;

    public float
        timeStep = 0.001f,
        escapeDistance = 10000f,
        horizonRadius = 0.5f,
        diskMax = 4f,
        diskMult = 1f,
        starMult = 1f,
        updateInterval = 1f;
    [Range(1E3F, 1E4F)]
    public float diskTemp = 1E4F;
    public int
        scaleFactor = 4,
        noiseWidth = 512;
    public Vector2
        noiseOrigin = new Vector2(0f, 0f),
        noiseScale = new Vector2(1f, 1f);
    public bool
        liveNoiseUpdate = false,
        saveToFile = false;
    public string
        filenamePrefix = "";

    public enum CameraState { relativistic, simple, unity };
    public CameraState cameraState = CameraState.relativistic;

    private Camera _camera;
    private RenderTexture _position;
    private RenderTexture _direction;
    private RenderTexture _color;
    private RenderTexture _isComplete;
    private RenderTexture _simpleTarget;
    private Texture2D _NoiseTexture;

    private float
        checkTimer = 0f;
    private float
        numThreads = 8f;
    private bool
        startRender = true,
        renderComplete = false;

    private void Awake() {
        _camera = GetComponent<Camera>();
        _NoiseTexture = new Texture2D(noiseWidth, noiseWidth);
        UpdateNoiseTexture();
        _NoiseTexture.wrapMode = TextureWrapMode.Clamp;
        BlackbodyTexture.wrapMode = TextureWrapMode.Clamp;
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

    private void InitRenderTextures() {

        SetupTexture(ref _position,     RenderTextureFormat.ARGBFloat);
        SetupTexture(ref _direction,    RenderTextureFormat.ARGBFloat);
        SetupTexture(ref _color,        RenderTextureFormat.ARGBFloat);
        SetupTexture(ref _isComplete,   RenderTextureFormat.RInt);
    }

    private void InitSimpleRenderTexture() {

        SetupTexture(ref _simpleTarget, RenderTextureFormat.ARGBFloat);
    }

    private void SetupTexture(ref RenderTexture texture, RenderTextureFormat format) {

        if (texture == null || texture.width != scaleFactor * Screen.width || texture.height != scaleFactor * Screen.height) {
            // Release render texture if we already have one
            if (texture != null)
                texture.Release();

            // Get a render target for Ray Tracing
            texture = new RenderTexture(scaleFactor * Screen.width, scaleFactor * Screen.height, 0,
                format, RenderTextureReadWrite.Linear);
            texture.enableRandomWrite = true;
            texture.Create();
        }
    }

    private void SetShaderParameters() {

        // Pre-convert camera position to spherical
        Vector3 cart = _camera.transform.position;
        float r = cart.magnitude;
        float theta = Mathf.Atan2(new Vector2(cart.x, cart.z).magnitude, cart.y);
        float phi = Mathf.Atan2(cart.z, cart.x);

        // Send camera vectors and matrices to initialization shader
        cameraVectorShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        cameraVectorShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        cameraVectorShader.SetVector("_CameraPositionCartesian", cart);
        cameraVectorShader.SetVector("_CameraPositionSpherical", new Vector3(r, theta, phi));
        cameraVectorShader.SetFloat("horizonRadius", horizonRadius);

        // Send render parameters to update shader
        rayUpdateShader.SetTexture(0, "_SkyboxTexture", skyboxTexture);
        rayUpdateShader.SetTexture(0, "_NoiseTexture", _NoiseTexture);
        rayUpdateShader.SetTexture(0, "_BlackbodyTexture", BlackbodyTexture);
        rayUpdateShader.SetFloat("timeStep", timeStep);
        rayUpdateShader.SetFloat("escapeDistance", escapeDistance);
        rayUpdateShader.SetFloat("horizonRadius", horizonRadius);
        rayUpdateShader.SetFloat("diskMax", diskMax);
        rayUpdateShader.SetFloat("diskMult", diskMult);
        rayUpdateShader.SetFloat("starMult", starMult);
        rayUpdateShader.SetFloat("diskTemp", diskTemp);
    }

    private void SetSimpleShaderParameters() {
        simpleRayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        simpleRayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        simpleRayTracingShader.SetTexture(0, "_SkyboxTexture", skyboxTexture);
        simpleRayTracingShader.SetTexture(0, "_NoiseTexture", _NoiseTexture);
        simpleRayTracingShader.SetTexture(0, "_BlackbodyTexture", BlackbodyTexture);
        simpleRayTracingShader.SetFloat("horizonRadius", horizonRadius);
        simpleRayTracingShader.SetFloat("diskMax", diskMax);
        simpleRayTracingShader.SetFloat("diskTemp", diskTemp);
        simpleRayTracingShader.SetFloat("diskMult", diskMult);
        simpleRayTracingShader.SetFloat("starMult", starMult);
        simpleRayTracingShader.SetInt("sampleRate", scaleFactor);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination) {

        switch(cameraState) {
            case CameraState.relativistic:
                Graphics.Blit(_color, destination);
                break;
            case CameraState.simple:
                SetSimpleShaderParameters();
                RenderSimple(destination);
                break;
            case CameraState.unity:
                Graphics.Blit(source, destination);
                break;
        }

    }

    private void Update() {

        // Skip if using built in render camera
        if(cameraState == CameraState.unity) {
            return;
        }

        // Manually save on S key
        if (Input.GetKeyDown(KeyCode.S)) {
            SaveToFile(cameraState == CameraState.relativistic ? _color : _simpleTarget);
        }

        // Update noise and skip if using simple renderer
        if(cameraState == CameraState.simple) {
            if (liveNoiseUpdate) { UpdateNoiseTexture(); }
            return;
        }

        // Restart render on spacebar
        if (Input.GetKeyDown(KeyCode.Space)) {
            startRender = true;
            renderComplete = false;
        }

        // Step through ray trace if not complete
        if (!renderComplete) {
            if (startRender) {
                UpdateNoiseTexture();
                SetShaderParameters();
                GenerateCameraVectors();
                startRender = false;
            } else {
                UpdateRay();
            }

            // Check for render completeness once per second
            if (Time.time - checkTimer > updateInterval) {
                checkTimer = Time.time;
                CheckCompleteness();
            }
        }
    }

    private void GenerateCameraVectors() {

        // Make sure we have a current render target
        InitRenderTextures();

        // Set textures and dispatch the compute shader
        cameraVectorShader.SetTexture(0, "Position", _position);
        cameraVectorShader.SetTexture(0, "Direction", _direction);
        cameraVectorShader.SetTexture(0, "Color", _color);
        cameraVectorShader.SetTexture(0, "isComplete", _isComplete);
        int threadGroupsX = Mathf.CeilToInt(scaleFactor * Screen.width / numThreads);
        int threadGroupsY = Mathf.CeilToInt(scaleFactor * Screen.height / numThreads);
        cameraVectorShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);
    }

    private void UpdateRay() {

        // Set textures and dispatch the compute shader
        rayUpdateShader.SetTexture(0, "Position", _position);
        rayUpdateShader.SetTexture(0, "Direction", _direction);
        rayUpdateShader.SetTexture(0, "Color", _color);
        rayUpdateShader.SetTexture(0, "isComplete", _isComplete);
        int threadGroupsX = Mathf.CeilToInt(scaleFactor * Screen.width / numThreads);
        int threadGroupsY = Mathf.CeilToInt(scaleFactor * Screen.height / numThreads);
        rayUpdateShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);
    }


    private void RenderSimple(RenderTexture destination) {
        // Make sure we have a current render target
        InitSimpleRenderTexture();
        // Set the target and dispatch the compute shader
        simpleRayTracingShader.SetTexture(0, "Result", _simpleTarget);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
        simpleRayTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);
        // Blit the result texture to the screen
        Graphics.Blit(_simpleTarget, destination);
    }

    private Texture2D RenderToTexture(RenderTexture rt, TextureFormat format) {

        // Read render texture to texture2D
        RenderTexture.active = rt;
        Texture2D outTex = new Texture2D(rt.width, rt.height, format, false);
        outTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        outTex.Apply(false);
        RenderTexture.active = null;

        return outTex;
    }

    private void CheckCompleteness() {

        // Read render texture to texture2D
        Texture2D completeTex = RenderToTexture(_isComplete, TextureFormat.RFloat);

        // Loop over pixels searching for incomplete
        for(int i = 0; i < _isComplete.width; i++) {
            for(int j = 0; j < _isComplete.width; j++) {
                if(completeTex.GetPixel(i, j).r == 0) {
                    Destroy(completeTex);
                    return;
                }
            }
        }

        // Run method if not broken
        Destroy(completeTex);
        OnComplete();
    }

    private void OnComplete() {

        // Set complete render flag
        renderComplete = true;

        // Debug message
        Debug.Log("Render complete!\nTime Elapsed: " + Time.realtimeSinceStartup.ToString());

        // Save PNG
        if(saveToFile) { SaveToFile(_color); }

    }

    private void SaveToFile(RenderTexture saveTexture) {

        // Create texture2D from render texture
        Texture2D colorTex = RenderToTexture(saveTexture, TextureFormat.RGBAFloat);

        // Encode to PNG
        //byte[] bytes = colorTex.EncodeToPNG();
        byte[] bytes = colorTex.EncodeToJPG();
        Destroy(colorTex);

        // Save to file
        try {

            // Set up filename and save
            string filename = string.IsNullOrEmpty(filenamePrefix) ? "" : filenamePrefix + "_";
            //filename += System.DateTime.Now.ToString("MMddyyyy_hhmmss") + ".png";
            filename += System.DateTime.Now.ToString("MMddyyyy_hhmmss") + ".jpg";
            File.WriteAllBytes(Application.dataPath + "/Output/" + filename, bytes);

            Debug.Log("File saved.");
        }
        catch {

            Debug.LogWarning("ERROR: Failure to save file.");
        }
    }
}
