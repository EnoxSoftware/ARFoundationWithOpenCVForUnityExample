#if !(PLATFORM_LUMIN && !UNITY_EDITOR)

using ARFoundationWithOpenCVForUnity.UnityUtils.Helper;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityUtils.Helper;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnityExample
{
    /// <summary>
    /// ARFoundationCamera ArUco Example
    /// An example of ArUco marker detection from an ARFoundation camera image.
    /// </summary>
    [RequireComponent(typeof(ARFoundationCameraToMatHelper))]
    public class ARFoundationCameraArUcoExample : MonoBehaviour
    {

        /// <summary>
        /// The raw camera image.
        /// </summary>
        public RawImage rawCameraImage;

        /// <summary>
        /// The dictionary identifier.
        /// </summary>
        public ArUcoDictionary dictionaryId = ArUcoDictionary.DICT_6X6_250;

        /// <summary>
        /// The dictionary id dropdown.
        /// </summary>
        public Dropdown dictionaryIdDropdown;

        /// <summary>
        /// Determines if applied the pose estimation.
        /// </summary>
        public bool applyEstimationPose = true;

        /// <summary>
        /// The apply estimation pose toggle.
        /// </summary>
        public Toggle applyEstimationPoseToggle;

        [Space(10)]

        /// <summary>
        /// The length of the markers' side. Normally, unit is meters.
        /// </summary>
        public float markerLength = 0.188f;

        /// <summary>
        /// The AR game object.
        /// </summary>
        public ARGameObject arGameObject;

        /// <summary>
        /// The AR camera.
        /// </summary>
        public Camera arCamera;

        [Space(10)]

        /// <summary>
        /// Determines if enable leap filter.
        /// </summary>
        public bool enableLerpFilter;

        /// <summary>
        /// The enable leap filter toggle.
        /// </summary>
        public Toggle enableLerpFilterToggle;

        /// <summary>
        /// The texture.
        /// </summary>
        Texture2D texture;

        /// <summary>
        /// The webcam texture to mat helper.
        /// </summary>
        ARFoundationCameraToMatHelper webCamTextureToMatHelper;

        /// <summary>
        /// The rgb mat.
        /// </summary>
        Mat rgbMat;

        /// <summary>
        /// The cameraparam matrix.
        /// </summary>
        Mat camMatrix;

        /// <summary>
        /// The distortion coeffs.
        /// </summary>
        MatOfDouble distCoeffs;

        /// <summary>
        /// The transformation matrix for AR.
        /// </summary>
        Matrix4x4 ARM;

        /// <summary>
        /// The FPS monitor.
        /// </summary>
        FpsMonitor fpsMonitor;

        // for CanonicalMarker.
        Mat ids;
        List<Mat> corners;
        List<Mat> rejectedCorners;
        Dictionary dictionary;
        ArucoDetector arucoDetector;

        Mat rvecs;
        Mat tvecs;


        Matrix4x4 fitARFoundationBackgroundMatrix;
        Matrix4x4 fitHelpersFlipMatrix;


        // Use this for initialization
        void Start()
        {
            fpsMonitor = GetComponent<FpsMonitor>();

            webCamTextureToMatHelper = gameObject.GetComponent<ARFoundationCameraToMatHelper>();
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            webCamTextureToMatHelper.frameMatAcquired += OnFrameMatAcquired;
#endif // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            webCamTextureToMatHelper.Initialize();

            dictionaryIdDropdown.value = (int)dictionaryId;
            applyEstimationPoseToggle.isOn = applyEstimationPose;
            enableLerpFilterToggle.isOn = enableLerpFilter;
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

            rawCameraImage.texture = texture;
            float heightScale = (float)webCamTextureMat.height() / webCamTextureMat.width();
            rawCameraImage.rectTransform.sizeDelta = new Vector2(640, 640 * heightScale);

            Debug.Log("Screen.width " + Screen.width + " Screen.height " + Screen.height + " Screen.orientation " + Screen.orientation);

            if (fpsMonitor != null)
            {
                fpsMonitor.Add("width", webCamTextureMat.width().ToString());
                fpsMonitor.Add("height", webCamTextureMat.height().ToString());
                fpsMonitor.Add("orientation", Screen.orientation.ToString());
                fpsMonitor.Add("IsFrontFacing", webCamTextureToMatHelper.IsFrontFacing().ToString());
                fpsMonitor.Add("requestedFacingDirection", webCamTextureToMatHelper.requestedFacingDirection.ToString());
                fpsMonitor.Add("currentFacingDirection", webCamTextureToMatHelper.currentFacingDirection.ToString());
                fpsMonitor.Add("requestedLightEstimation", webCamTextureToMatHelper.requestedLightEstimation.ToString());
                fpsMonitor.Add("currentLightEstimation", webCamTextureToMatHelper.currentLightEstimation.ToString());
            }


            // If the WebCam is front facing, flip the Mat horizontally. Required for successful detection of AR markers.
            if (webCamTextureToMatHelper.IsFrontFacing() && !webCamTextureToMatHelper.flipHorizontal)
            {
                webCamTextureToMatHelper.flipHorizontal = true;
            }
            else if (!webCamTextureToMatHelper.IsFrontFacing() && webCamTextureToMatHelper.flipHorizontal)
            {
                webCamTextureToMatHelper.flipHorizontal = false;
            }


            // set camera parameters.
            double fx;
            double fy;
            double cx;
            double cy;

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

            distCoeffs = new MatOfDouble(0, 0, 0, 0);

            Debug.Log("Created CameraParameters from the camera intrinsics to be populated if the camera supports intrinsics.");

            var focalLength = cameraIntrinsics.focalLength;
            var principalPoint = cameraIntrinsics.principalPoint;
            var resolution = cameraIntrinsics.resolution;

            if (fpsMonitor != null)
            {
                fpsMonitor.Add("cameraIntrinsics", "\n" + "FL: " + focalLength.x + "x" + focalLength.y + "\n" + "PP: " + principalPoint.x + "x" + principalPoint.y + "\n" + "R: " + resolution.x + "x" + resolution.y);
            }

#else // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

            float width = webCamTextureMat.width();
            float height = webCamTextureMat.height();

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

            distCoeffs = new MatOfDouble(0, 0, 0, 0);

            Debug.Log("Created a dummy CameraParameters.");

#endif // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

            Debug.Log("camMatrix " + camMatrix.dump());
            Debug.Log("distCoeffs " + distCoeffs.dump());


            rgbMat = new Mat(webCamTextureMat.rows(), webCamTextureMat.cols(), CvType.CV_8UC3);


            ids = new Mat();
            corners = new List<Mat>();
            rejectedCorners = new List<Mat>();
            rvecs = new Mat(1, 10, CvType.CV_64FC3);
            tvecs = new Mat(1, 10, CvType.CV_64FC3);
            dictionary = Objdetect.getPredefinedDictionary((int)dictionaryId);

            DetectorParameters detectorParams = new DetectorParameters();
            detectorParams.set_useAruco3Detection(true);
            RefineParameters refineParameters = new RefineParameters(10f, 3f, true);
            arucoDetector = new ArucoDetector(dictionary, detectorParams, refineParameters);


            // Create the transform matrix to fit the ARM to the background display by ARFoundationBackground component.
            fitARFoundationBackgroundMatrix = Matrix4x4.Scale(new Vector3(webCamTextureToMatHelper.GetDisplayFlipHorizontal() ? -1 : 1, webCamTextureToMatHelper.GetDisplayFlipVertical() ? -1 : 1, 1)) * Matrix4x4.identity;

            // Create the transform matrix to fit the ARM to the flip properties of the camera helper.
            fitHelpersFlipMatrix = Matrix4x4.Scale(new Vector3(webCamTextureToMatHelper.flipHorizontal ? -1 : 1, webCamTextureToMatHelper.flipVertical ? -1 : 1, 1)) * Matrix4x4.identity;
        }

        /// <summary>
        /// Raises the webcam texture to mat helper disposed event.
        /// </summary>
        public void OnWebCamTextureToMatHelperDisposed()
        {
            Debug.Log("OnWebCamTextureToMatHelperDisposed");

            if (rgbMat != null)
                rgbMat.Dispose();

            if (texture != null)
            {
                Texture2D.Destroy(texture);
                texture = null;
            }

            if (ids != null)
                ids.Dispose();
            foreach (var item in corners)
            {
                item.Dispose();
            }
            corners.Clear();
            foreach (var item in rejectedCorners)
            {
                item.Dispose();
            }
            rejectedCorners.Clear();
            if (rvecs != null)
                rvecs.Dispose();
            if (tvecs != null)
                tvecs.Dispose();
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
                fpsMonitor.consoleText = errorCode.ToString();
            }
        }

#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

        void OnFrameMatAcquired(Mat mat, Matrix4x4 projectionMatrix, Matrix4x4 cameraToWorldMatrix, XRCameraIntrinsics cameraIntrinsics, long timestamp)
        {
            Mat rgbaMat = mat;

            Imgproc.cvtColor(rgbaMat, rgbMat, Imgproc.COLOR_RGBA2RGB);

            // detect markers.
            Calib3d.undistort(rgbMat, rgbMat, camMatrix, distCoeffs);
            arucoDetector.detectMarkers(rgbMat, corners, ids, rejectedCorners);

            // if at least one marker detected
            if (ids.total() > 0)
            {
                // draw markers.
                Objdetect.drawDetectedMarkers(rgbMat, corners, ids, new Scalar(0, 255, 0));

                // estimate pose.
                if (applyEstimationPose)
                {
                    EstimatePoseCanonicalMarker(rgbMat);
                }
            }

            //Imgproc.putText (rgbMat, "W:" + rgbMat.width () + " H:" + rgbMat.height () + " SO:" + Screen.orientation, new Point (5, rgbMat.rows () - 10), Imgproc.FONT_HERSHEY_SIMPLEX, 1.0, new Scalar (255, 255, 255, 255), 2, Imgproc.LINE_AA, false);

            Imgproc.cvtColor(rgbMat, rgbaMat, Imgproc.COLOR_RGB2RGBA);

            Utils.matToTexture2D(rgbaMat, texture);
        }

#else // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

        // Update is called once per frame
        void Update()
        {
            if (webCamTextureToMatHelper.IsPlaying() && webCamTextureToMatHelper.DidUpdateThisFrame())
            {

                Mat rgbaMat = webCamTextureToMatHelper.GetMat();

                Imgproc.cvtColor(rgbaMat, rgbMat, Imgproc.COLOR_RGBA2RGB);

                // detect markers.
                Calib3d.undistort(rgbMat, rgbMat, camMatrix, distCoeffs);
                arucoDetector.detectMarkers(rgbMat, corners, ids, rejectedCorners);

                // if at least one marker detected
                if (ids.total() > 0)
                {
                    // draw markers.
                    Objdetect.drawDetectedMarkers(rgbMat, corners, ids, new Scalar(0, 255, 0));

                    // estimate pose.
                    if (applyEstimationPose)
                    {
                        EstimatePoseCanonicalMarker(rgbMat);
                    }
                }

                //Imgproc.putText (rgbMat, "W:" + rgbMat.width () + " H:" + rgbMat.height () + " SO:" + Screen.orientation, new Point (5, rgbMat.rows () - 10), Imgproc.FONT_HERSHEY_SIMPLEX, 1.0, new Scalar (255, 255, 255, 255), 2, Imgproc.LINE_AA, false);

                Imgproc.cvtColor(rgbMat, rgbaMat, Imgproc.COLOR_RGB2RGBA);

                Utils.matToTexture2D(rgbaMat, texture);
            }
        }

#endif // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

        private void EstimatePoseCanonicalMarker(Mat rgbMat)
        {
            using (MatOfPoint3f objPoints = new MatOfPoint3f(
                new Point3(-markerLength / 2f, markerLength / 2f, 0),
                new Point3(markerLength / 2f, markerLength / 2f, 0),
                new Point3(markerLength / 2f, -markerLength / 2f, 0),
                new Point3(-markerLength / 2f, -markerLength / 2f, 0)
            ))
            {
                for (int i = 0; i < ids.total(); i++)
                {
                    using (Mat rvec = new Mat(1, 1, CvType.CV_64FC3))
                    using (Mat tvec = new Mat(1, 1, CvType.CV_64FC3))
                    using (Mat corner_4x1 = corners[i].reshape(2, 4)) // 1*4*CV_32FC2 => 4*1*CV_32FC2
                    using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner_4x1))
                    {
                        // Calculate pose for each marker
                        Calib3d.solvePnP(objPoints, imagePoints, camMatrix, distCoeffs, rvec, tvec);

                        // In this example we are processing with RGB color image, so Axis-color correspondences are X: blue, Y: green, Z: red. (Usually X: red, Y: green, Z: blue)
                        Calib3d.drawFrameAxes(rgbMat, camMatrix, distCoeffs, rvec, tvec, markerLength * 0.5f);

                        // This example can display the ARObject on only first detected marker.
                        if (i == 0)
                        {
                            UpdateARObjectTransform(rvec, tvec);
                        }
                    }
                }
            }
        }

        private void UpdateARObjectTransform(Mat rvec, Mat tvec)
        {
            // Convert to unity pose data.
            double[] rvecArr = new double[3];
            rvec.get(0, 0, rvecArr);
            double[] tvecArr = new double[3];
            tvec.get(0, 0, tvecArr);
            PoseData poseData = ARUtils.ConvertRvecTvecToPoseData(rvecArr, tvecArr);

            // Convert to transform matrix.
            ARM = ARUtils.ConvertPoseDataToMatrix(ref poseData, true);

            // Apply the effect (flipping factors) of the projection matrix applied to the ARCamera by the ARFoundationBackground component to the ARM.
            ARM = fitARFoundationBackgroundMatrix * ARM;

            // When detecting the AR marker from a horizontal inverted image (front facing camera),
            // will need to apply an inverted X matrix to the transform matrix to match the ARFoundationBackground component display.
            ARM = fitHelpersFlipMatrix * ARM;

            ARM = arCamera.transform.localToWorldMatrix * ARM;

            if (enableLerpFilter)
            {
                arGameObject.SetMatrix4x4(ARM);
            }
            else
            {
                ARUtils.SetTransformFromMatrix(arGameObject.transform, ref ARM);
            }
        }

        private void ResetObjectTransform()
        {
            // reset AR object transform.
            Matrix4x4 i = Matrix4x4.identity;
            ARUtils.SetTransformFromMatrix(arGameObject.transform, ref i);
        }

        /// <summary>
        /// Raises the destroy event.
        /// </summary>
        void OnDestroy()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            webCamTextureToMatHelper.frameMatAcquired -= OnFrameMatAcquired;
