#if !(PLATFORM_LUMIN && !UNITY_EDITOR)

using System;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Extensions;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.UI;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnityExample
{
    /// <summary>
    /// ConvertAsync ARFoundationCameraToMat Example
    /// An example of converting an ARFoundation camera image to OpenCV's Mat format.
    /// </summary>
    public class ConvertAsyncARFoundationCameraToMatExample : MonoBehaviour
    {
        // Enums
        public enum ImageProcessingType
        {
            None,
            DrawLine,
            ConvertToGray,
        }

        // Public Fields
        [Header("Output")]
        /// <summary>
        /// The RawImage for previewing the result.
        /// </summary>
        public RawImage ResultPreview;

        [Space(10)]

        [TooltipAttribute("The ARCameraManager which will produce frame events.")]
        public ARCameraManager CameraManager = default;

        [TooltipAttribute("The ARCamera.")]
        public Camera ArCamera;

        [Header("Processing")]
        public ImageProcessingType ProcessingType = ImageProcessingType.None;
        public Dropdown ImageProcessingTypeDropdown;

        // Private Fields
        private Mat _rgbaMat;

        private Mat _rotatedFrameMat;

        private Mat _grayMat;

        private Texture2D _texture;

        private bool _hasInitDone = false;

        private bool _isPlaying = true;

        private ScreenOrientation _screenOrientation;

        private int _displayRotationAngle = 0;
        private bool _displayFlipVertical = false;
        private bool _displayFlipHorizontal = false;

        private FpsMonitor _fpsMonitor;

        // Unity Lifecycle Methods
        private void Start()
        {
            Debug.Assert(CameraManager != null, "camera manager cannot be null");

            _fpsMonitor = GetComponent<FpsMonitor>();

            // Checks camera permission state.
            if (_fpsMonitor != null && !CameraManager.permissionGranted)
            {
                _fpsMonitor.ConsoleText = "Camera permission has not been granted.";
            }

            // Update UI
            if (ImageProcessingTypeDropdown != null)
            {
                ImageProcessingTypeDropdown.value = (int)ProcessingType;
            }
        }

        private void OnEnable()
        {
            if (CameraManager != null)
            {
                CameraManager.frameReceived += OnCameraFrameReceived;
            }
        }

        private void OnDisable()
        {
            if (CameraManager != null)
            {
                CameraManager.frameReceived -= OnCameraFrameReceived;
            }
        }

        private void Update()
        {

        }

        private void OnDestroy()
        {
            Dispose();
        }

        // Public Methods
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
            if (_hasInitDone)
            {
                _isPlaying = true;
            }
        }

        /// <summary>
        /// Raises the pause button click event.
        /// </summary>
        public void OnPauseButtonClick()
        {
            if (_hasInitDone)
            {
                _isPlaying = false;
            }
        }

        /// <summary>
        /// Raises the stop button click event.
        /// </summary>
        public void OnStopButtonClick()
        {
            if (_hasInitDone)
            {
                _isPlaying = false;
            }
        }

        /// <summary>
        /// Raises the change camera button click event.
        /// </summary>
        public void OnChangeCameraButtonClick()
        {
            if (_hasInitDone)
            {
                // https://github.com/Unity-Technologies/arfoundation-samples/blob/main/Assets/Scripts/CameraSwapper.cs
                CameraFacingDirection newFacingDirection;
                switch (CameraManager.requestedFacingDirection)
                {
                    case CameraFacingDirection.World:
                        newFacingDirection = CameraFacingDirection.User;
                        break;
                    case CameraFacingDirection.User:
                    default:
                        newFacingDirection = CameraFacingDirection.World;
                        break;
                }

                Debug.Log($"Switching ARCameraManager.requestedFacingDirection from {CameraManager.requestedFacingDirection} to {newFacingDirection}");
                CameraManager.requestedFacingDirection = newFacingDirection;

                _hasInitDone = false;
            }
        }

        public void OnImageProcessingTypeDropdownValueChanged(int result)
        {
            ProcessingType = (ImageProcessingType)result;
        }

        // Protected Methods
        protected void OnCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
        {
            if ((CameraManager == null) || (CameraManager.subsystem == null) || !CameraManager.subsystem.running)
            {
                return;
            }

            // Attempt to get the latest camera image. If this method succeeds,
            // it acquires a native resource that must be disposed (see below).
            if (!CameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return;
            }

            int width = image.width;
            int height = image.height;

            if (!_hasInitDone || _rgbaMat == null || _rgbaMat.cols() != width || _rgbaMat.rows() != height || _screenOrientation != Screen.orientation)
            {
                Dispose();

                _screenOrientation = Screen.orientation;

                XRCameraConfiguration config = (XRCameraConfiguration)CameraManager.currentConfiguration;
                int framerate = config.framerate.HasValue ? config.framerate.Value : -1;

                Debug.Log("name:" + CameraManager.name + " width:" + width + " height:" + height + " fps:" + framerate);
                Debug.Log(" format:" + image.format + " isFrongFacing:" + (CameraManager.currentFacingDirection == CameraFacingDirection.User));

#if USE_ARFOUNDATION_5
                // Remove scaling and offset factors from the camera display matrix while maintaining orientation.
                // Decompose that matrix to extract the rotation and flipping factors.
                // https://github.com/Unity-Technologies/arfoundation-samples/blob/88179bab2b180dd90229d9ec995204be47da1cc1/Assets/Scripts/DisplayDepthImage.cs#L333
                if (eventArgs.displayMatrix.HasValue)
                {
                    // Copy the display rotation matrix from the camera.
                    Matrix4x4 cameraMatrix = eventArgs.displayMatrix ?? Matrix4x4.identity;

                    Vector2 affineBasisX = new Vector2(1.0f, 0.0f);
                    Vector2 affineBasisY = new Vector2(0.0f, 1.0f);
                    Vector2 affineTranslation = new Vector2(0.0f, 0.0f);

#if UNITY_IOS
                    affineBasisX = new Vector2(cameraMatrix[0, 0], cameraMatrix[1, 0]);
                    affineBasisY = new Vector2(cameraMatrix[0, 1], cameraMatrix[1, 1]);
                    affineTranslation = new Vector2(cameraMatrix[2, 0], cameraMatrix[2, 1]);
#endif // UNITY_IOS
#if UNITY_ANDROID
                    affineBasisX = new Vector2(cameraMatrix[0, 0], cameraMatrix[0, 1]);
                    affineBasisY = new Vector2(cameraMatrix[1, 0], cameraMatrix[1, 1]);
                    affineTranslation = new Vector2(cameraMatrix[0, 2], cameraMatrix[1, 2]);
#endif // UNITY_ANDROID

                    affineBasisX = affineBasisX.normalized;
                    affineBasisY = affineBasisY.normalized;
                    Matrix4x4 m_DisplayRotationMatrix = Matrix4x4.identity;
                    m_DisplayRotationMatrix = Matrix4x4.identity;
                    m_DisplayRotationMatrix[0, 0] = affineBasisX.x;
                    m_DisplayRotationMatrix[0, 1] = affineBasisY.x;
                    m_DisplayRotationMatrix[1, 0] = affineBasisX.y;
                    m_DisplayRotationMatrix[1, 1] = affineBasisY.y;

#if UNITY_IOS
                    Matrix4x4 FlipYMatrix = Matrix4x4.Scale(new Vector3(1, -1, 1));
                    m_DisplayRotationMatrix = FlipYMatrix.inverse * m_DisplayRotationMatrix;
#endif // UNITY_IOS

                    _displayRotationAngle = (int)OpenCVARUtils.ExtractRotationFromMatrix(ref m_DisplayRotationMatrix).eulerAngles.z;
                    Vector3 localScale = OpenCVARUtils.ExtractScaleFromMatrix(ref m_DisplayRotationMatrix);
                    _displayFlipVertical = Mathf.Sign(localScale.y) == -1;
                    _displayFlipHorizontal = Mathf.Sign(localScale.x) == -1;


                    if (_fpsMonitor != null)
                    {
                        _fpsMonitor.Add("displayMatrix", "\n" + eventArgs.displayMatrix.ToString());
                        _fpsMonitor.Add("displayRotationAngle", _displayRotationAngle.ToString());
                        _fpsMonitor.Add("displayFlipVertical", _displayFlipVertical.ToString());
                        _fpsMonitor.Add("displayFlipHorizontal", _displayFlipHorizontal.ToString());
                    }
                }
#else
                // Remove scaling and offset factors from the camera display matrix while maintaining orientation.
                // Decompose that matrix to extract the rotation and flipping factors.
                // https://github.com/Unity-Technologies/arfoundation-samples/blob/362a2596f35b9e45cb73920a9f8fffb1777e7d36/Assets/Scripts/Runtime/Occlusion/DisplayDepthImage.cs#L358
                if (eventArgs.displayMatrix.HasValue)
                {
                    // Copy the display rotation matrix from the camera.
                    Matrix4x4 cameraMatrix = eventArgs.displayMatrix ?? Matrix4x4.identity;

                    Vector2 affineBasisX = new Vector2(cameraMatrix[0, 0], cameraMatrix[1, 0]);
                    Vector2 affineBasisY = new Vector2(cameraMatrix[0, 1], cameraMatrix[1, 1]);
                    Vector2 affineTranslation = new Vector2(cameraMatrix[2, 0], cameraMatrix[2, 1]);
                    affineBasisX = affineBasisX.normalized;
                    affineBasisY = affineBasisY.normalized;
                    Matrix4x4 m_DisplayRotationMatrix = Matrix4x4.identity;
                    m_DisplayRotationMatrix[0, 0] = affineBasisX.x;
                    m_DisplayRotationMatrix[0, 1] = affineBasisY.x;
                    m_DisplayRotationMatrix[1, 0] = affineBasisX.y;
                    m_DisplayRotationMatrix[1, 1] = affineBasisY.y;

                    Matrix4x4 FlipYMatrix = Matrix4x4.Scale(new Vector3(1, -1, 1));
                    m_DisplayRotationMatrix = FlipYMatrix.inverse * m_DisplayRotationMatrix;

                    _displayRotationAngle = (int)OpenCVARUtils.ExtractRotationFromMatrix(ref m_DisplayRotationMatrix).eulerAngles.z;
                    Vector3 localScale = OpenCVARUtils.ExtractScaleFromMatrix(ref m_DisplayRotationMatrix);
                    _displayFlipVertical = Mathf.Sign(localScale.y) == -1;
                    _displayFlipHorizontal = Mathf.Sign(localScale.x) == -1;

                    if (_fpsMonitor != null)
                    {
                        _fpsMonitor.Add("displayMatrix", "\n" + eventArgs.displayMatrix.ToString());
                        _fpsMonitor.Add("displayRotationAngle", _displayRotationAngle.ToString());
                        _fpsMonitor.Add("displayFlipVertical", _displayFlipVertical.ToString());
                        _fpsMonitor.Add("displayFlipHorizontal", _displayFlipHorizontal.ToString());
                    }
                }
#endif // USE_ARFOUNDATION_5

                /*
                // Generate a camera matrix from cameraIntrinsics values.
                if (CameraManager.TryGetIntrinsics(out var cameraIntrinsics))
                {
                    var focalLength = cameraIntrinsics.focalLength;
                    var principalPoint = cameraIntrinsics.principalPoint;

                    Mat cameraMatrix = new Mat(3, 3, CvType.CV_64FC1);
                    cameraMatrix.put(0, 0, new double[] { focalLength.x, 0, principalPoint.x, 0, focalLength.y, principalPoint.y, 0, 0, 1.0f });

                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("cameraMatrix", "\n" + cameraMatrix.dump());
                    }
                }
                */

                _rgbaMat = new Mat(height, width, CvType.CV_8UC4);

                if (_displayRotationAngle == 90 || _displayRotationAngle == 270)
                {
                    width = image.height;
                    height = image.width;

                    _rotatedFrameMat = new Mat(height, width, CvType.CV_8UC4);
                }

                _grayMat = new Mat(height, width, CvType.CV_8UC1);
                _texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

                ResultPreview.texture = _texture;
                ResultPreview.GetComponent<AspectRatioFitter>().aspectRatio = (float)_texture.width / _texture.height;

                _hasInitDone = true;

                if (_fpsMonitor != null)
                {
                    _fpsMonitor.Add("width", image.width.ToString());
                    _fpsMonitor.Add("height", image.height.ToString());
                    _fpsMonitor.Add("framerate", framerate.ToString());
                    _fpsMonitor.Add("format", image.format.ToString());
                    _fpsMonitor.Add("orientation", Screen.orientation.ToString());

                    //fpsMonitor.Add("FormatSupported", image.FormatSupported(TextureFormat.RGBA32).ToString());
                    //XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32);
                    //fpsMonitor.Add("GetConvertedDataSize", image.GetConvertedDataSize(conversionParams).ToString());
                }
            }

            if (_hasInitDone && _isPlaying)
            {
                XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32, XRCpuImage.Transformation.None);
                image.ConvertAsync(conversionParams, ProcessImage);

                if (_fpsMonitor != null)
                {
                    _fpsMonitor.Add("currentFacingDirection", CameraManager.currentFacingDirection.ToString());
                    _fpsMonitor.Add("autoFocusEnabled", CameraManager.autoFocusEnabled.ToString());
                    _fpsMonitor.Add("currentLightEstimation", CameraManager.currentLightEstimation.ToString());
                }

                if (CameraManager.TryGetIntrinsics(out var cameraIntrinsics))
                {
                    var focalLength = cameraIntrinsics.focalLength;
                    var principalPoint = cameraIntrinsics.principalPoint;

                    if (_fpsMonitor != null)
                    {
                        _fpsMonitor.Add("cameraIntrinsics", "\n" + "FL: " + focalLength.x + "x" + focalLength.y + "\n" + "PP: " + principalPoint.x + "x" + principalPoint.y);
                    }
                }

                if (eventArgs.projectionMatrix.HasValue)
                {
                    if (_fpsMonitor != null)
                    {
                        _fpsMonitor.Add("projectionMatrix", "\n" + eventArgs.projectionMatrix.ToString());
                    }
                }

                if (eventArgs.timestampNs.HasValue)
                {
                    if (_fpsMonitor != null)
                    {
                        _fpsMonitor.Add("timestampNs", eventArgs.timestampNs.ToString());
                    }
                }

                /*
                if (ArCamera != null)
                {
                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("ARCamera_projectionMatrix", "\n" + ArCamera.projectionMatrix.ToString());
                        fpsMonitor.Add("ARCamera_worldToCameraMatrix", "\n" + ArCamera.worldToCameraMatrix.ToString());
                    }
                }
                */
            }

            image.Dispose();
        }

        protected void DisplayImage()
        {
            if (_displayFlipVertical && _displayFlipHorizontal)
            {
                Core.flip(_rgbaMat, _rgbaMat, -1);
            }
            else if (_displayFlipVertical)
            {
                Core.flip(_rgbaMat, _rgbaMat, 0);
            }
            else if (_displayFlipHorizontal)
            {
                Core.flip(_rgbaMat, _rgbaMat, 1);
            }

            if (_rotatedFrameMat != null)
            {
                if (_displayRotationAngle == 90)
                {
                    Core.rotate(_rgbaMat, _rotatedFrameMat, Core.ROTATE_90_CLOCKWISE);
                }
                else if (_displayRotationAngle == 270)
                {
                    Core.rotate(_rgbaMat, _rotatedFrameMat, Core.ROTATE_90_COUNTERCLOCKWISE);
                }

                ProcessImage(_rotatedFrameMat, _grayMat, ProcessingType);
                OpenCVMatUnityUtils.MatToTexture2D(_rotatedFrameMat, _texture);
            }
            else
            {
                if (_displayRotationAngle == 180)
                {
                    Core.rotate(_rgbaMat, _rgbaMat, Core.ROTATE_180);
                }

                ProcessImage(_rgbaMat, _grayMat, ProcessingType);
                OpenCVMatUnityUtils.MatToTexture2D(_rgbaMat, _texture);
            }
        }

        protected void ProcessImage(Mat frameMatrix, Mat grayMatrix, ImageProcessingType imageProcessingType)
        {
            switch (imageProcessingType)
            {
                case ImageProcessingType.DrawLine:
                    Imgproc.line(
                        frameMatrix,
                        new Point(0, 0),
                        new Point(frameMatrix.cols(), frameMatrix.rows()),
                        new Scalar(255, 0, 0, 255),
                        4
                    );
                    break;
                case ImageProcessingType.ConvertToGray:
                    Imgproc.cvtColor(frameMatrix, grayMatrix, Imgproc.COLOR_RGBA2GRAY);
                    Imgproc.cvtColor(grayMatrix, frameMatrix, Imgproc.COLOR_GRAY2RGBA);
                    break;
            }
        }

        // Private Methods
        private void ProcessImage(XRCpuImage.AsyncConversionStatus status, XRCpuImage.ConversionParams conversionParams, NativeArray<byte> data)
        {
            if (status != XRCpuImage.AsyncConversionStatus.Ready)
            {
                Debug.LogErrorFormat("Async request failed with status {0}", status);
                return;
            }

            if (_hasInitDone)
            {
                MatBufferUtils.CopyToMat<byte>(data, _rgbaMat);

                DisplayImage();
            }
        }

        /// <summary>
        /// Releases all resource.
        /// </summary>
        private void Dispose()
        {
            _hasInitDone = false;

            _rgbaMat?.Dispose();
            _rgbaMat = null;

            _rotatedFrameMat?.Dispose();
            _rotatedFrameMat = null;

            _grayMat?.Dispose();
            _grayMat = null;

            if (_texture != null)
            {
                Texture2D.Destroy(_texture);
                _texture = null;
            }
        }
    }
}

#endif
