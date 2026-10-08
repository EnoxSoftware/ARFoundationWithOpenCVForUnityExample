#if !(PLATFORM_LUMIN && !UNITY_EDITOR)

using System;
using System.Collections.Generic;
using ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Extensions.SourceToMat;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat;
using OpenCVForUnity.UnityIntegration.Helper.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnityExample
{
    /// <summary>
    /// ARFoundationCameraToMatHelper Example
    /// Demonstrates AR Foundation camera capture through <see cref="ARFoundationCameraToMatHelper"/>.
    ///
    /// Demonstrates:
    /// - Receiving frames via <see cref="ARFoundationCameraToMatHelper.FrameMatDelivered"/> (C# event)
    /// - Updating the preview and FpsMonitor layout fields via <see cref="SourceToMatHelperBase.OnFrameMatLayoutChanged"/>
    /// - Checking delivered Mat size in <see cref="ARFoundationCameraToMatHelper.FrameMatDelivered"/> when layout differs from the helper buffer
    /// - Calling <see cref="SourceToMatHelperBase.Play"/> in OnInitialized only when not already playing or paused
    /// - Recreating the preview texture when delivered Mat size differs from the current Texture2D
    /// - Helper API controls for resolution, FPS, Rotate90, Flip, AutoFocus, and facing
    ///
    /// OpenCV classes and APIs used:
    /// - <see cref="Mat"/>
    /// - <see cref="ARFoundationCameraToMatHelper"/>, <see cref="SourceToMatColorFormat"/>
    /// - <see cref="OpenCVMatUnityUtils"/>: MatToTexture2D
    ///
    /// Unity integration:
    /// - <see cref="FrameMatDeliveredEventArgs.Mat"/> is newly allocated; this example disposes it on the main thread after display conversion
    /// - On AR Foundation, <see cref="ARFoundationCameraToMatHelper.FrameMatDelivered"/> runs on the ConvertAsync completion callback
    /// - On WebCam fallback, the same event runs on the Unity main thread
    /// - Helper lifecycle events (OnInitialized, OnReleased, OnDisposed, OnErrorOccurred) are wired in the Inspector
    /// </summary>
    [RequireComponent(typeof(ARFoundationCameraToMatHelper))]
    public class ARFoundationCameraToMatHelperExample : MonoBehaviour
    {
        // Enums
        public enum FPSPreset : int
        {
            _0 = 0,
            _1 = 1,
            _5 = 5,
            _10 = 10,
            _15 = 15,
            _30 = 30,
            _60 = 60,
        }

        public enum ResolutionPreset : byte
        {
            _50x50 = 0,
            _640x480,
            _1280x720,
            _1920x1080,
            _9999x9999,
        }

        // Public Fields
        [Header("Output")]
        /// <summary>
        /// The RawImage for previewing the result.
        /// </summary>
        public RawImage ResultPreview;

        [Space(10)]

        /// <summary>
        /// The requested resolution dropdown.
        /// </summary>
        public Dropdown RequestedResolutionDropdown;

        /// <summary>
        /// The requested resolution.
        /// </summary>
        public ResolutionPreset RequestedResolution = ResolutionPreset._640x480;

        /// <summary>
        /// The RequestedFPS dropdown.
        /// </summary>
        public Dropdown RequestedFPSDropdown;

        /// <summary>
        /// The RequestedFPS.
        /// </summary>
        public FPSPreset RequestedFPS = FPSPreset._30;

        /// <summary>
        /// The rotate 90 degree toggle.
        /// </summary>
        public Toggle Rotate90DegreeToggle;

        /// <summary>
        /// The flip vertical toggle.
        /// </summary>
        public Toggle FlipVerticalToggle;

        /// <summary>
        /// The flip horizontal toggle.
        /// </summary>
        public Toggle FlipHorizontalToggle;

        // Private Fields
        private readonly Queue<Action> _executeOnMainThread = new Queue<Action>();
        private Texture2D _texture;
        private ARFoundationCameraToMatHelper _arFoundationCameraToMatHelper;
        private FpsMonitor _fpsMonitor;

        // Unity Lifecycle Methods
        private void Start()
        {
            _fpsMonitor = GetComponent<FpsMonitor>();

            _arFoundationCameraToMatHelper = gameObject.GetComponent<ARFoundationCameraToMatHelper>();
            _arFoundationCameraToMatHelper.FrameMatDelivered += OnFrameMatDelivered;
            _arFoundationCameraToMatHelper.UpdateFrameMatOnTick = false;
            _arFoundationCameraToMatHelper.OutputColorFormat = SourceToMatColorFormat.RGBA;
            Dimensions(RequestedResolution, out int width, out int height);
            _arFoundationCameraToMatHelper.RequestedWidth = width;
            _arFoundationCameraToMatHelper.RequestedHeight = height;
            _arFoundationCameraToMatHelper.RequestedFPS = (int)RequestedFPS;
            _arFoundationCameraToMatHelper.Initialize();

            // Update GUI state
            RequestedResolutionDropdown.value = (int)RequestedResolution;
            string[] enumNames = Enum.GetNames(typeof(FPSPreset));
            int index = Array.IndexOf(enumNames, RequestedFPS.ToString());
            RequestedFPSDropdown.value = index;
            Rotate90DegreeToggle.isOn = _arFoundationCameraToMatHelper.Rotate90Degree;
            FlipVerticalToggle.isOn = _arFoundationCameraToMatHelper.FlipVertical;
            FlipHorizontalToggle.isOn = _arFoundationCameraToMatHelper.FlipHorizontal;
        }

        private void Update()
        {
            lock (_executeOnMainThread)
            {
                while (_executeOnMainThread.Count > 0)
                {
                    _executeOnMainThread.Dequeue().Invoke();
                }
            }
        }

        private void OnDestroy()
        {
            if (_arFoundationCameraToMatHelper != null)
            {
                _arFoundationCameraToMatHelper.FrameMatDelivered -= OnFrameMatDelivered;
            }
        }

        // Public Methods
        /// <summary>
        /// Raises the helper initialized event.
        /// Recreates the preview texture and starts playback on first initialization.
        /// Skips Play when re-initialization has already restored Playing or Paused.
        /// </summary>
        public void OnSourceToMatHelperInitialized()
        {
            Debug.Log("OnSourceToMatHelperInitialized", this);

            RecreatePreviewTexture(_arFoundationCameraToMatHelper.FrameMat);

            if (_fpsMonitor != null)
            {
                _fpsMonitor.Add("DeviceName", _arFoundationCameraToMatHelper.DeviceName);
                RefreshFpsMonitorFrameMatLayout();
                _fpsMonitor.Add("CameraFPS", _arFoundationCameraToMatHelper.FPS.ToString());
                _fpsMonitor.Add("IsFrontFacing", _arFoundationCameraToMatHelper.IsFrontFacing.ToString());
                _fpsMonitor.Add("AutoFocusRequested", _arFoundationCameraToMatHelper.AutoFocusRequested.ToString());
                _fpsMonitor.Add("AutoFocusEnabled", _arFoundationCameraToMatHelper.AutoFocusEnabled.ToString());
                _fpsMonitor.Add("RequestedFacingDirection", _arFoundationCameraToMatHelper.RequestedFacingDirection.ToString());
                _fpsMonitor.Add("CurrentFacingDirection", _arFoundationCameraToMatHelper.CurrentFacingDirection.ToString());
                _fpsMonitor.Add("RequestedLightEstimation", _arFoundationCameraToMatHelper.RequestedLightEstimation.ToString());
                _fpsMonitor.Add("CurrentLightEstimation", _arFoundationCameraToMatHelper.CurrentLightEstimation.ToString());
            }

            LogCameraParameters(_arFoundationCameraToMatHelper.FrameMat, _arFoundationCameraToMatHelper.Intrinsics);

            if (!_arFoundationCameraToMatHelper.IsPlaying && !_arFoundationCameraToMatHelper.IsPaused)
            {
                _arFoundationCameraToMatHelper.Play();
            }
        }

        /// <summary>
        /// Raises the helper FrameMat layout changed event.
        /// </summary>
        public void OnSourceToMatHelperFrameMatLayoutChanged()
        {
            Debug.Log("OnSourceToMatHelperFrameMatLayoutChanged", this);

            if (_arFoundationCameraToMatHelper == null)
            {
                return;
            }

            RecreatePreviewTexture(_arFoundationCameraToMatHelper.FrameMat);
            RefreshFpsMonitorFrameMatLayout();
        }

        /// <summary>
        /// Raises the helper released event.
        /// </summary>
        public void OnSourceToMatHelperReleased()
        {
            Debug.Log("OnSourceToMatHelperReleased", this);

            CleanupPreviewResources();
        }

        /// <summary>
        /// Raises the helper disposed event.
        /// </summary>
        public void OnSourceToMatHelperDisposed()
        {
            Debug.Log("OnSourceToMatHelperDisposed", this);

            CleanupPreviewResources();
        }

        /// <summary>
        /// Raises the helper error occurred event.
        /// </summary>
        /// <param name="errorCode">Error code.</param>
        /// <param name="message">Message.</param>
        public void OnSourceToMatHelperErrorOccurred(SourceToMatErrorCode errorCode, string message)
        {
            Debug.Log("OnSourceToMatHelperErrorOccurred " + errorCode + ":" + message, this);

            if (_fpsMonitor != null)
            {
                _fpsMonitor.ConsoleText = "ErrorCode: " + errorCode + ":" + message;
            }
        }

        /// <summary>
        /// Raises the back button click event.
        /// </summary>
        public void OnBackButtonClick()
        {
            SceneManager.LoadScene("ARFoundationWithOpenCVForUnityExample");
        }

        /// <summary>
        /// Raises the play button click event.
        /// </summary>
        public void OnPlayButtonClick()
        {
            _arFoundationCameraToMatHelper.Play();
        }

        /// <summary>
        /// Raises the pause button click event.
        /// </summary>
        public void OnPauseButtonClick()
        {
            _arFoundationCameraToMatHelper.Pause();
        }

        /// <summary>
        /// Raises the stop button click event.
        /// </summary>
        public void OnStopButtonClick()
        {
            _arFoundationCameraToMatHelper.Stop();
        }

        /// <summary>
        /// Raises the change camera button click event.
        /// </summary>
        public void OnChangeCameraButtonClick()
        {
            _arFoundationCameraToMatHelper.RequestedIsFrontFacing = !_arFoundationCameraToMatHelper.RequestedIsFrontFacing;
        }

        /// <summary>
        /// Raises the requested resolution dropdown value changed event.
        /// </summary>
        /// <param name="result">Selected resolution preset index.</param>
        public void OnRequestedResolutionDropdownValueChanged(int result)
        {
            if ((int)RequestedResolution != result)
            {
                RequestedResolution = (ResolutionPreset)result;

                Dimensions(RequestedResolution, out int width, out int height);
                _arFoundationCameraToMatHelper.RequestedWidth = width;
                _arFoundationCameraToMatHelper.RequestedHeight = height;
            }
        }

        /// <summary>
        /// Raises the RequestedFPS dropdown value changed event.
        /// </summary>
        /// <param name="result">Selected FPS preset index.</param>
        public void OnRequestedFPSDropdownValueChanged(int result)
        {
            string[] enumNames = Enum.GetNames(typeof(FPSPreset));
            int value = (int)Enum.Parse(typeof(FPSPreset), enumNames[result], true);

            if ((int)RequestedFPS != value)
            {
                RequestedFPS = (FPSPreset)value;

                _arFoundationCameraToMatHelper.RequestedFPS = (int)RequestedFPS;
            }
        }

        /// <summary>
        /// Raises the rotate 90 degree toggle value changed event.
        /// </summary>
        public void OnRotate90DegreeToggleValueChanged()
        {
            if (Rotate90DegreeToggle.isOn != _arFoundationCameraToMatHelper.Rotate90Degree)
            {
                _arFoundationCameraToMatHelper.Rotate90Degree = Rotate90DegreeToggle.isOn;

                RefreshFpsMonitorFrameMatLayout();
            }
        }

        /// <summary>
        /// Raises the flip vertical toggle value changed event.
        /// </summary>
        public void OnFlipVerticalToggleValueChanged()
        {
            if (FlipVerticalToggle.isOn != _arFoundationCameraToMatHelper.FlipVertical)
            {
                _arFoundationCameraToMatHelper.FlipVertical = FlipVerticalToggle.isOn;

                if (_fpsMonitor != null)
                {
                    _fpsMonitor.Add("FlipVertical", _arFoundationCameraToMatHelper.FlipVertical.ToString());
                }
            }
        }

        /// <summary>
        /// Raises the flip horizontal toggle value changed event.
        /// </summary>
        public void OnFlipHorizontalToggleValueChanged()
        {
            if (FlipHorizontalToggle.isOn != _arFoundationCameraToMatHelper.FlipHorizontal)
            {
                _arFoundationCameraToMatHelper.FlipHorizontal = FlipHorizontalToggle.isOn;

                if (_fpsMonitor != null)
                {
                    _fpsMonitor.Add("FlipHorizontal", _arFoundationCameraToMatHelper.FlipHorizontal.ToString());
                }
            }
        }

        /// <summary>
        /// Raises the change autoFocus button click event.
        /// </summary>
        public void OnChangeAutoFocusButtonClick()
        {
            _arFoundationCameraToMatHelper.AutoFocusRequested = !_arFoundationCameraToMatHelper.AutoFocusRequested;

            if (_fpsMonitor != null)
            {
                _fpsMonitor.Add("AutoFocusRequested", _arFoundationCameraToMatHelper.AutoFocusRequested.ToString());
                _fpsMonitor.Add("AutoFocusEnabled", _arFoundationCameraToMatHelper.AutoFocusEnabled.ToString());
            }
        }

        // Private Methods
        private void RefreshFpsMonitorFrameMatLayout()
        {
            if (_fpsMonitor == null || _arFoundationCameraToMatHelper == null)
            {
                return;
            }

            _fpsMonitor.Add("Width", _arFoundationCameraToMatHelper.Width.ToString());
            _fpsMonitor.Add("Height", _arFoundationCameraToMatHelper.Height.ToString());
            _fpsMonitor.Add("Rotate90Degree", _arFoundationCameraToMatHelper.Rotate90Degree.ToString());
            _fpsMonitor.Add("FlipVertical", _arFoundationCameraToMatHelper.FlipVertical.ToString());
            _fpsMonitor.Add("FlipHorizontal", _arFoundationCameraToMatHelper.FlipHorizontal.ToString());
            _fpsMonitor.Add("Orientation", Screen.orientation.ToString());
            _fpsMonitor.Add("DisplayRotationAngle", _arFoundationCameraToMatHelper.DisplayRotationAngle.ToString());
            _fpsMonitor.Add("DisplayFlipVertical", _arFoundationCameraToMatHelper.DisplayFlipVertical.ToString());
            _fpsMonitor.Add("DisplayFlipHorizontal", _arFoundationCameraToMatHelper.DisplayFlipHorizontal.ToString());
        }

        private void OnFrameMatDelivered(object sender, FrameMatDeliveredEventArgs e)
        {
            Mat frameMat = e.Mat;
            if (frameMat == null)
            {
                return;
            }

            bool queuedForMainThread = false;
            try
            {
                Matrix4x4 projectionMatrix = e.ProjectionMatrix;
                XRCameraIntrinsics intrinsics = e.Intrinsics;

                Enqueue(() =>
                {
                    try
                    {
                        if (_arFoundationCameraToMatHelper == null || !_arFoundationCameraToMatHelper.IsPlaying)
                        {
                            return;
                        }

                        // Delivered Mat size may differ from FrameMat until the next layout commit.
                        EnsurePreviewTextureMatches(frameMat);
                        if (_texture == null)
                        {
                            return;
                        }

                        OpenCVMatUnityUtils.MatToTexture2D(frameMat, _texture);

                        if (_fpsMonitor != null)
                        {
                            Vector2 focalLength = intrinsics.focalLength;
                            Vector2 principalPoint = intrinsics.principalPoint;
                            Vector2Int resolution = intrinsics.resolution;
                            _fpsMonitor.Add("Intrinsics", "\n" + "FL: " + focalLength.x + "x" + focalLength.y + "\n" + "PP: " + principalPoint.x + "x" + principalPoint.y + "\n" + "R: " + resolution.x + "x" + resolution.y);
                            _fpsMonitor.Add("ProjectionMatrix", "\n" + projectionMatrix.ToString());
                        }
                    }
                    finally
                    {
                        frameMat.Dispose();
                    }
                });
                queuedForMainThread = true;
            }
            finally
            {
                if (!queuedForMainThread)
                {
                    frameMat.Dispose();
                }
            }
        }

        private void RecreatePreviewTexture(Mat imageMat)
        {
            if (imageMat == null || imageMat.cols() <= 0 || imageMat.rows() <= 0)
            {
                return;
            }

            DestroyPreviewTexture();

            // Texture dimensions must match Mat cols()/rows() (width/height may swap when rotated).
            _texture = new Texture2D(imageMat.cols(), imageMat.rows(), TextureFormat.RGBA32, false);
            _texture.wrapMode = TextureWrapMode.Clamp;

            OpenCVMatUnityUtils.MatToTexture2D(imageMat, _texture);

            if (ResultPreview != null)
            {
                ResultPreview.texture = _texture;
                AspectRatioFitter aspectRatioFitter = ResultPreview.GetComponent<AspectRatioFitter>();
                if (aspectRatioFitter != null)
                {
                    aspectRatioFitter.aspectRatio = (float)_texture.width / _texture.height;
                }
            }
        }

        private void EnsurePreviewTextureMatches(Mat imageMat)
        {
            if (imageMat == null)
            {
                return;
            }

            if (_texture != null && _texture.width == imageMat.cols() && _texture.height == imageMat.rows())
            {
                return;
            }

            RecreatePreviewTexture(imageMat);
        }

        private void CleanupPreviewResources()
        {
            // Flush queued frame callbacks so delivered Mats are disposed.
            lock (_executeOnMainThread)
            {
                while (_executeOnMainThread.Count > 0)
                {
                    _executeOnMainThread.Dequeue().Invoke();
                }
            }

            DestroyPreviewTexture();
        }

        private void DestroyPreviewTexture()
        {
            if (_texture != null)
            {
                Texture2D.Destroy(_texture);
                _texture = null;
            }
        }

        private void LogCameraParameters(Mat rgbaMat, XRCameraIntrinsics cameraIntrinsics)
        {
            double fx;
            double fy;
            double cx;
            double cy;

            using (Mat camMatrix = new Mat(3, 3, CvType.CV_64FC1))
            {
                if (cameraIntrinsics.resolution.x > 0 && cameraIntrinsics.resolution.y > 0)
                {
                    fx = cameraIntrinsics.focalLength.x;
                    fy = cameraIntrinsics.focalLength.y;
                    cx = cameraIntrinsics.principalPoint.x;
                    cy = cameraIntrinsics.principalPoint.y;

                    camMatrix.put(0, 0, fx);
                    camMatrix.put(0, 1, 0);
                    camMatrix.put(0, 2, cx);
                    camMatrix.put(1, 0, 0);
                    camMatrix.put(1, 1, fy);
                    camMatrix.put(1, 2, cy);
                    camMatrix.put(2, 0, 0);
                    camMatrix.put(2, 1, 0);
                    camMatrix.put(2, 2, 1.0f);

                    Debug.Log("Created CameraParameters from the camera intrinsics to be populated if the camera supports intrinsics. \n" + camMatrix.dump() + "\n " + cameraIntrinsics.resolution, this);
                }
                else
                {
                    float width = rgbaMat != null ? rgbaMat.width() : _arFoundationCameraToMatHelper.Width;
                    float height = rgbaMat != null ? rgbaMat.height() : _arFoundationCameraToMatHelper.Height;
                    int max_d = (int)Mathf.Max(width, height);
                    fx = max_d;
                    fy = max_d;
                    cx = width / 2.0f;
                    cy = height / 2.0f;

                    camMatrix.put(0, 0, fx);
                    camMatrix.put(0, 1, 0);
                    camMatrix.put(0, 2, cx);
                    camMatrix.put(1, 0, 0);
                    camMatrix.put(1, 1, fy);
                    camMatrix.put(1, 2, cy);
                    camMatrix.put(2, 0, 0);
                    camMatrix.put(2, 1, 0);
                    camMatrix.put(2, 2, 1.0f);

                    Debug.Log("Created a dummy CameraParameters. WebCam fallback Intrinsics is default. \n" + camMatrix.dump() + "\n " + width + " " + height, this);
                }
            }
        }

        private void Enqueue(Action action)
        {
            lock (_executeOnMainThread)
            {
                _executeOnMainThread.Enqueue(action);
            }
        }

        private void Dimensions(ResolutionPreset preset, out int width, out int height)
        {
            switch (preset)
            {
                case ResolutionPreset._50x50:
                    width = 50;
                    height = 50;
                    break;
                case ResolutionPreset._640x480:
                    width = 640;
                    height = 480;
                    break;
                case ResolutionPreset._1280x720:
                    width = 1280;
                    height = 720;
                    break;
                case ResolutionPreset._1920x1080:
                    width = 1920;
                    height = 1080;
                    break;
                case ResolutionPreset._9999x9999:
                    width = 9999;
                    height = 9999;
                    break;
                default:
                    width = height = 0;
                    break;
            }
        }
    }
}

#endif
