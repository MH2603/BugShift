using System;
using System.Collections.Generic;
using UnityEngine;

namespace MH
{
    public class SceneEntity : MonoBehaviour 
    {
        protected Dictionary<Type,IEntityComponent> componentDict;

        void Awake()
        {
            Init();
        }

        void Update()
        {
            Tick(Time.deltaTime);
        }

        protected virtual void Init()
        {
            foreach(var component in componentDict.Values){
                component.OnInit(this);
            }
        }

        public void AddComponent<T>(T component) where T : IEntityComponent
        {
            if (componentDict.ContainsKey(typeof(T)))
            {
                throw new Exception($"Component of type {typeof(T)} already exists");
            }
            componentDict.Add(typeof(T), component);
        }

        public T GetEntityComponent<T>() where T : IEntityComponent
        {
            if (!componentDict.ContainsKey(typeof(T)))
            {
                throw new Exception($"Component of type {typeof(T)} does not exist");
            }
            return (T)componentDict[typeof(T)];
        }

        public void RemoveComponent<T>() where T : IEntityComponent
        {
            if (!componentDict.ContainsKey(typeof(T)))
            {
                throw new Exception($"Component of type {typeof(T)} does not exist");
            }
            componentDict.Remove(typeof(T));
        }

        public virtual void Tick(float deltaTime){
            foreach(var component in componentDict.Values){
                component.Tick(deltaTime);
            }
        }
    }

    public interface IEntityComponent
    {
        void OnInit(SceneEntity entity);

        void Tick(float deltaTime);
    }
}