using UnityEngine;
using UnityEngine.SceneManagement;

namespace ARFoundationWithOpenCVForUnityExample
{
    public class ShowLicense : MonoBehaviour
    {
        // Unity Lifecycle Methods
        private void Start()
        {

        }

        private void Update()
        {

        }

        // Public Methods
        public void OnBackButtonClick()
        {
            SceneManager.LoadScene("ARFoundationWithOpenCVForUnityExample");
        }
    }
}
