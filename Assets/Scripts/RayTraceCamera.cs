using System.Collections;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

public class RayTraceCamera : MonoBehaviour
{
    [Header("Shaders")]
    public ComputeShader cameraVectorShader;
    public ComputeShader rayUpdateShader;
    public ComputeShader simpleRayTracingShader;

    [Header("Textures")]
    public Cubemap skyboxTexture;
    public Texture BlackbodyTexture;

    [Header("Step Size Parameters")]
    public float timeStep = 0.001f;
    public float poleMargin = 0.01f;
    public float poleStep = 0.0001f;
    public float escapeDistance = 10000f;

    [Header("Physical Parameters")]
    public float horizonRadius = 0.5f;
    public float diskMax = 4f;
    [Range(1E3F, 1E4F)]
    public float diskTemp = 1E4F;
    public float falloffRate = 10f;
    public float beamExponent = 2f;

    [Header("Noise Parameters")]
    public Vector2 noiseOffset = new Vector2(0f, 0f);
    public float noiseScale = 1f;
    public float noiseCirculation = Mathf.PI / 2f;
    public float noiseH = 1f;
    public int noiseOctaves = 4;

    [Header("Brightness Modifiers")]
    public float diskMult = 1f;
    public float starMult = 1f;

    [Header("Renderer Settings")]
    public float updateInterval = 15f;
    public int overSample = 4;
    public int maxPasses = 1000;
    public bool saveToFile = false;
    public string filenamePrefix = "";

    public enum SaveType { JPEG, PNG };
    public SaveType saveType = SaveType.JPEG;

    public enum CameraState { relativistic, simple, unity };
    public CameraState cameraState = CameraState.relativistic;

    // Private objects
    private Camera _camera;
    private RenderTexture _position;
    private RenderTexture _direction;
    private RenderTexture _color;
    private RenderTexture _isComplete;
    private RenderTexture _simpleTarget;

    // Private variables
    private float
        startTime = 0f,
        checkTimer = 0f,
        numThreads = 8f;
    private int
        currentPass = 0;
    private Vector2Int
        lastCheck = new Vector2Int(0, 0);
    private bool
        startRender = true,
        renderComplete = false;

    private void Awake() {
        _camera = GetComponent<Camera>();
        BlackbodyTexture.wrapMode = TextureWrapMode.Clamp;
    }

    private void InitRenderTextures() {

        SetupTexture(ref _position,     RenderTextureFormat.ARGBFloat,  overSample * Screen.width, overSample * Screen.height);
        SetupTexture(ref _direction,    RenderTextureFormat.ARGBFloat,  overSample * Screen.width, overSample * Screen.height);
        SetupTexture(ref _color,        RenderTextureFormat.ARGBFloat,  overSample * Screen.width, overSample * Screen.height);
        SetupTexture(ref _isComplete,   RenderTextureFormat.RInt,       overSample * Screen.width, overSample * Screen.height);
    }

    private void InitSimpleRenderTexture() {

        SetupTexture(ref _simpleTarget, RenderTextureFormat.ARGBFloat, Screen.width, Screen.height);
    }

