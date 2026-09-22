using UnityEngine;
namespace MH
{
    public class PlayerMovement : MonoBehaviour
    {
        #region FIELDS 

        [SerializeField] private float speed = 5f;
        
        #endregion

        #region PROPERTIES

        private CharacterController _unityChCtrl;
        #endregion


        #region UNITY API 

        void Start()
        {
            _unityChCtrl = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var moveDir = PlayerInputReader.Instance.GetMove();
            // test input
            // MHLogger.Log(moveDir.ToString());
            _unityChCtrl.Move(moveDir * speed * Time.deltaTime);
        }

        #endregion


    }
}