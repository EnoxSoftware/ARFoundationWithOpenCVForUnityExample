#if !(PLATFORM_LUMIN && !UNITY_EDITOR)

using System;
using System.Collections.Generic;
using System.Threading;
using ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Extensions;
using OpenCVForUnity.Extensions.AR;
using OpenCVForUnity.Extensions.SourceToMat;
using OpenCVForUnity.GeometryModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.AR;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat;
using OpenCVForUnity.UnityIntegration.Helper.UI;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnityExample
{
    /// <summary>
    /// ARFoundationCamera ArUco Example
    /// An example of marker based AR using OpenCVForUnity with AR Foundation camera frames.
    /// Referring to https://github.com/opencv/opencv_contrib/blob/master/modules/aruco/samples/detect_markers.cpp.
    ///
    /// Demonstrates:
    /// - Receiving frames via <see cref="ARFoundationCameraToMatHelper.FrameMatDelivered"/>
    /// - Updating preview and FpsMonitor layout fields via <see cref="SourceToMatHelperBase.OnFrameMatLayoutChanged"/>
    /// - Detecting ArUco markers on a worker thread
    /// - Placing <see cref="ARCube"/> instances through <see cref="ARHelper"/>
    ///
    /// OpenCV classes and APIs used:
    /// - <see cref="ArucoDetector"/>, <see cref="Geometry.solvePnP"/>
    /// - <see cref="ARFoundationCameraToMatHelper"/>, <see cref="SourceToMatColorFormat"/>
    ///
    /// Unity integration:
    /// - <see cref="FrameMatDeliveredEventArgs.Mat"/> is newly allocated; this example disposes it after copying to the worker Mat
    /// - Transform and Texture updates run on the Unity main thread via Enqueue
    /// </summary>
    [RequireComponent(typeof(ARFoundationCameraToMatHelper))]
    public class ARFoundationCameraArUcoExample : MonoBehaviour
    {
        // Enums
        /// <summary>
        /// Marker type enum
        /// </summary>
        public enum MarkerType
        {
            CanonicalMarker,
        }

        /// <summary>
        /// ArUco dictionary enum
        /// </summary>
        public enum ArUcoDictionary
        {
            DICT_4X4_50 = Objdetect.DICT_4X4_50,
            DICT_4X4_100 = Objdetect.DICT_4X4_100,
            DICT_4X4_250 = Objdetect.DICT_4X4_250,
            DICT_4X4_1000 = Objdetect.DICT_4X4_1000,
            DICT_5X5_50 = Objdetect.DICT_5X5_50,
            DICT_5X5_100 = Objdetect.DICT_5X5_100,
            DICT_5X5_250 = Objdetect.DICT_5X5_250,
            DICT_5X5_1000 = Objdetect.DICT_5X5_1000,
            DICT_6X6_50 = Objdetect.DICT_6X6_50,
            DICT_6X6_100 = Objdetect.DICT_6X6_100,
            DICT_6X6_250 = Objdetect.DICT_6X6_250,
            DICT_6X6_1000 = Objdetect.DICT_6X6_1000,
            DICT_7X7_50 = Objdetect.DICT_7X7_50,
            DICT_7X7_100 = Objdetect.DICT_7X7_100,
            DICT_7X7_250 = Objdetect.DICT_7X7_250,
            DICT_7X7_1000 = Objdetect.DICT_7X7_1000,
            DICT_ARUCO_ORIGINAL = Objdetect.DICT_ARUCO_ORIGINAL,
        }

        // Public Fields
        [Header("Preview")]
        public GameObject PreviewQuad;
        public Toggle DisplayCameraPreviewToggle;
        public bool DisplayCameraPreview;

        [Header("Detection")]
        public bool EnableDetection = true;
        public Toggle EnableDownScaleToggle;
        public bool EnableDownScale;

        [Tooltip("Ratio used to downscale the detection Mat when EnableDownScale is on.")]
        public float DownscaleRatio = 2f;

        [Header("AR")]
        public bool ApplyEstimationPose = true;
        public Dropdown DictionaryIdDropdown;
        public ArUcoDictionary DictionaryId = ArUcoDictionary.DICT_6X6_250;
        public Toggle EnableLowPassFilterToggle;
        public bool EnableLowPassFilter = false;
        public Toggle EnableSmoothingFilterToggle;
        public bool EnableSmoothingFilter = false;
        public Toggle EnableSOLVEPNP_ITERATIVEToggle;
        public bool EnableSOLVEPNP_ITERATIVE = false;

        [Space(10)]

        [Tooltip("The length of the markers' side. Normally, unit is meters.")]
        public float MarkerLength = 0.188f;
        public ARHelper ArHelper;
        public GameObject ArCubePrefab;

        // Private Fields
        private MarkerType _selectedMarkerType = MarkerType.CanonicalMarker;
        private readonly Queue<Action> _executeOnMainThread = new Queue<Action>();
        private Texture2D _texture;
        private ARFoundationCameraToMatHelper _arFoundationCameraToMatHelper;
        private XROrigin _xrOrigin;
        private ARCameraManager _arCameraManager;
        private FpsMonitor _fpsMonitor;
        private Mat _downScaleMat;
        private float _downScaleRatio = 1f;
        private Matrix4x4 _deliveredCameraToWorldMatrix = Matrix4x4.identity;
        private Mat _rgbMatForPreview;
        private Mat _camMatrix;
        private MatOfDouble _distCoeffs;

        private Mat _downScaleMatForWorker;
        private Mat _undistortedRgbMatForWorker;

        private Mat _camMatrixForWorker;
        private MatOfDouble _distCoeffsForWorker;

        private Dictionary _dictionary;
        private ArucoDetector _arucoDetector;

        private Dictionary<ArUcoIdentifier, ARGameObject> _arGameObjectCache = new Dictionary<ArUcoIdentifier, ARGameObject>();

        private struct DetectionResult
        {
            public int MarkerId;
            public Vector2[] ImagePoints;
            public Vector3[] ObjectPoints;
        }
        private List<DetectionResult> _detectionResults = new List<DetectionResult>();
        private readonly object _sync = new object();
        private bool _isThreadRunningValue;

        // Private Properties
        private bool _isThreadRunning
        {
            get
            {
                lock (_sync)
                {
                    return _isThreadRunningValue;
                }
            }
            set
            {
                lock (_sync)
                {
                    _isThreadRunningValue = value;
                }
            }
        }

        private bool _isDetectingValue;
        private bool _isDetecting
        {
            get
            {
                lock (_sync)
                {
                    return _isDetectingValue;
                }
            }
            set
            {
                lock (_sync)
                {
                    _isDetectingValue = value;
                }
            }
        }

        // Unity Lifecycle Methods
        private void Start()
        {
            _fpsMonitor = GetComponent<FpsMonitor>();
            _xrOrigin = FindFirstObjectByType<XROrigin>();
            if (_xrOrigin != null && _xrOrigin.Camera != null)
            {
                _arCameraManager = _xrOrigin.Camera.GetComponent<ARCameraManager>();
            }

            _arFoundationCameraToMatHelper = gameObject.GetComponent<ARFoundationCameraToMatHelper>();
            _arFoundationCameraToMatHelper.FrameMatDelivered += OnFrameMatDelivered;
            _arFoundationCameraToMatHelper.UpdateFrameMatOnTick = false;
            _arFoundationCameraToMatHelper.OutputColorFormat = SourceToMatColorFormat.GRAY;
            _arFoundationCameraToMatHelper.Initialize();

            DictionaryIdDropdown.value = (int)DictionaryId;
            DisplayCameraPreviewToggle.isOn = DisplayCameraPreview;
            EnableDownScaleToggle.isOn = EnableDownScale;
            EnableLowPassFilterToggle.isOn = EnableLowPassFilter;
            EnableSmoothingFilterToggle.isOn = EnableSmoothingFilter;
            EnableSOLVEPNP_ITERATIVEToggle.isOn = EnableSOLVEPNP_ITERATIVE;
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
        /// Raises the source to mat helper initialized event.
        /// </summary>
        public void OnSourceToMatHelperInitialized()
        {
            Debug.Log("OnSourceToMatHelperInitialized", this);

            Mat grayMat = _arFoundationCameraToMatHelper.FrameMat;
            SetupDownScaleWorkMat(grayMat);

            Mat previewSizeMat = EnableDownScale && _downScaleMat != null ? _downScaleMat : grayMat;
            if (previewSizeMat == null)
            {
                return;
            }

            RecreatePreviewTexture(previewSizeMat, uploadPixels: false);
            if (PreviewQuad != null)
            {
                PreviewQuad.SetActive(DisplayCameraPreview);
            }

            if (_fpsMonitor != null)
            {
                _fpsMonitor.Add("DeviceName", _arFoundationCameraToMatHelper.DeviceName);
                RefreshFpsMonitorFrameMatLayout(previewSizeMat);
                _fpsMonitor.Add("CameraFPS", _arFoundationCameraToMatHelper.FPS.ToString());
                _fpsMonitor.Add("IsFrontFacing", _arFoundationCameraToMatHelper.IsFrontFacing.ToString());
                _fpsMonitor.Add("RequestedFacingDirection", _arFoundationCameraToMatHelper.RequestedFacingDirection.ToString());
                _fpsMonitor.Add("CurrentFacingDirection", _arFoundationCameraToMatHelper.CurrentFacingDirection.ToString());
                _fpsMonitor.Add("RequestedLightEstimation", GetRequestedLightEstimation().ToString());
                _fpsMonitor.Add("CurrentLightEstimation", GetCurrentLightEstimation().ToString());
            }

            _dictionary = Objdetect.getPredefinedDictionary((int)DictionaryId);

            _undistortedRgbMatForWorker = new Mat();

            DetectorParameters detectorParams = new DetectorParameters();
            detectorParams.set_minDistanceToBorder(3);
            detectorParams.set_useAruco3Detection(true);
            detectorParams.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
            detectorParams.set_minSideLengthCanonicalImg(16);
            detectorParams.set_errorCorrectionRate(0.8);
            RefineParameters refineParameters = new RefineParameters(10f, 3f, true);
            _arucoDetector = new ArucoDetector(_dictionary, detectorParams, refineParameters);

            _arFoundationCameraToMatHelper.FlipHorizontal = _arFoundationCameraToMatHelper.IsFrontFacing;

            _rgbMatForPreview = new Mat();

            if (ArHelper != null)
            {
                Camera dummyCamera = ArHelper.ARCamera != null ? ArHelper.ARCamera.GetComponent<Camera>() : null;
                if (dummyCamera != null)
                {
                    dummyCamera.nearClipPlane = 0.01f;
                }

                ArHelper.Initialize();
            }

            RefreshCameraParametersForFrameLayout(grayMat, previewSizeMat);

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

            Mat grayMat = _arFoundationCameraToMatHelper.FrameMat;
            if (grayMat == null)
            {
                return;
            }

            SetupDownScaleWorkMat(grayMat);

            Mat previewSizeMat = EnableDownScale && _downScaleMat != null ? _downScaleMat : grayMat;
            if (previewSizeMat == null)
            {
                return;
            }

            RecreatePreviewTexture(previewSizeMat, uploadPixels: false);
            RefreshFpsMonitorFrameMatLayout(previewSizeMat);
            RefreshCameraParametersForFrameLayout(grayMat, previewSizeMat);
        }

        /// <summary>
        /// Raises the helper released event.
        /// </summary>
        public void OnSourceToMatHelperReleased()
        {
            Debug.Log("OnSourceToMatHelperReleased", this);

            CleanupDetectionResources();
        }

        /// <summary>
        /// Raises the source to mat helper disposed event.
        /// </summary>
        public void OnSourceToMatHelperDisposed()
        {
            Debug.Log("OnSourceToMatHelperDisposed", this);

            CleanupDetectionResources();
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

            if (_fpsMonitor != null)
            {
                _fpsMonitor.Toast("If LightEstimation is enabled, the camera facing direction may not be changed depending on the device's capabilities.");
            }
        }

        /// <summary>
        /// Raises the change LightEstimation button click event.
        /// </summary>
        public void OnChangeLightEstimationButtonClick()
        {
            LightEstimation next = GetRequestedLightEstimation() == LightEstimation.None
                ? LightEstimation.AmbientColor | LightEstimation.AmbientIntensity
                : LightEstimation.None;

            if (_arFoundationCameraToMatHelper != null)
            {
                _arFoundationCameraToMatHelper.RequestedLightEstimation = next;
            }

            // The helper setter is a no-op on the Editor WebCam path. Drive ARCameraManager
            // so LightEstimationManager continues to receive frame light-estimation data.
            if (_arCameraManager != null)
            {
                _arCameraManager.requestedLightEstimation = next;
            }

            if (_fpsMonitor != null)
            {
                _fpsMonitor.Add("RequestedLightEstimation", GetRequestedLightEstimation().ToString());
                _fpsMonitor.Add("CurrentLightEstimation", GetCurrentLightEstimation().ToString());
                _fpsMonitor.Toast("If LightEstimation is enabled, the camera facing direction may not be changed depending on the device's capabilities.");
            }
        }

        /// <summary>
        /// Raises the display camera preview toggle value changed event.
        /// </summary>
        public void OnDisplayCameraPreviewToggleValueChanged()
        {
            DisplayCameraPreview = DisplayCameraPreviewToggle.isOn;

            if (PreviewQuad != null)
            {
                PreviewQuad.SetActive(DisplayCameraPreview);
            }
        }

        /// <summary>
        /// Raises the enable downscale toggle value changed event.
        /// </summary>
        public void OnEnableDownScaleToggleValueChanged()
        {
            EnableDownScale = EnableDownScaleToggle.isOn;

            if (_arFoundationCameraToMatHelper != null && _arFoundationCameraToMatHelper.IsInitialized)
            {
                _arFoundationCameraToMatHelper.Initialize();
            }
        }

        /// <summary>
        /// Raises the dictionary id dropdown value changed event.
        /// </summary>
        public void OnDictionaryIdDropdownValueChanged(int result)
        {
            if ((int)DictionaryId != result)
            {
                DictionaryId = (ArUcoDictionary)result;
                _dictionary = Objdetect.getPredefinedDictionary((int)DictionaryId);

                if (_arFoundationCameraToMatHelper != null && _arFoundationCameraToMatHelper.IsInitialized)
                {
                    _arFoundationCameraToMatHelper.Initialize();
                }
            }
        }

        /// <summary>
        /// Raises the enable low pass filter toggle value changed event.
        /// </summary>
        public void OnEnableLowPassFilterToggleValueChanged()
        {
            EnableLowPassFilter = EnableLowPassFilterToggle.isOn;

            if (ArHelper != null && ArHelper.ARGameObjects != null)
            {
                foreach (ARGameObject arGameObject in ArHelper.ARGameObjects)
                {
                    if (arGameObject != null)
                    {
                        arGameObject.UseLowPassFilter = EnableLowPassFilter;
                    }
                }
            }
        }

        /// <summary>
        /// Raises the enable smoothing filter toggle value changed event.
        /// </summary>
        public void OnEnableSmoothingFilterToggleValueChanged()
        {
            EnableSmoothingFilter = EnableSmoothingFilterToggle.isOn;

            if (ArHelper != null && ArHelper.ARGameObjects != null)
            {
                foreach (ARGameObject arGameObject in ArHelper.ARGameObjects)
                {
                    if (arGameObject != null)
                    {
                        arGameObject.UseSmoothingFilter = EnableSmoothingFilter;
                    }
                }
            }
        }

        /// <summary>
        /// Raises the enable SOLVEPNP_ITERATIVE toggle value changed event.
        /// </summary>
        public void OnEnableSOLVEPNP_ITERATIVEToggleValueChanged()
        {
            EnableSOLVEPNP_ITERATIVE = EnableSOLVEPNP_ITERATIVEToggle.isOn;

            if (ArHelper != null && ArHelper.ARGameObjects != null)
            {
                foreach (ARGameObject arGameObject in ArHelper.ARGameObjects)
                {
                    if (arGameObject != null)
                    {
                        arGameObject.UseSOLVEPNP_ITERATIVE = EnableSOLVEPNP_ITERATIVE;
                    }
                }
            }
        }

        /// <summary>
        /// Called when an ARGameObject enters the ARCamera viewport.
        /// </summary>
        /// <param name="aRHelper">The ARHelper that raised the event.</param>
        /// <param name="arCamera">The ARCamera whose viewport was entered.</param>
        /// <param name="arGameObject">The ARGameObject that entered the viewport.</param>
        public void OnEnterARCameraViewport(ARHelper aRHelper, ARCamera arCamera, ARGameObject arGameObject)
        {
            Debug.Log("OnEnterARCamera arCamera.name " + arCamera.name + " arGameObject.name " + arGameObject.name, this);

            StartCoroutine(arGameObject.GetComponent<ARCube>().EnterAnimation(arGameObject.gameObject, 0f, 1f, 0.5f));
        }

        /// <summary>
        /// Called when an ARGameObject exits the ARCamera viewport.
        /// </summary>
        /// <param name="aRHelper">The ARHelper that raised the event.</param>
        /// <param name="arCamera">The ARCamera whose viewport was exited.</param>
        /// <param name="arGameObject">The ARGameObject that exited the viewport.</param>
        public void OnExitARCameraViewport(ARHelper aRHelper, ARCamera arCamera, ARGameObject arGameObject)
        {
            Debug.Log("OnExitARCamera arCamera.name " + arCamera.name + " arGameObject.name " + arGameObject.name, this);

            StartCoroutine(arGameObject.GetComponent<ARCube>().ExitAnimation(arGameObject.gameObject, 1f, 0f, 0.2f));
        }

        // Private Methods
        private void OnFrameMatDelivered(object sender, FrameMatDeliveredEventArgs e)
        {
            Mat grayMat = e.Mat;
            if (grayMat == null)
            {
                return;
            }

            bool queuedForMainThread = false;
            try
            {
                if (EnableDetection && !_isDetecting)
                {
                    _isDetecting = true;

                    EnsureDownScaleWorkMat(grayMat);

                    Mat detectMat = grayMat;
                    if (EnableDownScale && _downScaleMat != null && !_downScaleMat.empty())
                    {
                        Imgproc.resize(grayMat, _downScaleMat, _downScaleMat.size(), 0, 0, Imgproc.INTER_LINEAR);
                        detectMat = _downScaleMat;
                    }

                    lock (_sync)
                    {
                        _deliveredCameraToWorldMatrix = e.CameraToWorldMatrix;

                        if (detectMat != null && !detectMat.empty())
                        {
                            if (_downScaleMatForWorker == null || _downScaleMatForWorker.empty() ||
                                _downScaleMatForWorker.width() != detectMat.width() ||
                                _downScaleMatForWorker.height() != detectMat.height() ||
                                _downScaleMatForWorker.type() != detectMat.type())
                            {
                                _downScaleMatForWorker?.Dispose();
                                _downScaleMatForWorker = new Mat(detectMat.rows(), detectMat.cols(), detectMat.type());
                            }

                            detectMat.copyTo(_downScaleMatForWorker);
                        }
                    }

                    StartThread(ThreadWorker);
                }
            }
            finally
            {
                if (!queuedForMainThread)
                {
                    grayMat.Dispose();
                }
            }
        }

        private void EnsureDownScaleWorkMat(Mat sourceMat)
        {
            if (!EnableDownScale || sourceMat == null)
            {
                return;
            }

            float ratio = DownscaleRatio > 1f ? DownscaleRatio : 1f;
            int expectedWidth = Mathf.Max(1, Mathf.RoundToInt(sourceMat.width() / ratio));
            int expectedHeight = Mathf.Max(1, Mathf.RoundToInt(sourceMat.height() / ratio));
            if (_downScaleMat == null || _downScaleMat.empty() ||
                _downScaleMat.width() != expectedWidth ||
                _downScaleMat.height() != expectedHeight ||
                _downScaleMat.type() != sourceMat.type())
            {
                SetupDownScaleWorkMat(sourceMat);
            }
        }

        private void RefreshFpsMonitorFrameMatLayout(Mat previewSizeMat)
        {
            if (_fpsMonitor == null || _arFoundationCameraToMatHelper == null)
            {
                return;
            }

            _fpsMonitor.Add("Width", _arFoundationCameraToMatHelper.Width.ToString());
            _fpsMonitor.Add("Height", _arFoundationCameraToMatHelper.Height.ToString());
            _fpsMonitor.Add("OutputColorFormat", _arFoundationCameraToMatHelper.OutputColorFormat.ToString());
            _fpsMonitor.Add("Rotate90Degree", _arFoundationCameraToMatHelper.Rotate90Degree.ToString());
            _fpsMonitor.Add("FlipVertical", _arFoundationCameraToMatHelper.FlipVertical.ToString());
            _fpsMonitor.Add("FlipHorizontal", _arFoundationCameraToMatHelper.FlipHorizontal.ToString());
            _fpsMonitor.Add("Orientation", Screen.orientation.ToString());
            _fpsMonitor.Add("DisplayRotationAngle", _arFoundationCameraToMatHelper.DisplayRotationAngle.ToString());
            _fpsMonitor.Add("DisplayFlipVertical", _arFoundationCameraToMatHelper.DisplayFlipVertical.ToString());
            _fpsMonitor.Add("DisplayFlipHorizontal", _arFoundationCameraToMatHelper.DisplayFlipHorizontal.ToString());

            if (EnableDownScale && previewSizeMat != null)
            {
                float width = previewSizeMat.width();
                float height = previewSizeMat.height();
                _fpsMonitor.Add("EnableDownScale", _downScaleRatio + " / " + width + " x " + height);
            }
        }

        /// <summary>
        /// Rebuilds camera intrinsics from the current <see cref="SourceToMatHelperBase.FrameMat"/>, preview resolution,
        /// and helper intrinsics, then applies them to the detection worker and <see cref="ARCamera"/>.
        /// </summary>
        /// <param name="grayMat">The helper <see cref="SourceToMatHelperBase.FrameMat"/>.</param>
        /// <param name="previewSizeMat">Mat used for preview and AR projection (including downscaled size when enabled).</param>
        private void RefreshCameraParametersForFrameLayout(Mat grayMat, Mat previewSizeMat)
        {
            if (_arFoundationCameraToMatHelper == null || grayMat == null || previewSizeMat == null || grayMat.empty())
            {
                return;
            }

            float previewWidth = previewSizeMat.width();
            float previewHeight = previewSizeMat.height();
            float scaleX = previewWidth / Mathf.Max(1, grayMat.width());
            float scaleY = previewHeight / Mathf.Max(1, grayMat.height());

            XRCameraIntrinsics cameraIntrinsics = _arFoundationCameraToMatHelper.Intrinsics;

            _camMatrix?.Dispose();
            _distCoeffs?.Dispose();

            if (cameraIntrinsics.resolution.x > 0 && cameraIntrinsics.resolution.y > 0)
            {
                _camMatrix = CreateCameraMatrix(
                    cameraIntrinsics.focalLength.x * scaleX,
                    cameraIntrinsics.focalLength.y * scaleY,
                    cameraIntrinsics.principalPoint.x * scaleX,
                    cameraIntrinsics.principalPoint.y * scaleY);
                _distCoeffs = new MatOfDouble(0, 0, 0, 0);

                Debug.Log("Created CameraParameters from the camera intrinsics to be populated if the camera supports intrinsics.", this);
            }
            else
            {
                float dummyWidth = grayMat.width();
                float dummyHeight = grayMat.height();
                int max_d = (int)Mathf.Max(dummyWidth, dummyHeight);
                double fx = max_d * scaleX;
                double fy = max_d * scaleY;
                double cx = dummyWidth / 2.0f * scaleX;
                double cy = dummyHeight / 2.0f * scaleY;

                _camMatrix = CreateCameraMatrix(fx, fy, cx, cy);
                _distCoeffs = new MatOfDouble(0, 0, 0, 0);

                Debug.Log("Created a dummy CameraParameters. WebCam fallback Intrinsics is default.", this);
            }

            Debug.Log("camMatrix " + _camMatrix.dump(), this);
            Debug.Log("distCoeffs " + _distCoeffs.dump(), this);

            if (_fpsMonitor != null)
            {
                Vector2 focalLength = cameraIntrinsics.focalLength;
                Vector2 principalPoint = cameraIntrinsics.principalPoint;
                Vector2Int resolution = cameraIntrinsics.resolution;
                _fpsMonitor.Add("Intrinsics", "\n" + "FL: " + focalLength.x + "x" + focalLength.y + "\n" + "PP: " + principalPoint.x + "x" + principalPoint.y + "\n" + "R: " + resolution.x + "x" + resolution.y);
            }

            Mat newCamMatrixForWorker = _camMatrix.clone();
            MatOfDouble newDistCoeffsForWorker = new MatOfDouble(_distCoeffs);
            lock (_sync)
            {
                _camMatrixForWorker?.Dispose();
                _camMatrixForWorker = newCamMatrixForWorker;
                _distCoeffsForWorker?.Dispose();
                _distCoeffsForWorker = newDistCoeffsForWorker;
            }

            if (ArHelper != null && ArHelper.ARCamera != null)
            {
                ArHelper.ARCamera.SetCamMatrix(_camMatrix);
                ArHelper.ARCamera.SetDistCoeffs(_distCoeffs);
                ArHelper.ARCamera.SetARCameraParameters(
                    Screen.width,
                    Screen.height,
                    (int)previewWidth,
                    (int)previewHeight,
                    Vector2.zero,
                    new Vector2(1.0f, 1.0f));
            }
        }

        private void SetupDownScaleWorkMat(Mat sourceMat)
        {
            _downScaleMat?.Dispose();
            _downScaleMat = null;

            if (sourceMat == null || sourceMat.empty())
            {
                _downScaleRatio = 1f;
                return;
            }

            if (EnableDownScale && DownscaleRatio > 1f)
            {
                _downScaleRatio = DownscaleRatio;
                int width = Mathf.Max(1, Mathf.RoundToInt(sourceMat.width() / _downScaleRatio));
                int height = Mathf.Max(1, Mathf.RoundToInt(sourceMat.height() / _downScaleRatio));
                _downScaleMat = new Mat(height, width, sourceMat.type());
            }
            else
            {
                _downScaleRatio = 1f;
            }
        }

        private void CleanupDetectionResources()
        {
            StopThread();
            lock (_executeOnMainThread)
            {
                while (_executeOnMainThread.Count > 0)
                {
                    _executeOnMainThread.Dequeue().Invoke();
                }
            }
            _isDetecting = false;

            _arucoDetector?.Dispose();
            _arucoDetector = null;

            _dictionary?.Dispose();
            _dictionary = null;

            _camMatrixForWorker?.Dispose();
            _camMatrixForWorker = null;
            _distCoeffsForWorker?.Dispose();
            _distCoeffsForWorker = null;

            _downScaleMatForWorker?.Dispose();
            _downScaleMatForWorker = null;
            _undistortedRgbMatForWorker?.Dispose();
            _undistortedRgbMatForWorker = null;

            _downScaleMat?.Dispose();
            _downScaleMat = null;

            if (ArHelper != null)
            {
                RemoveAllARGameObject(ArHelper.ARGameObjects);
                ArHelper.Dispose();
            }

            _camMatrix?.Dispose();
            _camMatrix = null;
            _distCoeffs?.Dispose();
            _distCoeffs = null;

            _rgbMatForPreview?.Dispose();
            _rgbMatForPreview = null;

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

        private void ApplyPreviewQuadLayout()
        {
            if (PreviewQuad == null || _texture == null)
            {
                return;
            }

            MeshRenderer meshRenderer = PreviewQuad.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.material.mainTexture = _texture;
            }

            float width = _texture.width;
            float height = Mathf.Max(1, _texture.height);
            float aspect = width / height;
            const float PREVIEW_EXTENT = 0.15f;
            float scaleX = PREVIEW_EXTENT * Mathf.Max(aspect, 1f);
            float scaleY = PREVIEW_EXTENT * Mathf.Max(1f / aspect, 1f);
            PreviewQuad.transform.localScale = new Vector3(scaleX, scaleY, 1f);
        }

        private void RecreatePreviewTexture(Mat imageMat, bool uploadPixels)
        {
            if (imageMat == null || imageMat.cols() <= 0 || imageMat.rows() <= 0)
            {
                return;
            }

            DestroyPreviewTexture();

            _texture = new Texture2D(imageMat.cols(), imageMat.rows(), TextureFormat.RGB24, false);
            ApplyPreviewQuadLayout();

            if (uploadPixels && !imageMat.empty())
            {
                OpenCVMatUnityUtils.MatToTexture2D(imageMat, _texture);
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

            RecreatePreviewTexture(imageMat, uploadPixels: false);
        }

        private Mat CreateCameraMatrix(double fx, double fy, double cx, double cy)
        {
            Mat camMatrix = new Mat(3, 3, CvType.CV_64FC1);
            camMatrix.put(0, 0, fx);
            camMatrix.put(0, 1, 0);
            camMatrix.put(0, 2, cx);
            camMatrix.put(1, 0, 0);
            camMatrix.put(1, 1, fy);
            camMatrix.put(1, 2, cy);
            camMatrix.put(2, 0, 0);
            camMatrix.put(2, 1, 0);
            camMatrix.put(2, 2, 1.0f);

            return camMatrix;
        }

        private void StartThread(Action action)
        {
            ThreadPool.QueueUserWorkItem(_ => action());
        }

        private void StopThread()
        {
            if (!_isThreadRunning)
            {
                return;
            }

            while (_isThreadRunning)
            {
                //Wait threading stop
            }
        }

        private void ThreadWorker()
        {
            _isThreadRunning = true;

            DetectARUcoMarker();

            lock (_executeOnMainThread)
            {
                if (_executeOnMainThread.Count == 0)
                {
                    _executeOnMainThread.Enqueue(() =>
                    {
                        OnDetectionDone();
                    });
                }
            }

            _isThreadRunning = false;
        }

        private void DetectARUcoMarker()
        {
            List<Mat> corners = new List<Mat>();
            Mat ids = new Mat();
            List<Mat> rejectedCorners = new List<Mat>();

            try
            {
                lock (_sync)
                {
                    if (_downScaleMatForWorker == null || _downScaleMatForWorker.empty())
                    {
                        _detectionResults = new List<DetectionResult>();
                        return;
                    }
                }

                Imgproc.undistort(_downScaleMatForWorker, _undistortedRgbMatForWorker, _camMatrixForWorker, _distCoeffsForWorker);
                _arucoDetector.detectMarkers(_undistortedRgbMatForWorker, corners, ids, rejectedCorners);

                if (ApplyEstimationPose && ids.total() > 0)
                {
                    EstimatePoseCanonicalMarker(_undistortedRgbMatForWorker, corners, ids);
                }
                else
                {
                    lock (_sync)
                    {
                        _detectionResults = new List<DetectionResult>();
                    }
                }
            }
            finally
            {
                ids?.Dispose();
                if (corners != null)
                {
                    foreach (var item in corners)
                    {
                        item.Dispose();
                    }
                }

                if (rejectedCorners != null)
                {
                    foreach (var item in rejectedCorners)
                    {
                        item.Dispose();
                    }
                }
            }
        }

        private void OnDetectionDone()
        {
            Matrix4x4 deliveredCameraToWorldMatrix;
            lock (_sync)
            {
                deliveredCameraToWorldMatrix = _deliveredCameraToWorldMatrix;
            }

            if (ApplyEstimationPose && ArHelper != null && ArHelper.ARCamera != null)
            {
                if (_xrOrigin != null)
                {
                    deliveredCameraToWorldMatrix = _xrOrigin.transform.localToWorldMatrix * deliveredCameraToWorldMatrix;
                }

                Matrix4x4 cameraLocalToWorldMatrix = deliveredCameraToWorldMatrix * Matrix4x4.Scale(new Vector3(1, 1, -1));
                OpenCVARUtils.SetTransformFromMatrix(ArHelper.ARCamera.transform, ref cameraLocalToWorldMatrix);

                if (_camMatrixForWorker != null && _distCoeffsForWorker != null)
                {
                    ArHelper.ARCamera.SetCamMatrix(_camMatrixForWorker);
                    ArHelper.ARCamera.SetDistCoeffs(_distCoeffsForWorker);
                }

                ArHelper.ResetARGameObjectsImagePointsAndObjectPoints();

                List<DetectionResult> detectionResults;
                lock (_sync)
                {
                    detectionResults = new List<DetectionResult>(_detectionResults);
                }

                foreach (var result in detectionResults)
                {
                    var arUcoId = new ArUcoIdentifier((int)_selectedMarkerType, (int)DictionaryId, new[] { result.MarkerId });
                    ARGameObject aRGameObject = FindOrCreateARGameObject(ArHelper.ARGameObjects, arUcoId, ArHelper.transform);
                    aRGameObject.SolvePnPFlagsMode = ARPoseEstimator.Calib3dSolvePnPFlagsMode.SOLVEPNP_IPPE_SQUARE;

                    aRGameObject.ImagePoints = result.ImagePoints;
                    aRGameObject.ObjectPoints = result.ObjectPoints;
                }

                ArHelper.CalculateARMatrix();
                ArHelper.UpdateTransform();
            }

            if (DisplayCameraPreview)
            {
                Mat previewSource = null;
                lock (_sync)
                {
                    previewSource = _downScaleMatForWorker;
                }

                if (previewSource != null && !previewSource.empty())
                {
                    Imgproc.cvtColor(previewSource, _rgbMatForPreview, Imgproc.COLOR_GRAY2RGB);

                    List<DetectionResult> detectionResults;
                    lock (_sync)
                    {
                        detectionResults = new List<DetectionResult>(_detectionResults);
                    }
                    foreach (var result in detectionResults)
                    {
                        using (MatOfPoint2f imagePoints = new MatOfPoint2f(result.ImagePoints))
                        using (MatOfPoint3f objectPoints = new MatOfPoint3f(result.ObjectPoints))
                        {
                            DebugDrawFrameAxes(_rgbMatForPreview, objectPoints, imagePoints, _camMatrixForWorker != null ? _camMatrixForWorker : _camMatrix, _distCoeffsForWorker != null ? _distCoeffsForWorker : _distCoeffs, MarkerLength * 0.5f);
                        }
                    }

                    EnsurePreviewTextureMatches(_rgbMatForPreview);
                    if (_texture != null)
                    {
                        OpenCVMatUnityUtils.MatToTexture2D(_rgbMatForPreview, _texture);
                    }
                }
            }

            _isDetecting = false;
        }

        /// <summary>
        /// Finds or creates an ARGameObject with the specified AR marker identifier.
        /// </summary>
        /// <param name="arGameObjects">Registered ARGameObject list.</param>
        /// <param name="arUcoId">ArUco identifier used as the cache key.</param>
        /// <param name="parentTransform">Parent transform for a newly instantiated cube.</param>
        /// <returns>The existing or newly created ARGameObject.</returns>
        private ARGameObject FindOrCreateARGameObject(List<ARGameObject> arGameObjects, ArUcoIdentifier arUcoId, Transform parentTransform)
        {
            ARGameObject FindARGameObjectById(List<ARGameObject> arGameObjects, ArUcoIdentifier id)
            {
                if (_arGameObjectCache.TryGetValue(id, out var cachedObject) && cachedObject != null)
                {
                    return cachedObject;
                }
                return null;
            }

            ARGameObject arGameObject = FindARGameObjectById(arGameObjects, arUcoId);
            if (arGameObject == null)
            {
                arGameObject = Instantiate(ArCubePrefab, parentTransform).GetComponent<ARGameObject>();

                string markerIdsStr = arUcoId.MarkerIds != null ? string.Join(",", arUcoId.MarkerIds) : null;
                string arUcoIdNameStr;
                if (markerIdsStr != null)
                {
                    arUcoIdNameStr = (MarkerType)arUcoId.MarkerType + " " + (ArUcoDictionary)arUcoId.DictionaryId + " [" + markerIdsStr + "]";
                }
                else
                {
                    arUcoIdNameStr = (MarkerType)arUcoId.MarkerType + " " + (ArUcoDictionary)arUcoId.DictionaryId;
                }

                arGameObject.name = arUcoIdNameStr;
                arGameObject.GetComponent<ARCube>().SetInfoPlateTexture(arUcoIdNameStr);
                arGameObject.UseLowPassFilter = EnableLowPassFilter;
                arGameObject.UseSmoothingFilter = EnableSmoothingFilter;
                arGameObject.UseSOLVEPNP_ITERATIVE = EnableSOLVEPNP_ITERATIVE;
                arGameObject.OnEnterARCameraViewport.AddListener(OnEnterARCameraViewport);
                arGameObject.OnExitARCameraViewport.AddListener(OnExitARCameraViewport);
                arGameObject.gameObject.SetActive(false);
                arGameObjects.Add(arGameObject);
                _arGameObjectCache[arUcoId] = arGameObject;
            }
            return arGameObject;
        }

        /// <summary>
        /// Removes all ARGameObjects from the list and destroys them.
        /// </summary>
        /// <param name="arGameObjects">Registered ARGameObject list.</param>
        private void RemoveAllARGameObject(List<ARGameObject> arGameObjects)
        {
            if (arGameObjects != null)
            {
                foreach (ARGameObject arGameObject in arGameObjects)
                {
                    if (arGameObject != null)
                    {
                        Destroy(arGameObject.gameObject);
                    }
                }
                arGameObjects.Clear();
            }

            _arGameObjectCache.Clear();
        }

        private void DebugDrawFrameAxes(Mat image, MatOfPoint3f objectPoints, MatOfPoint2f imagePoints, Mat cameraMatrix, MatOfDouble distCoeffs,
                                 float length, int thickness = 3)
        {
            using (Mat rvec = new Mat(3, 1, CvType.CV_64FC1))
            using (Mat tvec = new Mat(3, 1, CvType.CV_64FC1))
            {
                Geometry.solvePnP(objectPoints, imagePoints, cameraMatrix, distCoeffs, rvec, tvec);

                OpenCVARUtils.SafeDrawFrameAxes(image, cameraMatrix, distCoeffs, rvec, tvec, length, thickness);
            }
        }

        private struct ArUcoIdentifier : IEquatable<ArUcoIdentifier>
        {
            public int MarkerType;
            public int DictionaryId;
            public int[] MarkerIds;

            public ArUcoIdentifier(int markerType, int dictionaryId, int[] markerIds)
            {
                MarkerType = markerType;
                DictionaryId = dictionaryId;
                MarkerIds = markerIds;
            }

            public override string ToString()
            {
                string markerIdsStr = MarkerIds != null ? string.Join(",", MarkerIds) : null;
                if (markerIdsStr != null)
                {
                    return $"{MarkerType} {DictionaryId} [{markerIdsStr}]";
                }
                else
                {
                    return $"{MarkerType} {DictionaryId}";
                }
            }

            public override int GetHashCode()
            {
                int hash = MarkerType;
                hash = hash * 31 + DictionaryId;
                if (MarkerIds != null)
                {
                    foreach (int id in MarkerIds)
                    {
                        hash = hash * 31 + id;
                    }
                }
                return hash;
            }

            public bool Equals(ArUcoIdentifier other)
            {
                if (MarkerType != other.MarkerType || DictionaryId != other.DictionaryId)
                {
                    return false;
                }

                if (MarkerIds == null)
                {
                    return other.MarkerIds == null;
                }

                if (other.MarkerIds == null)
                {
                    return false;
                }

                if (MarkerIds.Length != other.MarkerIds.Length)
                {
                    return false;
                }

                for (int i = 0; i < MarkerIds.Length; i++)
                {
                    if (MarkerIds[i] != other.MarkerIds[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private void EstimatePoseCanonicalMarker(Mat rgbMat, List<Mat> corners, Mat ids)
        {
            using (MatOfPoint3f objectPoints = new MatOfPoint3f(
                new Point3(-MarkerLength / 2f, MarkerLength / 2f, 0),
                new Point3(MarkerLength / 2f, MarkerLength / 2f, 0),
                new Point3(MarkerLength / 2f, -MarkerLength / 2f, 0),
                new Point3(-MarkerLength / 2f, -MarkerLength / 2f, 0)
                ))
            {
                List<DetectionResult> detectionResults = new List<DetectionResult>();

                Span<int> idsValues = ids.AsSpan<int>();

                for (int i = 0; i < idsValues.Length; i++)
                {
                    using (Mat corner_4x1 = corners[i].reshape(2, 4))
                    using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner_4x1))
                    {
                        DetectionResult result = new DetectionResult
                        {
                            MarkerId = idsValues[i],
                            ImagePoints = imagePoints.toVector2Array(),
                            ObjectPoints = objectPoints.toVector3Array()
                        };
                        detectionResults.Add(result);
                    }
                }

                lock (_sync)
                {
                    _detectionResults = detectionResults;
                }
            }
        }

        /// <summary>
        /// Gets the requested light estimation, falling back to <see cref="ARCameraManager"/> on the Editor WebCam path.
        /// </summary>
        /// <returns>The requested light estimation.</returns>
        private LightEstimation GetRequestedLightEstimation()
        {
            if (_arFoundationCameraToMatHelper != null)
            {
                LightEstimation helperRequested = _arFoundationCameraToMatHelper.RequestedLightEstimation;
                if (helperRequested != LightEstimation.None)
                {
                    return helperRequested;
                }
            }

            if (_arCameraManager != null)
            {
                return _arCameraManager.requestedLightEstimation;
            }

            return LightEstimation.None;
        }

        /// <summary>
        /// Gets the current light estimation, falling back to <see cref="ARCameraManager"/> on the Editor WebCam path.
        /// </summary>
        /// <returns>The current light estimation.</returns>
        private LightEstimation GetCurrentLightEstimation()
        {
            if (_arFoundationCameraToMatHelper != null)
            {
                LightEstimation helperCurrent = _arFoundationCameraToMatHelper.CurrentLightEstimation;
                if (helperCurrent != LightEstimation.None)
                {
                    return helperCurrent;
                }
            }

            if (_arCameraManager != null)
            {
                return _arCameraManager.currentLightEstimation;
            }

            return LightEstimation.None;
        }
    }
}

#endif