#endif // (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
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

            // While you can request any of these simultaneously, support for each of these varies greatly among devices.
            // Some platforms may not be able to be simultaneously provide all options, or it may depend on other features (e.g., camera facing direction).
            if (fpsMonitor != null)
            {
                fpsMonitor.Toast("If LightEstimation is enabled, the camera facing direction may not be changed depending on the device's capabilities.");
            }
        }

        /// <summary>
        /// Raises the dictionary id dropdown value changed event.
        /// </summary>
        public void OnDictionaryIdDropdownValueChanged(int result)
        {
            if ((int)dictionaryId != result)
            {
                dictionaryId = (ArUcoDictionary)result;
                dictionary = Objdetect.getPredefinedDictionary((int)dictionaryId);

                ResetObjectTransform();

                if (webCamTextureToMatHelper != null && webCamTextureToMatHelper.IsInitialized())
                    webCamTextureToMatHelper.Initialize();
            }
        }

        /// <summary>
        /// Raises the apply estimation P\pose toggle value changed event.
        /// </summary>
        public void OnApplyEstimationPoseToggleValueChanged()
        {
            applyEstimationPose = applyEstimationPoseToggle.isOn;
        }

        /// <summary>
        /// Raises the enable leap filter toggle value changed event.
        /// </summary>
        public void OnEnableLeapFilterToggleValueChanged()
        {
            enableLerpFilter = enableLerpFilterToggle.isOn;
        }

        /// <summary>
        /// Raises the change LightEstimation button click event.
        /// </summary>
        public void OnChangeLightEstimationButtonClick()
        {
            if (webCamTextureToMatHelper != null && webCamTextureToMatHelper.IsInitialized())
            {
                webCamTextureToMatHelper.requestedLightEstimation = (webCamTextureToMatHelper.requestedLightEstimation == LightEstimation.None)
                    ? LightEstimation.AmbientColor | LightEstimation.AmbientIntensity
                    : LightEstimation.None;
            }

            // While you can request any of these simultaneously, support for each of these varies greatly among devices.
            // Some platforms may not be able to be simultaneously provide all options, or it may depend on other features (e.g., camera facing direction).
            if (fpsMonitor != null)
            {
                fpsMonitor.Toast("If LightEstimation is enabled, the camera facing direction may not be changed depending on the device's capabilities.");
            }
        }

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
    }
}

#endif