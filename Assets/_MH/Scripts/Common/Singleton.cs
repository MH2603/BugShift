using System;

namespace MH
{
    /// <summary>
    /// Base class for plain (non-<see cref="UnityEngine.MonoBehaviour"/>) singletons.
    /// The single instance is created lazily on first access and creation is thread-safe.
    /// </summary>
    /// <typeparam name="T">
    /// The concrete singleton type. It must derive from <see cref="Singleton{T}"/> and expose a
    /// public parameterless constructor.
    /// </typeparam>
    /// <example>
    /// <code>
    /// public class GameSettings : Singleton&lt;GameSettings&gt;
    /// {
    ///     public int MasterVolume { get; set; } = 80;
    ///
    ///     protected override void OnInit()
    ///     {
    ///         // Optional: runs once, right after the instance is constructed.
    ///     }
    /// }
    ///
    /// GameSettings.Instance.MasterVolume = 50;
    /// </code>
    /// </example>
    public abstract class Singleton<T> where T : Singleton<T>, new()
    {
        // The Lazy<T>(Func<T>) constructor defaults to LazyThreadSafetyMode.ExecutionAndPublication,
        // so the factory runs exactly once even if Instance is touched from several threads.
        private static readonly Lazy<T> _instance = new Lazy<T>(CreateInstance);

        /// <summary>The single, lazily-created instance of <typeparamref name="T"/>.</summary>
        public static T Instance => _instance.Value;

        /// <summary>
        /// Whether the instance has already been created. Reading this does not trigger creation.
        /// </summary>
        public static bool IsInitialized => _instance.IsValueCreated;

        private static T CreateInstance()
        {
            T instance = new T();
            instance.OnInit();
            return instance;
        }

        /// <summary>
        /// Called once, immediately after the instance is constructed.
        /// Override for initialization that needs a fully constructed object.
        /// </summary>
        protected virtual void OnInit() { }
    }
}
