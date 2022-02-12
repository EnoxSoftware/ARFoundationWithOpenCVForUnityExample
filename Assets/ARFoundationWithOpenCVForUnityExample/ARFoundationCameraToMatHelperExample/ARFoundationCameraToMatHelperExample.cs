#if !(PLATFORM_LUMIN && !UNITY_EDITOR)

using ARFoundationWithOpenCVForUnity.UnityUtils.Helper;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityUtils.Helper;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ARFoundationWithOpenCVForUnityExample
{
    /// <summary>
    /// ARFoundationCameraToMatHelper Example
    /// </summary>
    [RequireComponent(typeof(ARFoundationCameraToMatHelper))]
    public class ARFoundationCameraToMatHelperExample : MonoBehaviour
    {

        [SerializeField]
        public Camera mainCamera = default;

        /// <summary>
        /// The requested resolution dropdown.
        /// </summary>
        public Dropdown requestedResolutionDropdown;

        /// <summary>
        /// The requested resolution.
        /// </summary>
        public ResolutionPreset requestedResolution = ResolutionPreset._640x480;

        /// <summary>
        /// The requestedFPS dropdown.
        /// </summary>
        public Dropdown requestedFPSDropdown;

        /// <summary>
        /// The requestedFPS.
        /// </summary>
        public FPSPreset requestedFPS = FPSPreset._30;

        /// <summary>
        /// The rotate 90 degree toggle.
        /// </summary>
        public Toggle rotate90DegreeToggle;

        /// <summary>
        /// The flip vertical toggle.
        /// </summary>
        public Toggle flipVerticalToggle;

        /// <summary>
        /// The flip horizontal toggle.
        /// </summary>
        public Toggle flipHorizontalToggle;

        /// <summary>
        /// The texture.
        /// </summary>
        Texture2D texture;

        /// <summary>
        /// The webcam texture to mat helper.
        /// </summary>
        ARFoundationCameraToMatHelper webCamTextureToMatHelper;

        /// <summary>
        /// The FPS monitor.
        /// </summary>
        FpsMonitor fpsMonitor;

        // Use this for initialization
        void Start()
        {
            fpsMonitor = GetComponent<FpsMonitor>();

            webCamTextureToMatHelper = gameObject.GetComponent<ARFoundationCameraToMatHelper>();
            int width, height;
            Dimensions(requestedResolution, out width, out height);
            webCamTextureToMatHelper.requestedWidth = width;
            webCamTextureToMatHelper.requestedHeight = height;
            webCamTextureToMatHelper.requestedFPS = (int)requestedFPS;
            webCamTextureToMatHelper.Initialize();

            // Update GUI state
            requestedResolutionDropdown.value = (int)requestedResolution;
            string[] enumNames = System.Enum.GetNames(typeof(FPSPreset));
            int index = Array.IndexOf(enumNames, requestedFPS.ToString());
            requestedFPSDropdown.value = index;
            rotate90DegreeToggle.isOn = webCamTextureToMatHelper.rotate90Degree;
            flipVerticalToggle.isOn = webCamTextureToMatHelper.flipVertical;
            flipHorizontalToggle.isOn = webCamTextureToMatHelper.flipHorizontal;
        }

        /// <summary>
        /// Raises the webcam texture to mat helper initialized event.
        /// </summary>
        public void OnWebCamTextureToMatHelperInitialized()
        {
            Debug.Log("OnWebCamTextureToMatHelperInitialized");

            Mat webCamTextureMat = webCamTextureToMatHelper.GetMat();

            texture = new Texture2D(webCamTextureMat.cols(), webCamTextureMat.rows(), TextureFormat.RGBA32, false);
            Utils.fastMatToTexture2D(webCamTextureMat, texture);

            gameObject.GetComponent<Renderer>().material.mainTexture = texture;

            gameObject.transform.localScale = new Vector3(webCamTextureMat.cols(), webCamTextureMat.rows(), 1);
            Debug.Log("Screen.width " + Screen.width + " Screen.height " + Screen.height + " Screen.orientation " + Screen.orientation);

            if (fpsMonitor != null)
            {
                fpsMonitor.Add("deviceName", webCamTextureToMatHelper.GetDeviceName().ToString());
                fpsMonitor.Add("width", webCamTextureToMatHelper.GetWidth().ToString());
                fpsMonitor.Add("height", webCamTextureToMatHelper.GetHeight().ToString());
                fpsMonitor.Add("camera fps", webCamTextureToMatHelper.GetFPS().ToString());
                fpsMonitor.Add("isFrontFacing", webCamTextureToMatHelper.IsFrontFacing().ToString());
                fpsMonitor.Add("rotate90Degree", webCamTextureToMatHelper.rotate90Degree.ToString());
                fpsMonitor.Add("flipVertical", webCamTextureToMatHelper.flipVertical.ToString());
                fpsMonitor.Add("flipHorizontal", webCamTextureToMatHelper.flipHorizontal.ToString());
                fpsMonitor.Add("orientation", Screen.orientation.ToString());
                fpsMonitor.Add("autoFocusRequested", webCamTextureToMatHelper.autoFocusRequested.ToString());
                fpsMonitor.Add("autoFocusEnabled", webCamTextureToMatHelper.autoFocusEnabled.ToString());
                fpsMonitor.Add("requestedFacingDirection", webCamTextureToMatHelper.requestedFacingDirection.ToString());
                fpsMonitor.Add("currentFacingDirection", webCamTextureToMatHelper.currentFacingDirection.ToString());
                fpsMonitor.Add("requestedLightEstimation", webCamTextureToMatHelper.requestedLightEstimation.ToString());
                fpsMonitor.Add("currentLightEstimation", webCamTextureToMatHelper.currentLightEstimation.ToString());

                fpsMonitor.Add("GetDisplayRotationAngle", webCamTextureToMatHelper.GetDisplayRotationAngle().ToString());
                fpsMonitor.Add("GetDisplayFlipVertical", webCamTextureToMatHelper.GetDisplayFlipVertical().ToString());
                fpsMonitor.Add("GetDisplayFlipHorizontal", webCamTextureToMatHelper.GetDisplayFlipHorizontal().ToString());
            }


            float width = webCamTextureMat.width();
            float height = webCamTextureMat.height();

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

            double fx;
            double fy;
            double cx;
            double cy;

            Mat camMatrix;

#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

            UnityEngine.XR.ARSubsystems.XRCameraIntrinsics cameraIntrinsics = webCamTextureToMatHelper.GetCameraIntrinsics();

            // Apply the rotate and flip properties of camera helper to the camera intrinsics.
            Vector2 fl = cameraIntrinsics.focalLength;
            Vector2 pp = cameraIntrinsics.principalPoint;
            Vector2Int r = cameraIntrinsics.resolution;

            Matrix4x4 tM = Matrix4x4.Translate(new Vector3(-r.x / 2, -r.y / 2, 0));
            pp = tM.MultiplyPoint3x4(pp);

            Matrix4x4 rotationAndFlipM = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, 0, webCamTextureToMatHelper.rotate90Degree ? 90 : 0),
                new Vector3(webCamTextureToMatHelper.flipHorizontal ? -1 : 1, webCamTextureToMatHelper.flipVertical ? -1 : 1, 1));
            pp = rotationAndFlipM.MultiplyPoint3x4(pp);

            if (webCamTextureToMatHelper.rotate90Degree)
            {
                fl = new Vector2(fl.y, fl.x);
                r = new Vector2Int(r.y, r.x);
            }

            Matrix4x4 _tM = Matrix4x4.Translate(new Vector3(r.x / 2, r.y / 2, 0));
            pp = _tM.MultiplyPoint3x4(pp);

            cameraIntrinsics = new UnityEngine.XR.ARSubsystems.XRCameraIntrinsics(fl, pp, r);


            fx = cameraIntrinsics.focalLength.x;
            fy = cameraIntrinsics.focalLength.y;
            cx = cameraIntrinsics.principalPoint.x;
            cy = cameraIntrinsics.principalPoint.y;

            camMatrix = new Mat(3, 3, CvType.CV_64FC1);
            camMatrix.put(0, 0, fx);
            camMatrix.put(0, 1, 0);
            camMatrix.put(0, 2, cx);
            camMatrix.put(1, 0, 0);
            camMatrix.put(1, 1, fy);
            camMatrix.put(1, 2, cy);
            camMatrix.put(2, 0, 0);
            camMatrix.put(2, 1, 0);
            camMatrix.put(2, 2, 1.0f);

            Debug.Log("Created CameraParameters from the camera intrinsics to be populated if the camera supports intrinsics. \n" + camMatrix.dump() + "\n " + cameraIntrinsics.resolution);

#else // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

            int max_d = (int)Mathf.Max(width, height);
            fx = max_d;
            fy = max_d;
            cx = width / 2.0f;
            cy = height / 2.0f;

            camMatrix = new Mat(3, 3, CvType.CV_64FC1);
            camMatrix.put(0, 0, fx);
            camMatrix.put(0, 1, 0);
            camMatrix.put(0, 2, cx);
            camMatrix.put(1, 0, 0);
            camMatrix.put(1, 1, fy);
            camMatrix.put(1, 2, cy);
            camMatrix.put(2, 0, 0);
            camMatrix.put(2, 1, 0);
            camMatrix.put(2, 2, 1.0f);

            Debug.Log("Created a dummy CameraParameters. \n" + camMatrix.dump() + "\n " + width + " " + height);

#endif // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

        }

        /// <summary>
        /// Raises the webcam texture to mat helper disposed event.
        /// </summary>
        public void OnWebCamTextureToMatHelperDisposed()
        {
            Debug.Log("OnWebCamTextureToMatHelperDisposed");

            if (texture != null)
            {
                Texture2D.Destroy(texture);
                texture = null;
            }
        }

        /// <summary>
        /// Raises the webcam texture to mat helper error occurred event.
        /// </summary>
        /// <param name="errorCode">Error code.</param>
        public void OnWebCamTextureToMatHelperErrorOccurred(WebCamTextureToMatHelper.ErrorCode errorCode)
        {
            Debug.Log("OnWebCamTextureToMatHelperErrorOccurred " + errorCode);

            if (fpsMonitor != null)
            {
                fpsMonitor.consoleText = "ErrorCode: " + errorCode;
            }
        }

        // Update is called once per frame
        void Update()
        {
            if (webCamTextureToMatHelper.IsPlaying() && webCamTextureToMatHelper.DidUpdateThisFrame())
            {

                Mat rgbaMat = webCamTextureToMatHelper.GetMat();

                //Imgproc.putText (rgbaMat, "W:" + rgbaMat.width () + " H:" + rgbaMat.height () + " SO:" + Screen.orientation, new Point (5, rgbaMat.rows () - 10), Imgproc.FONT_HERSHEY_SIMPLEX, 1.0, new Scalar (255, 255, 255, 255), 2, Imgproc.LINE_AA, false);

                Utils.fastMatToTexture2D(rgbaMat, texture);

                if (fpsMonitor != null)
                {
                    var focalLength = webCamTextureToMatHelper.GetCameraIntrinsics().focalLength;
                    var principalPoint = webCamTextureToMatHelper.GetCameraIntrinsics().principalPoint;
                    var resolution = webCamTextureToMatHelper.GetCameraIntrinsics().resolution;
                    fpsMonitor.Add("GetCameraIntrinsics", "\n" + "FL: " + focalLength.x + "x" + focalLength.y + "\n" + "PP: " + principalPoint.x + "x" + principalPoint.y + "\n" + "R: " + resolution.x + "x" + resolution.y);

                    fpsMonitor.Add("GetProjectionMatrix", "\n" + webCamTextureToMatHelper.GetProjectionMatrix().ToString());
                }
            }
        }

        /// <summary>
        /// Raises the destroy event.
        /// </summary>
        void OnDestroy()
        {
            webCamTextureToMatHelper.Dispose();
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
            webCamTextureToMatHelper.Play();
        }

        /// <summary>
        /// Raises the pause button click event.
        /// </summary>
        public void OnPauseButtonClick()
        {
            webCamTextureToMatHelper.Pause();
        }

        /// <summary>
        /// Raises the stop button click event.
        /// </summary>
        public void OnStopButtonClick()
        {
            webCamTextureToMatHelper.Stop();
        }

        /// <summary>
        /// Raises the change camera button click event.
        /// </summary>
        public void OnChangeCameraButtonClick()
        {
            webCamTextureToMatHelper.requestedIsFrontFacing = !webCamTextureToMatHelper.requestedIsFrontFacing;
        }

        /// <summary>
        /// Raises the requested resolution dropdown value changed event.
        /// </summary>
        public void OnRequestedResolutionDropdownValueChanged(int result)
        {
            if ((int)requestedResolution != result)
            {
                requestedResolution = (ResolutionPreset)result;

                int width, height;
                Dimensions(requestedResolution, out width, out height);

                webCamTextureToMatHelper.Initialize(width, height);
            }
        }

        /// <summary>
        /// Raises the requestedFPS dropdown value changed event.
        /// </summary>
        public void OnRequestedFPSDropdownValueChanged(int result)
        {
            string[] enumNames = Enum.GetNames(typeof(FPSPreset));
            int value = (int)System.Enum.Parse(typeof(FPSPreset), enumNames[result], true);

            if ((int)requestedFPS != value)
            {
                requestedFPS = (FPSPreset)value;

                webCamTextureToMatHelper.requestedFPS = (int)requestedFPS;
            }
        }

        /// <summary>
        /// Raises the rotate 90 degree toggle value changed event.
        /// </summary>
        public void OnRotate90DegreeToggleValueChanged()
        {
            if (rotate90DegreeToggle.isOn != webCamTextureToMatHelper.rotate90Degree)
            {
                webCamTextureToMatHelper.rotate90Degree = rotate90DegreeToggle.isOn;

                if (fpsMonitor != null)
                    fpsMonitor.Add("rotate90Degree", webCamTextureToMatHelper.rotate90Degree.ToString());
            }
        }

        /// <summary>
        /// Raises the flip vertical toggle value changed event.
        /// </summary>
        public void OnFlipVerticalToggleValueChanged()
        {
            if (flipVerticalToggle.isOn != webCamTextureToMatHelper.flipVertical)
            {
                webCamTextureToMatHelper.flipVertical = flipVerticalToggle.isOn;

                if (fpsMonitor != null)
                    fpsMonitor.Add("flipVertical", webCamTextureToMatHelper.flipVertical.ToString());
            }
        }

        /// <summary>
        /// Raises the flip horizontal toggle value changed event.
        /// </summary>
        public void OnFlipHorizontalToggleValueChanged()
        {
            if (flipHorizontalToggle.isOn != webCamTextureToMatHelper.flipHorizontal)
            {
                webCamTextureToMatHelper.flipHorizontal = flipHorizontalToggle.isOn;

                if (fpsMonitor != null)
                    fpsMonitor.Add("flipHorizontal", webCamTextureToMatHelper.flipHorizontal.ToString());
            }
        }

        /// <summary>
        /// Raises the change autoFocus button click event.
        /// </summary>
        public void OnChangeAutoFocusButtonClick()
        {
            webCamTextureToMatHelper.autoFocusRequested = !webCamTextureToMatHelper.autoFocusRequested;

            if (fpsMonitor != null)
            {
                fpsMonitor.Add("autoFocusRequested", webCamTextureToMatHelper.autoFocusRequested.ToString());
                fpsMonitor.Add("autoFocusEnabled", webCamTextureToMatHelper.autoFocusEnabled.ToString());
            }
        }

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