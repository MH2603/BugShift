using UnityEngine;
using UnityEngine.InputSystem;

namespace MH
{
    public static class InputActionNames
    {
        public const string Move = "Move";
    }

    public class PlayerInputReader : Singleton<PlayerInputReader>
    {
        InputAction moveAction;

        protected override void OnInit()
        {
            base.OnInit();

            moveAction = InputSystem.actions.FindAction(InputActionNames.Move);
        }

        public Vector2 GetMove()
        {
            return moveAction.ReadValue<Vector2>();
        }

    }
}
