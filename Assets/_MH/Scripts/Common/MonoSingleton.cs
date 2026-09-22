using UnityEngine;

namespace MH
{
    /// <summary>
    /// Base class for <see cref="MonoBehaviour"/> singletons.
    /// The instance is located in the scene or, when none exists, created on demand, and it is kept
    /// alive across scene loads. Duplicate instances are destroyed automatically.
    /// </summary>
    /// <typeparam name="T">The concrete singleton type deriving from <see cref="MonoSingleton{T}"/>.</typeparam>
    /// <example>
    /// <code>
    /// public class AudioManager : MonoSingleton&lt;AudioManager&gt;
    /// {
    ///     protected override void Awake()
    ///     {
    ///         base.Awake();   // required: it wires up the singleton
    ///         // ...
    ///     }
    /// }
    ///
    /// AudioManager.Instance.Play("hit");
    /// </code>
    /// </example>
    public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoSingleton<T>
    {
        private static readonly object _lock = new object();

        private static T _instance;
        private static bool _isQuitting;

        /// <summary>
        /// The single instance of <typeparamref name="T"/>. Returns an existing instance found in the
        /// scene, or creates a new <see cref="GameObject"/> named after the type when there is none.
        /// Returns <c>null</c> while the application is quitting.
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_isQuitting)
                {
                    return null;
                }

                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = FindAnyObjectByType<T>();

                            if (_instance == null)
                            {
                                var go = new GameObject(typeof(T).Name);
                                _instance = go.AddComponent<T>();
                            }
                        }
                    }
                }

                return _instance;
            }
        }

        /// <summary>Whether a live instance currently exists. Reading this does not create one.</summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// Registers this component as the singleton and keeps it alive across scene loads.
        /// If you override this, you <b>must</b> call <c>base.Awake()</c>.
        /// </summary>
        protected virtual void Awake()
        {
            if (_instance == null)
            {
                _instance = (T)this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Debug.LogWarning($"[{typeof(T).Name}] A duplicate instance was found on '{name}' and will be destroyed.", this);
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Clears the cached instance when this object is destroyed, so a fresh one can be created later.
        /// If you override this, you <b>must</b> call <c>base.OnDestroy()</c>.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Prevents <see cref="Instance"/> from re-creating the singleton while the application shuts down.
        /// If you override this, you <b>must</b> call <c>base.OnApplicationQuit()</c>.
        /// </summary>
        protected virtual void OnApplicationQuit()
        {
            _isQuitting = true;
        }
    }
}