    private void SetupTexture(ref RenderTexture texture, RenderTextureFormat format, int width, int height) {

        if (texture == null || texture.width !=  width || texture.height != height) {
            // Release render texture if we already have one
            if (texture != null)
                texture.Release();

            // Get a render target for Ray Tracing
            texture = new RenderTexture(width, height, 0,
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
        rayUpdateShader.SetTexture(0, "_BlackbodyTexture", BlackbodyTexture);
        rayUpdateShader.SetVector("noiseOffset", noiseOffset);
        rayUpdateShader.SetFloat("noiseScale", noiseScale);
        rayUpdateShader.SetFloat("noiseCirculation", noiseCirculation);
        rayUpdateShader.SetFloat("noiseH", noiseH);
        rayUpdateShader.SetInt("noiseOctaves", noiseOctaves);
        rayUpdateShader.SetFloat("timeStep", timeStep);
        rayUpdateShader.SetFloat("poleMargin", poleMargin);
        rayUpdateShader.SetFloat("poleStep", poleStep);
        rayUpdateShader.SetFloat("escapeDistance", escapeDistance);
        rayUpdateShader.SetFloat("horizonRadius", horizonRadius);
        rayUpdateShader.SetFloat("diskMax", diskMax);
        rayUpdateShader.SetFloat("diskTemp", diskTemp);
        rayUpdateShader.SetFloat("falloffRate", falloffRate);
        rayUpdateShader.SetFloat("beamExponent", beamExponent);
        rayUpdateShader.SetFloat("diskMult", diskMult);
        rayUpdateShader.SetFloat("starMult", starMult);
    }

    private void SetSimpleShaderParameters() {
        simpleRayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        simpleRayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        simpleRayTracingShader.SetTexture(0, "_SkyboxTexture", skyboxTexture);
        simpleRayTracingShader.SetTexture(0, "_BlackbodyTexture", BlackbodyTexture);
        simpleRayTracingShader.SetVector("noiseOffset", noiseOffset);
        simpleRayTracingShader.SetFloat("noiseScale", noiseScale);
        simpleRayTracingShader.SetFloat("noiseCirculation", noiseCirculation);
        simpleRayTracingShader.SetFloat("noiseH", noiseH);
        simpleRayTracingShader.SetInt("noiseOctaves", noiseOctaves);
        simpleRayTracingShader.SetFloat("horizonRadius", horizonRadius);
        simpleRayTracingShader.SetFloat("diskMax", diskMax);
        simpleRayTracingShader.SetFloat("diskTemp", diskTemp);
        simpleRayTracingShader.SetFloat("falloffRate", falloffRate);
        simpleRayTracingShader.SetFloat("beamExponent", beamExponent);
        simpleRayTracingShader.SetFloat("diskMult", diskMult);
        simpleRayTracingShader.SetFloat("starMult", starMult);
        simpleRayTracingShader.SetInt("sampleRate", overSample);
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

        // Skip if using simple renderer
        if (cameraState == CameraState.simple) {
            return;
        }

        // Manually save on S key
        if (Input.GetKeyDown(KeyCode.S)) {
            SaveToFile(cameraState == CameraState.relativistic ? _color : _simpleTarget);
        }

        // Restart render on spacebar
        if (Input.GetKeyDown(KeyCode.Space)) {
            startRender = true;
            renderComplete = false;
        }

        // Step through ray trace if not complete
        if (!renderComplete) {
            if (startRender) {
                
                // Reset variables
                startTime = Time.realtimeSinceStartup;
                lastCheck = Vector2Int.zero;
                startRender = false;
                currentPass = 0;

                // Initialize shaders
                SetShaderParameters();
                GenerateCameraVectors();

            } else {

                // March rays
                UpdateRay();
                currentPass++;

                // Check if maximum passes is surpassed
                if(currentPass >= maxPasses) {
                    Debug.Log("Maximum passes exceeded, timing out.");
                    OnComplete();
                }
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
        int threadGroupsX = Mathf.CeilToInt(overSample * Screen.width / numThreads);
        int threadGroupsY = Mathf.CeilToInt(overSample * Screen.height / numThreads);
        cameraVectorShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);
    }

    private void UpdateRay() {

        // Set textures and dispatch the compute shader
        rayUpdateShader.SetTexture(0, "Position", _position);
        rayUpdateShader.SetTexture(0, "Direction", _direction);
        rayUpdateShader.SetTexture(0, "Color", _color);
        rayUpdateShader.SetTexture(0, "isComplete", _isComplete);
        int threadGroupsX = Mathf.CeilToInt(overSample * Screen.width / numThreads);
        int threadGroupsY = Mathf.CeilToInt(overSample * Screen.height / numThreads);
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
        for(int i = lastCheck.x; i < _isComplete.width; i++) {
            for(int j = lastCheck.y; j < _isComplete.width; j++) {
                if(completeTex.GetPixel(i, j).r == 0) {
                    Destroy(completeTex);
                    lastCheck = new Vector2Int(i, j);
                    return;
                }
            }
        }

        // Run method if not broken
        Destroy(completeTex);
        Debug.Log("All pixels rendered successfully.");
        OnComplete();
    }

    private void OnComplete() {

        // Set complete render flag
        renderComplete = true;

        // Debug message
        int elapsedTime = (int)(Time.realtimeSinceStartup - startTime);
        Debug.Log("Render complete!\nTime Elapsed: " + elapsedTime.ToString() + " s");

        // Save PNG
        if(saveToFile) { SaveToFile(_color); }

    }

    private void SaveToFile(RenderTexture saveTexture) {

        // Create texture2D from render texture
        Texture2D colorTex = RenderToTexture(saveTexture, TextureFormat.RGBAFloat);

        // Encode to image format
        byte[] bytes;
        switch (saveType) {
            case SaveType.JPEG:
                bytes = colorTex.EncodeToJPG();
                break;
            case SaveType.PNG:
                bytes = colorTex.EncodeToPNG();
                break;
            default:
                bytes = colorTex.EncodeToPNG();
                break;
        }
        Destroy(colorTex);

        // Save to file
        try {

            // Set up filename and save
            string filename = string.IsNullOrEmpty(filenamePrefix) ? "" : filenamePrefix + "_";
            switch (saveType) {
                case SaveType.JPEG:
                    filename += System.DateTime.Now.ToString("MMddyyyy_hhmmss") + ".jpg";
                    break;
                case SaveType.PNG:
                    filename += System.DateTime.Now.ToString("MMddyyyy_hhmmss") + ".png";
                    break;
            }
            File.WriteAllBytes(Application.dataPath + "/Output/" + filename, bytes);

            Debug.Log("File saved.");
        }
        catch {

            Debug.LogWarning("ERROR: Failure to save file.");
        }
    }
}
