using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RayTraceController : MonoBehaviour
{
    public ComputeShader RayTracingShader;
    public Texture SkyboxTexture;
    public float
        M = 1f,
        alpha = 60f,
        minStep = 0.1f,
        escapeDist = 10000f;
    public int maxSteps = 100;
    public bool
        checkEscape = true,
        checkHorizon = true,
        RK4 = true,
        dynamicStep = true;

    [HideInInspector]
    public RenderTexture _target;
    private Camera _camera;

    // Debug options
    [Header("Debug options")]
    public bool debug = false;
    public Vector2Int numPoints = new Vector2Int(3, 3);
    public float debugRayMultiplier = 1f;
    private Vector2Int textureSize;
    private List<Vector2Int> testPoints = new List<Vector2Int>();
    private Texture2D debugTexture;

    private void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    private void SetShaderParameters()
    {
        // Pre-convert camera position to spherical
        Vector3 cart = _camera.transform.position;
        float r = cart.magnitude;
        float theta = Mathf.Acos(cart.y / r);
        float phi = Mathf.Atan2(cart.z, cart.x);

        RayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        RayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        RayTracingShader.SetVector("_CameraPosition", new Vector3(r, theta, phi));
        RayTracingShader.SetVector("_CameraRotation", _camera.transform.eulerAngles);
        RayTracingShader.SetFloat("M", M);
        RayTracingShader.SetFloat("alpha", alpha*Mathf.Deg2Rad);
        RayTracingShader.SetFloat("minStep", minStep);
        RayTracingShader.SetFloat("escapeDist", escapeDist);
        RayTracingShader.SetInt("maxSteps", maxSteps);
        RayTracingShader.SetBool("checkEscape", checkEscape);
        RayTracingShader.SetBool("checkHorizon", checkHorizon);
        RayTracingShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        SetShaderParameters();
        Render(destination);
    }

    private void Render(RenderTexture destination)
    {
        // Make sure we have a current render target
        InitRenderTexture();

        // Set the target and dispatch the compute shader
        RayTracingShader.SetTexture(0, "Result", _target);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
        RayTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Blit the result texture to the screen
        Graphics.Blit(_target, destination);

        if (debug) {
            RenderTexture.active = _target;
            debugTexture.ReadPixels(new Rect(0, 0, textureSize.x, textureSize.y), 0, 0);
            debugTexture.Apply(false);
            RenderTexture.active = null;
        }
    }

    private void InitRenderTexture()
    {
        if (_target == null || _target.width != Screen.width || _target.height != Screen.height)
        {
            // Release render texture if we already have one
            if (_target != null)
                _target.Release();

            // Get a render target for Ray Tracing
            _target = new RenderTexture(Screen.width, Screen.height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.enableRandomWrite = true;
            _target.Create();


            // Debug options
            if (debug)
            {
                textureSize = new Vector2Int(_target.width, _target.height);
                debugTexture = new Texture2D(_target.width, _target.height, TextureFormat.RGBAFloat, false);
                for (int x = 0; x < numPoints.x; x++)
                {
                    int xVal = (textureSize.x * x) / (numPoints.x - 1);

                    for (int y = 0; y < numPoints.y; y++)
                    {
                        int yVal = (textureSize.y * y) / (numPoints.y - 1);
                        testPoints.Add(new Vector2Int(Mathf.Min(xVal, _target.width-1), Mathf.Min(yVal, _target.height-1)));
                        //Debug.Log(new Vector2Int(xVal, yVal));
                    }
                }
            }
        }
    }

    // Update is called once per frame
    void Update() {

        if (debug && debugTexture != null) 
        {

            foreach (Vector2Int coord in testPoints) {
                Color positionC = debugTexture.GetPixel(coord.x, coord.y);
                //Debug.Log(positionC);
                Vector3 position = new Vector3(positionC.r, positionC.g, positionC.b);
                Debug.DrawLine(position, _camera.transform.position, Color.white);
                //Vector3 k = new Vector3(positionC.r, positionC.g, positionC.b);
                //Debug.DrawRay(_camera.transform.position, debugRayMultiplier * k, Color.white);
            }
        }
    }
}