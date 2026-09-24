using UnityEngine;
using UnityEngine.Pool;
namespace MH
{
    public class PlayerMovement : MonoBehaviour, IEntityComponent
    
    {
        #region FIELDS 

        [SerializeField] private float speed = 5f;
        
        #endregion

        #region PROPERTIES

        private CharacterController _unityChCtrl;

        public void OnInit(SceneEntity entity)
        {
            _unityChCtrl = GetComponent<CharacterController>();
        }

        public void Tick(float deltaTime)
        {
            var inputDir = PlayerInputReader.Instance.GetMove();
            Vector3 moveDir = new Vector3(inputDir.x, 0, inputDir.y);

            // test input
            // MHLogger.Log(moveDir.ToString());
            _unityChCtrl.Move(moveDir * speed * deltaTime);
        }
        #endregion


        #region UNITY API 

        #endregion


    }
}