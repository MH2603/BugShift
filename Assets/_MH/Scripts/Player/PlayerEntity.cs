using UnityEngine;

namespace MH
{
    public class PlayerEntity : SceneEntity
    {
        protected override void Init()
        {
            AddComponent(GetComponent<PlayerMovement>());

            base.Init();
        }
    }
}