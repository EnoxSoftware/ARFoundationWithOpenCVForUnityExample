using UnityEngine;
using UnityEngine.SceneManagement;

namespace ARFoundationWithOpenCVForUnityExample
{

    public class ShowLicense : MonoBehaviour
    {

        public void OnBackButtonClick()
        {
            SceneManager.LoadScene("ARFoundationWithOpenCVForUnityExample");
        }
    }
}
