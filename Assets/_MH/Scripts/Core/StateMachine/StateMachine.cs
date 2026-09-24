using System;
using System.Collections.Generic;

namespace MH
{
    public class StateMachine<T> where T : Enum
    {
        protected Dictionary<T,IStateHandler> stateMap;

        private T _currentState;

        public T CurrentState => _currentState;
        public event Action<T> EnterState;
        public event Action<T> ExitState;
        

        public bool ChangeState(T nextState)
        {
            if(!stateMap.ContainsKey(nextState)) return false;
            
            stateMap[_currentState].OnExit();
            _currentState = nextState;
            stateMap[nextState].OnEnter();
            
            return true;
        }

        public bool RegisterState(T state, IStateHandler handler)
        {
            if(stateMap.ContainsKey(state)) return false;
            stateMap.Add(state, handler);
            return true;
        }
        
    }

}