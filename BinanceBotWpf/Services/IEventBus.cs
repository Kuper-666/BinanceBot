using System;

namespace BinanceBotWpf.Services
{
    public interface IEventBus
    {
        void Publish<T> (T eventData);
        IDisposable Subscribe<T> (Action<T> handler);
    }

    public class EventBus : IEventBus
    {
        public void Publish<T> (T eventData)
        {
            OnEvent?.Invoke (eventData, typeof (T));
        }

        public IDisposable Subscribe<T> (Action<T> handler)
        {
            Action<object, Type> wrapped = (data, type) =>
            {
                if (type == typeof (T))
                    handler ((T)data);
            };
            OnEvent += wrapped;
            return new Subscription (() => OnEvent -= wrapped);
        }

        private event Action<object, Type> OnEvent;

        private class Subscription : IDisposable
        {
            private readonly Action _unsubscribe;
            public Subscription (Action unsubscribe) => _unsubscribe = unsubscribe;
            public void Dispose () => _unsubscribe ();
        }
    }
}
