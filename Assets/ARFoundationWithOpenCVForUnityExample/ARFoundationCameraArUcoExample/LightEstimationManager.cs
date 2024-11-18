using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace ARFoundationWithOpenCVForUnityExample
{
    public class LightEstimationManager : MonoBehaviour
    {
        public ARCameraManager cameraManager;
        private Light _light;

        void Awake()
        {
            _light = GetComponent<Light>();
        }

        void OnEnable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived += FrameChanged;
            }
        }

        void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= FrameChanged;
            }
        }

        void FrameChanged(ARCameraFrameEventArgs args)
        {
            if (args.lightEstimation.averageBrightness.HasValue)
            {
                float? averageBrightness = args.lightEstimation.averageBrightness.Value;
                _light.intensity = averageBrightness.Value;
            }

            if (args.lightEstimation.averageColorTemperature.HasValue)
            {
                float? averageColorTemperature = args.lightEstimation.averageColorTemperature.Value;
                _light.colorTemperature = averageColorTemperature.Value;
            }

            if (args.lightEstimation.colorCorrection.HasValue)
            {
                Color? colorCorrection = args.lightEstimation.colorCorrection.Value;
                _light.color = colorCorrection.Value;
            }

            if (args.lightEstimation.ambientSphericalHarmonics.HasValue)
            {
                SphericalHarmonicsL2? sphericalHarmonics = args.lightEstimation.ambientSphericalHarmonics;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientProbe = sphericalHarmonics.Value;
            }

            if (args.lightEstimation.mainLightDirection.HasValue)
            {
                Vector3? mainLightDirection = args.lightEstimation.mainLightDirection;
                _light.transform.rotation = Quaternion.LookRotation(mainLightDirection.Value);
            }

            if (args.lightEstimation.mainLightColor.HasValue)
            {
                Color? mainLightColor = args.lightEstimation.mainLightColor;
                _light.color = mainLightColor.Value;
            }

            if (args.lightEstimation.averageMainLightBrightness.HasValue)
            {
                float? averageMainLightBrightness = args.lightEstimation.averageMainLightBrightness;
                _light.intensity = averageMainLightBrightness.Value;
            }
        }
    }
}