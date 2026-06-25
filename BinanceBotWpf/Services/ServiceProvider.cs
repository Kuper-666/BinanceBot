using System;
using System.Collections.Generic;

namespace BinanceBotWpf.Services
{
    public class ServiceRegistry
    {
        private readonly Dictionary<Type, object> _services = new ();
        private readonly Dictionary<Type, Func<object>> _factories = new ();

        public void Register<T> (T service) where T : class
        {
            _services[typeof (T)] = service;
        }

        public void RegisterFactory<T> (Func<T> factory) where T : class
        {
            _factories[typeof (T)] = () => factory ();
        }

        public T Get<T> () where T : class
        {
            if (_services.TryGetValue (typeof (T), out var service))
                return (T)service;

            if (_factories.TryGetValue (typeof (T), out var factory))
            {
                var instance = (T)factory ();
                _services[typeof (T)] = instance;
                return instance;
            }

            throw new InvalidOperationException ($"Service {typeof (T).Name} not registered.");
        }

        public bool TryGet<T> (out T? service) where T : class
        {
            if (_services.TryGetValue (typeof (T), out var obj))
            {
                service = (T)obj;
                return true;
            }
            service = null;
            return false;
        }
    }
}
