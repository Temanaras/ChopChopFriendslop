using System;
using System.Collections.Generic;

namespace ChopChop.Core
{
    /// <summary>
    /// Minimal service registry. Populated once by the Bootstrap assembly and read
    /// by everything else, which is what lets Core stay dependency-free while still
    /// giving systems a way to find each other.
    ///
    /// Not thread-safe by design: all access happens on the Unity main thread.
    /// </summary>
    public static class ServiceLocator
    {
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();

        public static void Register<T>(T service) where T : class
        {
            if (service == null)
                throw new ArgumentNullException(nameof(service));

            Services[typeof(T)] = service;
        }

        public static bool TryGet<T>(out T service) where T : class
        {
            if (Services.TryGetValue(typeof(T), out object found))
            {
                service = (T)found;
                return true;
            }

            service = null;
            return false;
        }

        public static T Get<T>() where T : class
        {
            if (!TryGet(out T service))
                throw new InvalidOperationException($"No service registered for {typeof(T).Name}.");

            return service;
        }

        public static void Unregister<T>() where T : class => Services.Remove(typeof(T));

        public static void Clear() => Services.Clear();
    }
}
