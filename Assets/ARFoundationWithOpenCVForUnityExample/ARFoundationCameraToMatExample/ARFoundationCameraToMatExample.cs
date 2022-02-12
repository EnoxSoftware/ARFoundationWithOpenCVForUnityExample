#if !(PLATFORM_LUMIN && !UNITY_EDITOR)

using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnityExample
{
    /// <summary>
    /// ARFoundationCameraToMat Example
    /// An example of converting an ARFoundation camera image to OpenCV's Mat format.
    /// </summary>
    public class ARFoundationCameraToMatExample : MonoBehaviour
    {
        [SerializeField, TooltipAttribute("The ARCameraManager which will produce frame events.")]
        public ARCameraManager cameraManager = default;

        [SerializeField, TooltipAttribute("The ARCamera.")]
        public Camera arCamera;

        [SerializeField]
        public Camera mainCamera = default;

        [Header("Processing")]
        public ImageProcessingType imageProcessingType = ImageProcessingType.None;
        public Dropdown imageProcessingTypeDropdown;

        Mat rgbaMat;

        Mat rotatedFrameMat;

        Mat grayMat;

        Texture2D texture;

        bool hasInitDone = false;

        bool isPlaying = true;

        ScreenOrientation screenOrientation;

        int displayRotationAngle = 0;
        bool displayFlipVertical = false;
        bool displayFlipHorizontal = false;

        FpsMonitor fpsMonitor;

        // Use this for initialization
        void Start()
        {
            Debug.Assert(cameraManager != null, "camera manager cannot be null");

            fpsMonitor = GetComponent<FpsMonitor>();


            // Checks camera permission state.
            if (fpsMonitor != null && !cameraManager.permissionGranted)
            {
                fpsMonitor.consoleText = "Camera permission has not been granted.";
            }

            // Update UI
            if (imageProcessingTypeDropdown != null)
                imageProcessingTypeDropdown.value = (int)imageProcessingType;
        }

        void OnEnable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived += OnCameraFrameReceived;
            }
        }

        void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= OnCameraFrameReceived;
            }
        }

        protected void OnCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
        {
            if ((cameraManager == null) || (cameraManager.subsystem == null) || !cameraManager.subsystem.running)
                return;

            // Attempt to get the latest camera image. If this method succeeds,
            // it acquires a native resource that must be disposed (see below).
            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return;
            }

            int width = image.width;
            int height = image.height;

            if (!hasInitDone || rgbaMat == null || rgbaMat.cols() != width || rgbaMat.rows() != height || screenOrientation != Screen.orientation)
            {
                Dispose();

                screenOrientation = Screen.orientation;

                XRCameraConfiguration config = (XRCameraConfiguration)cameraManager.currentConfiguration;
                int framerate = config.framerate.HasValue ? config.framerate.Value : -1;

                Debug.Log("name:" + cameraManager.name + " width:" + width + " height:" + height + " fps:" + framerate);
                Debug.Log(" format:" + image.format + " isFrongFacing:" + (cameraManager.currentFacingDirection == CameraFacingDirection.User));


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

                    displayRotationAngle = (int)ARUtils.ExtractRotationFromMatrix(ref m_DisplayRotationMatrix).eulerAngles.z;
                    Vector3 localScale = ARUtils.ExtractScaleFromMatrix(ref m_DisplayRotationMatrix);
                    displayFlipVertical = Mathf.Sign(localScale.y) == -1;
                    displayFlipHorizontal = Mathf.Sign(localScale.x) == -1;


                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("displayMatrix", "\n" + eventArgs.displayMatrix.ToString());
                        fpsMonitor.Add("displayRotationAngle", displayRotationAngle.ToString());
                        fpsMonitor.Add("displayFlipVertical", displayFlipVertical.ToString());
                        fpsMonitor.Add("displayFlipHorizontal", displayFlipHorizontal.ToString());
                    }

                }

                /*
                // Generate a camera matrix from cameraIntrinsics values.
                if (cameraManager.TryGetIntrinsics(out var cameraIntrinsics))
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

                rgbaMat = new Mat(height, width, CvType.CV_8UC4);

                if (displayRotationAngle == 90 || displayRotationAngle == 270)
                {
                    width = image.height;
                    height = image.width;

                    rotatedFrameMat = new Mat(height, width, CvType.CV_8UC4);
                }

                grayMat = new Mat(height, width, CvType.CV_8UC1);
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);


                gameObject.GetComponent<Renderer>().material.mainTexture = texture;

                gameObject.transform.localScale = new Vector3(width, height, 1);
                Debug.Log("Screen.width " + Screen.width + " Screen.height " + Screen.height + " Screen.orientation " + Screen.orientation);

                float widthScale = (float)Screen.width / width;
                float heightScale = (float)Screen.height / height;
                if (widthScale < heightScale)
                {
                    mainCamera.orthographicSize = (width * (float)Screen.height / (float)Screen.width) / 2;
                }
                else
                {
                    mainCamera.orthographicSize = height / 2;
                }

                hasInitDone = true;


                if (fpsMonitor != null)
                {
                    fpsMonitor.Add("width", image.width.ToString());
                    fpsMonitor.Add("height", image.height.ToString());
                    fpsMonitor.Add("framerate", framerate.ToString());
                    fpsMonitor.Add("format", image.format.ToString());
                    fpsMonitor.Add("orientation", Screen.orientation.ToString());

                    //fpsMonitor.Add("FormatSupported", image.FormatSupported(TextureFormat.RGBA32).ToString());
                    //XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32);
                    //fpsMonitor.Add("GetConvertedDataSize", image.GetConvertedDataSize(conversionParams).ToString());
                }

            }

            if (hasInitDone && isPlaying)
            {
                XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32, XRCpuImage.Transformation.None);
                image.Convert(conversionParams, (IntPtr)rgbaMat.dataAddr(), (int)rgbaMat.total() * (int)rgbaMat.elemSize());

                DisplayImage();

                if (fpsMonitor != null)
                {
                    fpsMonitor.Add("currentFacingDirection", cameraManager.currentFacingDirection.ToString());
                    fpsMonitor.Add("autoFocusEnabled", cameraManager.autoFocusEnabled.ToString());
                    fpsMonitor.Add("currentLightEstimation", cameraManager.currentLightEstimation.ToString());
                }

                if (cameraManager.TryGetIntrinsics(out var cameraIntrinsics))
                {
                    var focalLength = cameraIntrinsics.focalLength;
                    var principalPoint = cameraIntrinsics.principalPoint;

                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("cameraIntrinsics", "\n" + "FL: " + focalLength.x + "x" + focalLength.y + "\n" + "PP: " + principalPoint.x + "x" + principalPoint.y);
                    }
                }

                if (eventArgs.projectionMatrix.HasValue)
                {
                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("projectionMatrix", "\n" + eventArgs.projectionMatrix.ToString());
                    }
                }

                if (eventArgs.timestampNs.HasValue)
                {
                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("timestampNs", eventArgs.timestampNs.ToString());
                    }
                }

                /*
                if (arCamera != null)
                {
                    if (fpsMonitor != null)
                    {
                        fpsMonitor.Add("ARCamera_projectionMatrix", "\n" + arCamera.projectionMatrix.ToString());
                        fpsMonitor.Add("ARCamera_worldToCameraMatrix", "\n" + arCamera.worldToCameraMatrix.ToString());
                    }
                }
                */
            }

            image.Dispose();
        }

        protected void DisplayImage()
        {
            if (displayFlipVertical && displayFlipHorizontal)
            {
                Core.flip(rgbaMat, rgbaMat, -1);
            }
            else if (displayFlipVertical)
            {
                Core.flip(rgbaMat, rgbaMat, 0);
            }
            else if (displayFlipHorizontal)
            {
                Core.flip(rgbaMat, rgbaMat, 1);
            }

            if (rotatedFrameMat != null)
            {
                if (displayRotationAngle == 90)
                {
                    Core.rotate(rgbaMat, rotatedFrameMat, Core.ROTATE_90_CLOCKWISE);
                }
                else if (displayRotationAngle == 270)
                {
                    Core.rotate(rgbaMat, rotatedFrameMat, Core.ROTATE_90_COUNTERCLOCKWISE);
                }

                ProcessImage(rotatedFrameMat, grayMat, imageProcessingType);
                Utils.fastMatToTexture2D(rotatedFrameMat, texture);
            }
            else
            {
                if (displayRotationAngle == 180)
                {
                    Core.rotate(rgbaMat, rgbaMat, Core.ROTATE_180);
                }

                ProcessImage(rgbaMat, grayMat, imageProcessingType);
                Utils.fastMatToTexture2D(rgbaMat, texture);
            }
        }

        /// <summary>
        /// Releases all resource.
        /// </summary>
        private void Dispose()
        {
            hasInitDone = false;

            if (rgbaMat != null)
            {
                rgbaMat.Dispose();
                rgbaMat = null;
            }
            if (rotatedFrameMat != null)
            {
                rotatedFrameMat.Dispose();
                rotatedFrameMat = null;
            }
            if (grayMat != null)
            {
                grayMat.Dispose();
                grayMat = null;
            }
            if (texture != null)
            {
                Texture2D.Destroy(texture);
                texture = null;
            }
        }

        // Update is called once per frame
        void Update()
        {

        }

        /// <summary>
        /// Raises the destroy event.
        /// </summary>
        void OnDestroy()
        {
            Dispose();
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
            if (hasInitDone)
                isPlaying = true;
        }

        /// <summary>
        /// Raises the pause button click event.
        /// </summary>
        public void OnPauseButtonClick()
        {
            if (hasInitDone)
                isPlaying = false;
        }

        /// <summary>
        /// Raises the stop button click event.
        /// </summary>
        public void OnStopButtonClick()
        {
            if (hasInitDone)
                isPlaying = false;
        }

        /// <summary>
        /// Raises the change camera button click event.
        /// </summary>
        public void OnChangeCameraButtonClick()
        {
            if (hasInitDone)
            {
                // https://github.com/Unity-Technologies/arfoundation-samples/blob/main/Assets/Scripts/CameraSwapper.cs
                CameraFacingDirection newFacingDirection;
                switch (cameraManager.requestedFacingDirection)
                {
                    case CameraFacingDirection.World:
                        newFacingDirection = CameraFacingDirection.User;
                        break;
                    case CameraFacingDirection.User:
                    default:
                        newFacingDirection = CameraFacingDirection.World;
                        break;
                }

                Debug.Log($"Switching ARCameraManager.requestedFacingDirection from {cameraManager.requestedFacingDirection} to {newFacingDirection}");
                cameraManager.requestedFacingDirection = newFacingDirection;

                hasInitDone = false;
            }
        }

        public void OnImageProcessingTypeDropdownValueChanged(int result)
        {
            imageProcessingType = (ImageProcessingType)result;
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
    }

    public enum ImageProcessingType
    {
        None,
        DrawLine,
        ConvertToGray,
    }
}

#endif