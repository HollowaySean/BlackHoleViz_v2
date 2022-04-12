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
    public ComputeShader fluidSourceSetup;
    public ComputeShader fluidAdvect;
    public ComputeShader fluidDiffuse;
    public ComputeShader fluidForce;
    public ComputeShader fluidDivergence;
    public ComputeShader fluidProject;
    public ComputeShader fluidBoundary;
    public ComputeShader fluidRenderer;

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
    [Range(-1f, 1f)]
    public float spinFactor = 0.0f;
    public float diskMax = 4f;
    [Range(1E3F, 1E4F)]
    public float diskTemp = 1E4F;
    public float falloffRate = 10f;
    public float beamExponent = 2f;
    public float rotationSpeed = 1f;
    public float timeDelayFactor = 0.1f;

    [Header("Noise Parameters")]
    public Vector3 noiseOffset = new Vector3(0f, 0f, 0f);
    public float noiseScale = 1f;
    public float noiseCirculation = Mathf.PI / 2f;
    public float noiseH = 1f;
    public int noiseOctaves = 4;

    [Header("Volumetric Cloud Parameters")]
    public float stepSize = 0.01f;
    public float absorptionFactor = 0.5f;
    public float noiseCutoff = 0.5f;
    public float noiseMultiplier = 1f;
    public int maxSteps = 20;

    [Header("Fluid Sim Parameters")]
    public int fluidSize = 512;
    public float timeScale = 1f;
    public float lengthScale = 1f;
    public int jacobiIterations = 25;
    public float viscosity = 0f;
    public float diffusivity = 0f;
    public float conductivity = 0f;

    [Header("Brightness Modifiers")]
    public float diskMult = 1f;
    public float starMult = 1f;

    [Header("Renderer Settings")]
    public int numFrames = 60;
    public float framesPerSecond = 30f;
    public float updateInterval = 15f;
    public int overSample = 4;
    public int maxSoftPasses = 5000;
    public int maxPasses = 10000;
    public bool saveToFile = false;
    public bool timeStampFile = false;
    public bool frameStampFile = true;
    public string filenamePrefix = "";
    public string subfolder = "";
    public bool exitOnComplete = false;
    public SaveType saveType = SaveType.JPEG;
    public CameraState cameraState = CameraState.relativistic;

    [Header("Camera Motion Settings")]
    public Transform motionPivot;
    public float sweptAngle;

    public enum SaveType { JPEG, PNG };

    public enum CameraState { relativistic, simple, fluid, unity };

    // Private objects
    private Camera _camera;
    private RenderTexture _position;
    private RenderTexture _direction;
    private RenderTexture _color;
    private RenderTexture _isComplete;
    private RenderTexture _simpleTarget;
    private RenderTexture _fluidState1;
    private RenderTexture _fluidState2;
    private RenderTexture _fluidSource;
    private RenderTexture _fluidStaticSource;
    private RenderTexture _fluidVisual;

    // Private variables
    private float
        startTime = 0f,
        checkTimer = 0f,
        numThreads = 8f,
        coordinateTime = 0f;
    private int
        currentPass = 0,
        currentFrame = 0;
    private Vector2Int
        lastCheck = new Vector2Int(0, 0);
    private bool
        startRender = true,
        renderComplete = false,
        hardCheck = false;

    private void Awake() {

        // Get Camera component
        _camera = GetComponent<Camera>();

        // Set up textures for fluid simulation
        InitFluidTextures();
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

    private void InitFluidTextures() {

        // Initialize fluid texture
        SetupTexture(ref _fluidState1, RenderTextureFormat.ARGBFloat, fluidSize, fluidSize);
        SetupTexture(ref _fluidState2, RenderTextureFormat.ARGBFloat, fluidSize, fluidSize);
        SetupTexture(ref _fluidSource, RenderTextureFormat.ARGBFloat, fluidSize, fluidSize);
        SetupTexture(ref _fluidVisual, RenderTextureFormat.ARGBFloat, fluidSize, fluidSize);
        SetupTexture(ref _fluidStaticSource, RenderTextureFormat.ARGBFloat, fluidSize, fluidSize);

        // Initialize fluid sources and initial state
        fluidSourceSetup.SetTexture(0, "Source", _fluidSource);
        fluidSourceSetup.SetTexture(0, "State", _fluidState1);
        fluidSourceSetup.SetTexture(0, "Statics", _fluidStaticSource);
        int threadGroupsX = Mathf.CeilToInt(fluidSize / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(fluidSize / 8.0f);
        fluidSourceSetup.Dispatch(0, threadGroupsX, threadGroupsY, 1);
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

        // Set boundary method to clamp
        texture.wrapMode = TextureWrapMode.Clamp;
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
        rayUpdateShader.SetFloat("stepSize", stepSize);
        rayUpdateShader.SetFloat("absorptionFactor", absorptionFactor);
        rayUpdateShader.SetFloat("noiseMultiplier", noiseMultiplier);
        rayUpdateShader.SetFloat("noiseCutoff", noiseCutoff);
        rayUpdateShader.SetInt("maxSteps", maxSteps);
        rayUpdateShader.SetFloat("timeStep", timeStep);
        rayUpdateShader.SetFloat("poleMargin", poleMargin);
        rayUpdateShader.SetFloat("poleStep", poleStep);
        rayUpdateShader.SetFloat("escapeDistance", escapeDistance);
        rayUpdateShader.SetFloat("horizonRadius", horizonRadius);
        rayUpdateShader.SetFloat("spinFactor", spinFactor);
        rayUpdateShader.SetFloat("diskMax", diskMax);
        rayUpdateShader.SetFloat("diskTemp", diskTemp);
        rayUpdateShader.SetFloat("falloffRate", falloffRate);
        rayUpdateShader.SetFloat("beamExponent", beamExponent);
        rayUpdateShader.SetFloat("diskMult", diskMult);
        rayUpdateShader.SetFloat("starMult", starMult);
        rayUpdateShader.SetBool("hardCheck", hardCheck);
        rayUpdateShader.SetFloat("time", coordinateTime);
        rayUpdateShader.SetFloat("rotationSpeed", rotationSpeed);
        rayUpdateShader.SetFloat("timeDelayFactor", timeDelayFactor);
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
        simpleRayTracingShader.SetFloat("stepSize", stepSize);
        simpleRayTracingShader.SetFloat("absorptionFactor", absorptionFactor);
        simpleRayTracingShader.SetFloat("noiseMultiplier", noiseMultiplier);
        simpleRayTracingShader.SetFloat("noiseCutoff", noiseCutoff);
        simpleRayTracingShader.SetInt("maxSteps", maxSteps);
        simpleRayTracingShader.SetFloat("horizonRadius", horizonRadius);
        simpleRayTracingShader.SetFloat("diskMax", diskMax);
        simpleRayTracingShader.SetFloat("diskTemp", diskTemp);
        simpleRayTracingShader.SetFloat("falloffRate", falloffRate);
        simpleRayTracingShader.SetFloat("beamExponent", beamExponent);
        simpleRayTracingShader.SetFloat("diskMult", diskMult);
        simpleRayTracingShader.SetFloat("starMult", starMult);
        simpleRayTracingShader.SetInt("sampleRate", overSample);
        simpleRayTracingShader.SetFloat("time", Time.time);
        simpleRayTracingShader.SetFloat("rotationSpeed", rotationSpeed);
        simpleRayTracingShader.SetFloat("timeDelayFactor", timeDelayFactor);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination) {

        switch(cameraState) {
            case CameraState.fluid:
                Graphics.Blit(_fluidVisual, destination);
                break;
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
            switch(cameraState) {
                case CameraState.relativistic:
                    SaveToFile(_color);
                    break;
                case CameraState.fluid:
                    SaveToFile(_fluidVisual);
                    break;
                case CameraState.simple:
                    SaveToFile(_simpleTarget);
                    break;
            }
        }

        // Restart render on spacebar
        if (Input.GetKeyDown(KeyCode.Space)) {
            switch(cameraState) {
                case CameraState.fluid:
                    InitFluidTextures();
                    break;
                default:
                    ResetSettings();
                    break;
            }
        }

        // Call for fluid update if running fluid simulator
        if (cameraState == CameraState.fluid) {
            StepFluidSim(timeScale);
            return;
        }

        // Skip if using simple renderer
        if (cameraState == CameraState.simple) {
            return;
        }

        // Step through ray trace if not complete
        if (!renderComplete) {
            if (startRender) {

                // Read out to console
                Debug.Log("Beginning render.");
                
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

                // Check if hard check pass is surpassed
                if(!hardCheck && currentPass >= maxSoftPasses) {
                    hardCheck = true;
                    Debug.Log("Maximum soft passes exceeded, checking for stranded rays.");
                    rayUpdateShader.SetBool("hardCheck", true);
                }

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

    private void StepFluidSim(float timeStep) {

        // Set parameters for fluid compute shaders
        int threadGroupsX = Mathf.CeilToInt(fluidSize / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(fluidSize / 8.0f);

        // Advection step
        fluidAdvect.SetTexture(0, "StateIn", _fluidState1);
        fluidAdvect.SetTexture(0, "StateOut", _fluidState2);
        fluidAdvect.SetFloat("timeStep", timeStep);
        fluidAdvect.SetFloat("lengthScale", lengthScale);
        fluidAdvect.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Diffusion step
        fluidDiffuse.SetTexture(0, "State", _fluidState2);
        fluidDiffuse.SetFloat("timeStep", timeStep);
        fluidDiffuse.SetFloat("lengthScale", lengthScale);
        fluidDiffuse.SetFloat("nu_0", viscosity);
        fluidDiffuse.SetFloat("D_0", diffusivity);
        fluidDiffuse.SetFloat("lambda_0", conductivity);

        // Jacobi iteration
        for (int i = 0; i < jacobiIterations; i++) {
            fluidDiffuse.Dispatch(0, threadGroupsX, threadGroupsY, 1);
        }

        // Force application 
        fluidForce.SetTexture(0, "StateIn", _fluidState2);
        fluidForce.SetTexture(0, "StateOut", _fluidState1);
        fluidForce.SetFloat("timeStep", timeStep);
        fluidForce.SetFloat("lengthScale", lengthScale);
        fluidForce.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Hodge projection
        fluidDivergence.SetTexture(0, "StateIn", _fluidState1);
        fluidDivergence.SetTexture(0, "DivOut", _fluidState2);
        fluidDivergence.SetFloat("timeStep", timeStep);
        fluidDivergence.SetFloat("lengthScale", lengthScale);
        //fluidDivergence.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Jacobi iteration
        fluidProject.SetTexture(0, "StateIn", _fluidState1);
        fluidProject.SetTexture(0, "Div", _fluidState2);
        fluidProject.SetFloat("timeStep", timeStep);
        fluidProject.SetFloat("lengthScale", lengthScale);
        for (int i = 0; i < jacobiIterations; i++) {
            //fluidProject.Dispatch(0, threadGroupsX, threadGroupsY, 1);
        }

        // Boundary and sources
        fluidBoundary.SetTexture(0, "StateIn", _fluidState1);
        fluidBoundary.SetTexture(0, "StateOut", _fluidState2);
        fluidBoundary.SetTexture(0, "Source", _fluidSource);
        fluidBoundary.SetTexture(0, "StaticSource", _fluidStaticSource);
        fluidBoundary.SetFloat("timeStep", timeStep);
        fluidBoundary.SetFloat("lengthScale", lengthScale);
        //fluidBoundary.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Pass fluid state to viewer for rendering
        fluidRenderer.SetTexture(0, "StateIn", _fluidState1);
        fluidRenderer.SetTexture(0, "StateOut", _fluidState2);
        fluidRenderer.SetTexture(0, "Output", _fluidVisual);
        fluidRenderer.Dispatch(0, threadGroupsX, threadGroupsY, 1);
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
                    //Debug.Log("Incomplete on pixel: (" + i.ToString() + ", " + j.ToString() + ")");
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

        // Save image file
        if(saveToFile) { SaveToFile(_color); }

        // Update coordinate time
        currentFrame++;
        if (currentFrame >= numFrames) {

            // Console readout
            Debug.Log("Render cycle complete!");

            // Quit application
            if(exitOnComplete) { 
                
                Application.Quit();

#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif

            }

        } else {

            // Move camera around central mass
            transform.RotateAround(Vector3.zero, motionPivot.up, sweptAngle / numFrames);

            // Advance time and reset settings
            coordinateTime += (1f / framesPerSecond);
            ResetSettings();
        }
    }

    private void ResetSettings() {
        startRender = true;
        renderComplete = false;
        hardCheck = false;
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
            string filename = string.IsNullOrEmpty(filenamePrefix) ? "" : filenamePrefix;
            filename = frameStampFile ? filename + "_" +  currentFrame.ToString() : filename;
            filename = timeStampFile ? filename + "_" + System.DateTime.Now.ToString("MMddyyyy_hhmmss") : filename;
            switch (saveType) {
                case SaveType.JPEG:
                    filename += ".jpg";
                    break;
                case SaveType.PNG:
                    filename += ".png";
                    break;
            }

            // Set up path to directory
            string fullPath = Application.dataPath + "/Output/";
            fullPath = string.IsNullOrEmpty(subfolder) ? fullPath : fullPath + subfolder + "/";

            // Ensure existence of directory
            if(!Directory.Exists(fullPath)) {
                Directory.CreateDirectory(fullPath);
            }

            // Save file
            File.WriteAllBytes(fullPath + filename, bytes);
            Debug.Log("File saved.");

        } catch {

            Debug.LogWarning("ERROR: Failure to save file.");
        }
    }
}
