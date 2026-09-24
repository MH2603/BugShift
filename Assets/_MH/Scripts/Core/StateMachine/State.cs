

using System;

namespace MH
{

    public interface IStateHandler
    {
        void OnEnter();
        void OnExit();
        void Tick(float deltaTime);
    }
    
    public class StateHandler<T> : IStateHandler where T : Enum 
    {
        private T _state ;
        
        public T State => _state;
        protected Func<T> changeStateFunc;
        
        public StateHandler(T state, Func<T> changeStateFunc)
        {
            _state = state;
            this.changeStateFunc = changeStateFunc;
        }

        public virtual void OnEnter(){}

        public virtual void OnExit(){}
        public virtual void Tick(float deltaTime){}

    }
}